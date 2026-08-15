using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace RDPVault;

public sealed class SessionManager : IDisposable
{
    public static SessionManager Current { get; } = new();

    public string VaultPath { get; }
    public string ExeRoot { get; }
    public string? PendingLaunchId { get; private set; }

    public VaultFile? File { get; private set; }
    public byte[]? Master { get; private set; }
    public VaultPayload? Payload { get; private set; }

    public bool IsUnlocked => Master != null;

    public string DisplayRoot => Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "RDPVault.exe");

    private readonly Mutex _singleMutex;
    private readonly DispatcherTimer _lockTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _usbTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    private DateTime _lastActivity = DateTime.UtcNow;
    private bool _exiting;

    public event Action? Locked;
    public event Action? UsbRemoved;
    public event Action? ShowRequested;

    private SessionManager()
    {
        string exeDir = AppContext.BaseDirectory;
        ExeRoot = Path.GetPathRoot(exeDir) ?? "C:\\";
        VaultPath = Path.Combine(exeDir, "vault.rdpv");

        var args = Environment.GetCommandLineArgs();
        string pipeMsg = "SHOW";
        if (args.Length >= 3 && args[1] == "--launch" && System.IO.File.Exists(args[2]))
        {
            try
            {
                string content = System.IO.File.ReadAllText(args[2]);
                if (content.StartsWith("TargetProfileId="))
                {
                    string target = content.Substring(16).Trim();
                    PendingLaunchId = target;
                    pipeMsg = $"LAUNCH:{target}";
                }
            }
            catch { }
        }

        bool createdNew;
        _singleMutex = new Mutex(true, @"Local\RDPVault_SingleInstance", out createdNew);
        if (!createdNew)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", "RDPVault_Show", PipeDirection.Out);
                client.Connect(500);
                using var w = new StreamWriter(client) { AutoFlush = true };
                w.Write(pipeMsg);
            }
            catch { }
            Environment.Exit(0);
        }

        _lockTimer.Tick += (_, _) => CheckAutoLock();
        _usbTimer.Tick += (_, _) => CheckUsbStillPresent();
        _ = Task.Run(PipeLoop);
    }

    public void StartTimers()
    {
        _lockTimer.Start();
        _usbTimer.Start();
    }

    public void LoadFile()
        => File = System.Text.Json.JsonSerializer.Deserialize<VaultFile>(
               System.IO.File.ReadAllText(VaultPath), VaultCrypto.JsonOpts)
               ?? throw new InvalidDataException("Vault file is empty.");

    public void CreateNew(string password, VaultPayload payload)
    {
        File = VaultCrypto.CreateVault(password, payload, VaultPath);
        (Master, Payload) = (null, payload);
        UnlockWithPassword(password);
    }

    public void UnlockWithPassword(string password)
    {
        LoadFile();
        try
        {
            (Master, Payload) = VaultCrypto.Open(File!, password);
            SecurityEnforcer.ClearFailedAttempts();
        }
        catch (InvalidDataException)
        {
            int maxFails = Payload?.Settings.SelfDestructFailedAttempts ?? 20;
            int window = Payload?.Settings.SelfDestructWindowMinutes ?? 60;
            SecurityEnforcer.RecordFailedAttempt(maxFails, window, VaultPath);
            throw;
        }
        AfterUnlock();
    }

    public async Task<bool> UnlockWithHelloAsync()
    {
        LoadFile();
        string machineId = VaultCrypto.CurrentMachineId();
        SealEntry? seal = File!.Seals.FirstOrDefault(s => s.MachineId == machineId);
        if (seal == null || string.IsNullOrEmpty(seal.KeyId)) return false;

        byte[]? signature = await WindowsHello.GetSignatureAsync(seal.KeyId);
        if (signature == null) return false;

        byte[]? master = VaultCrypto.UnsealTpm(File!, seal, signature);
        if (master == null) return false;

        try { Payload = VaultCrypto.OpenPayload(File!, master); }
        catch { System.Security.Cryptography.CryptographicOperations.ZeroMemory(master); return false; }

        Master = master;
        AfterUnlock();
        return true;
    }

    public bool HelloSealAvailable()
    {
        try
        {
            if (!System.IO.File.Exists(VaultPath)) return false;
            LoadFile();
            string machineId = VaultCrypto.CurrentMachineId();
            return File!.Seals.Any(s => s.MachineId == machineId && !string.IsNullOrEmpty(s.KeyId));
        }
        catch { return false; }
    }

    public void Save(string? newPassword = null)
    {
        if (File == null || Master == null || Payload == null) return;
        VaultCrypto.Save(File, Master, Payload, VaultPath, newPassword);
    }

    public void Lock(bool killSessions)
    {
        if (Master != null) System.Security.Cryptography.CryptographicOperations.ZeroMemory(Master);
        Master = null;
        Payload = null;
        _lastActivity = DateTime.UtcNow;
        Locked?.Invoke();
        if (killSessions) RdpLauncher.KillAll();
        TraceCleaner.Sweep();
    }

    private void AfterUnlock()
    {
        _lastActivity = DateTime.UtcNow;
        TraceCleaner.Sweep();
        StartTimers();

        if (!string.IsNullOrEmpty(PendingLaunchId) && Payload != null)
        {
            var p = Payload.Profiles.FirstOrDefault(x => x.Id == PendingLaunchId);
            if (p != null) Dispatcher.UIThread.InvokeAsync(() => RdpLauncher.Launch(p));
            PendingLaunchId = null;
        }
    }

    public void Touch() => _lastActivity = DateTime.UtcNow;

    private void CheckAutoLock()
    {
        if (!IsUnlocked || Payload == null) return;
        int minutes = Math.Max(1, Payload.Settings.LockMinutes);
        if (DateTime.UtcNow - _lastActivity >= TimeSpan.FromMinutes(minutes))
            Lock(killSessions: false);
    }

    private void CheckUsbStillPresent()
    {
        if (_exiting) return;
        try
        {
            if (!Directory.Exists(AppContext.BaseDirectory)) throw new IOException("root gone");
            if (!System.IO.File.Exists(Environment.ProcessPath)) throw new IOException("exe gone");
        }
        catch
        {
            _exiting = true;
            UsbRemoved?.Invoke();
            bool kill = Payload?.Settings.KillSessionsOnUsbRemoval ?? true;
            Lock(killSessions: kill);
            if (kill) TraceCleaner.DeepSweep();
            Environment.Exit(0);
        }
    }

    public async Task<bool> EnableHelloSealAsync()
    {
        if (File == null || Master == null || Payload == null) return false;
        var enroll = await WindowsHello.EnrollAndSignAsync();
        if (enroll == null) return false;

        var seal = VaultCrypto.SealTpm(Master, enroll.Value.KeyId, enroll.Value.Signature, File);
        var seals = File.Seals.Where(s => s.MachineId != seal.MachineId).ToList();
        seals.Add(seal);
        VaultCrypto.Save(File, Master, Payload, VaultPath, newPassword: null, newSeals: seals);
        return true;
    }

    public void DisableHelloSeal()
    {
        if (File == null || Master == null || Payload == null) return;
        string id = VaultCrypto.CurrentMachineId();
        var seals = File.Seals.Where(s => s.MachineId != id).ToList();
        VaultCrypto.Save(File, Master, Payload, VaultPath, newPassword: null, newSeals: seals);
    }

    public void ChangePassword(string oldPassword, string newPassword)
    {
        LoadFile();
        _ = VaultCrypto.Open(File!, oldPassword);
        VaultCrypto.Save(File!, Master!, Payload!, VaultPath, newPassword, newSeals: new System.Collections.Generic.List<SealEntry>());
    }

    private async Task PipeLoop()
    {
        while (!_exiting)
        {
            try
            {
                using var server = new NamedPipeServerStream("RDPVault_Show", PipeDirection.In);
                await server.WaitForConnectionAsync();
                using var r = new StreamReader(server);
                string? msg = await r.ReadToEndAsync();
                if (msg == "SHOW")
                {
                    Touch();
                    Dispatcher.UIThread.InvokeAsync(() => ShowRequested?.Invoke());
                }
                else if (msg != null && msg.StartsWith("LAUNCH:"))
                {
                    string target = msg.Substring(7);
                    Touch();
                    if (IsUnlocked && Payload != null)
                    {
                        var p = Payload.Profiles.FirstOrDefault(x => x.Id == target);
                        if (p != null) Dispatcher.UIThread.InvokeAsync(() => RdpLauncher.Launch(p));
                    }
                    else
                    {
                        PendingLaunchId = target;
                        Dispatcher.UIThread.InvokeAsync(() => ShowRequested?.Invoke());
                    }
                }
            }
            catch { }
        }
    }

    public void Dispose()
    {
        _exiting = true;
        try { _singleMutex.ReleaseMutex(); } catch { }
        _lockTimer.Stop();
        _usbTimer.Stop();
    }
}
