using System.IO;
using System.IO.Pipes;
using Avalonia.Threading;

namespace RDPVault;

/// <summary>
/// Holds the unlocked vault state in memory: master key + payload.
/// Owns the auto-lock timer and the USB-removal watcher.
/// A named mutex guarantees only one instance runs per vault.
/// </summary>
public sealed class SessionManager : IDisposable
{
    public static SessionManager Current { get; } = new();

    public string VaultPath { get; }
    public string ExeRoot { get; }   // e.g. E:\  (USB stick root, or a folder on a disk)

    public VaultFile? File { get; private set; }
    public byte[]? Master { get; private set; }
    public VaultPayload? Payload { get; private set; }

    public bool IsUnlocked => Master != null;

    public string DisplayRoot => Path.GetFileNameWithoutExtension(
        Environment.ProcessPath ?? "RDPVault.exe");

    private readonly Mutex _singleMutex;
    private readonly DispatcherTimer _lockTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _usbTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    private DateTime _lastActivity = DateTime.UtcNow;
    private bool _exiting;

    public event Action? Locked;             // vault re-locked (timer / USB / manual)
    public event Action? UsbRemoved;

    private SessionManager()
    {
        string exeDir = AppContext.BaseDirectory;
        ExeRoot = Path.GetPathRoot(exeDir) ?? "C:\\";
        VaultPath = Path.Combine(exeDir, "vault.rdpv");

        bool createdNew;
        _singleMutex = new Mutex(true, @"Local\RDPVault_SingleInstance", out createdNew);
        if (!createdNew)
        {
            // Tell the running instance to surface, then exit.
            try
            {
                using var client = new NamedPipeClientStream(".", "RDPVault_Show", PipeDirection.Out);
                client.Connect(500);
                using var w = new StreamWriter(client) { AutoFlush = true };
                w.Write("SHOW");
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

    // ---------------- unlock / lock / save ----------------

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

    /// <summary>
    /// Windows Hello quick unlock:
    /// 1. find this PC's seal in the vault, 2. make Windows prompt finger/face/PIN
    /// and verify the key fingerprint, 3. unseal the master key, 4. unlock.
    /// Returns false when unsupported, no seal, or the user fails verification.
    /// </summary>
    public async Task<bool> UnlockWithHelloAsync()
    {
        LoadFile();
        string machineId = VaultCrypto.CurrentMachineId();
        SealEntry? seal = File!.Seals.FirstOrDefault(s => s.MachineId == machineId);
        if (seal == null || string.IsNullOrEmpty(seal.KeyId)) return false;

        bool verified = await WindowsHello.VerifyAsync(seal.KeyId);
        if (!verified) return false;

        byte[]? master = VaultCrypto.UnsealLocal(File!, out _);
        if (master == null) return false;

        try { Payload = VaultCrypto.OpenPayload(File!, master); }
        catch { System.Security.Cryptography.CryptographicOperations.ZeroMemory(master); return false; }

        Master = master;
        AfterUnlock();
        return true;
    }

    /// <summary>True if this PC has a usable quick-unlock seal AND Hello is available.</summary>
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
        TraceCleaner.Sweep();      // hygiene: clear residue possibly left by earlier crash
        StartTimers();
    }

    // ---------------- timers ----------------

    public void Touch() => _lastActivity = DateTime.UtcNow;

    private void CheckAutoLock()
    {
        if (!IsUnlocked || Payload == null) return;
        int minutes = Math.Max(1, Payload.Settings.LockMinutes);
        if (DateTime.UtcNow - _lastActivity >= TimeSpan.FromMinutes(minutes))
            Lock(killSessions: false);   // keep live RDP windows; only lock the vault
    }

    private void CheckUsbStillPresent()
    {
        if (_exiting) return;
        try
        {
            // The app folder must still exist (works for USB sticks and fixed folders).
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

    // ---------------- quick-unlock seals ----------------

    /// <summary>Enable Hello quick unlock on THIS PC (prompt). Vault must be unlocked.</summary>
    public async Task<bool> EnableHelloSealAsync()
    {
        if (File == null || Master == null || Payload == null) return false;
        string? keyId = await WindowsHello.EnrollAsync();
        if (keyId == null) return false;

        var seal = VaultCrypto.SealLocal(Master);
        seal.KeyId = keyId;
        var seals = File.Seals.Where(s => s.MachineId != seal.MachineId).ToList();
        seals.Add(seal);
        VaultCrypto.Save(File, Master, Payload, VaultPath, newPassword: null, newSeals: seals);
        return true;
    }

    /// <summary>Remove this PC's quick-unlock seal (password needed from now on).</summary>
    public void DisableHelloSeal()
    {
        if (File == null || Master == null || Payload == null) return;
        string id = VaultCrypto.CurrentMachineId();
        var seals = File.Seals.Where(s => s.MachineId != id).ToList();
        VaultCrypto.Save(File, Master, Payload, VaultPath, newPassword: null, newSeals: seals);
    }

    /// <summary>Change master password; also invalidates every quick-unlock seal.</summary>
    public void ChangePassword(string oldPassword, string newPassword)
    {
        LoadFile();
        _ = VaultCrypto.Open(File!, oldPassword);   // throws InvalidDataException when wrong
        // Password change = possible compromise → wipe all quick-unlock seals.
        VaultCrypto.Save(File!, Master!, Payload!, VaultPath, newPassword, newSeals: new List<SealEntry>());
    }

    // ---------------- show-again pipe ----------------

    private async Task PipeLoop()
    {
        while (!_exiting)
        {
            try
            {
                using var server = new NamedPipeServerStream("RDPVault_Show", PipeDirection.In);
                await server.WaitForConnectionAsync();
                using var r = new StreamReader(server);
                if (await r.ReadToEndAsync() == "SHOW") Touch();
                ShowRequested?.Invoke();
            }
            catch { }
        }
    }

    public event Action? ShowRequested;

    public void Dispose()
    {
        _exiting = true;
        try { _singleMutex.ReleaseMutex(); } catch { }
        _lockTimer.Stop();
        _usbTimer.Stop();
    }
}
