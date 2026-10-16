using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace RDPVault;

public sealed class SessionManager : IDisposable
{
    public static SessionManager Current { get; } = new();

    public string VaultPath { get; }
    public string? PendingLaunchId { get; private set; }

    public VaultFile? File { get; private set; }
    public byte[]? Master { get; private set; }
    public VaultPayload? Payload { get; private set; }

    public bool IsUnlocked => Master != null;
    public bool VaultExists => System.IO.File.Exists(VaultPath);

    public bool UnlockedViaRecovery { get; private set; }

    private DispatcherTimer? _lockTimer;
    private DispatcherTimer? _usbTimer;
    private DateTime _lastActivity = DateTime.UtcNow;
    private bool _exiting;

    private const int UsbMissesBeforeShutdown = 3;
    private int _usbMisses;

    public event Action? Locked;
    public event Action? Unlocked;
    public event Action? UsbRemoved;
    public event Action? ShowRequested;
    public event Action? VaultDestroyed;
    public event Action<string>? Notice;
    public event Action<RdpProfile, bool>? LaunchRequested;

    private SessionManager()
    {
        VaultPath = AppPaths.VaultPath;
        SecurityEnforcer.RemoveLegacyState();

        ParseCommandLine();
        _ = Task.Run(PipeLoop);
    }

    private string ParseCommandLine()
    {
        var args = Environment.GetCommandLineArgs();
        for (int i = 1; i < args.Length - 1; i++)
        {
            if (!string.Equals(args[i], "--launch", StringComparison.OrdinalIgnoreCase)) continue;

            string value = args[i + 1].Trim().Trim('"', '\'');
            string? id = null;

            if (System.IO.File.Exists(value))
            {
                try
                {
                    string content = System.IO.File.ReadAllText(value).Trim();
                    const string prefix = "TargetProfileId=";
                    if (content.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        id = content.Substring(prefix.Length).Trim().Trim('"', '\'');
                    else
                        id = content;
                }
                catch { }
            }
            else
            {
                id = value;
            }

            if (!string.IsNullOrWhiteSpace(id))
            {
                PendingLaunchId = id;
                return "LAUNCH:" + id;
            }
        }
        return "SHOW";
    }

    public RdpProfile? FindProfile(string? target)
    {
        if (Payload?.Profiles == null || string.IsNullOrWhiteSpace(target)) return null;

        target = target.Trim().Trim('"', '\'');
        string normTarget = target.Replace("-", "").Trim();

        var match = Payload.Profiles.FirstOrDefault(p => string.Equals(p.Id, target, StringComparison.OrdinalIgnoreCase));
        if (match != null) return match;

        if (normTarget.Length >= 8)
        {
            match = Payload.Profiles.FirstOrDefault(p =>
                string.Equals((p.Id ?? "").Replace("-", "").Trim(), normTarget, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;
        }

        match = Payload.Profiles.FirstOrDefault(p =>
            string.Equals(p.Name, target, StringComparison.OrdinalIgnoreCase));
        if (match != null) return match;

        match = Payload.Profiles.FirstOrDefault(p =>
            string.Equals(p.Host, target, StringComparison.OrdinalIgnoreCase));
        return match;
    }


    private static void OnUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }

    private void StartTimers() => OnUi(() =>
    {
        if (_lockTimer == null)
        {
            _lockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _lockTimer.Tick += (_, _) => CheckAutoLock();
        }
        if (_usbTimer == null)
        {
            _usbTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _usbTimer.Tick += (_, _) => CheckUsbStillPresent();
        }
        _lockTimer.Start();
        _usbTimer.Start();
    });


    public void LoadFile()
    {
        string json = System.IO.File.ReadAllText(VaultPath);
        File = System.Text.Json.JsonSerializer.Deserialize(json, VaultJsonContext.Default.VaultFile)
               ?? throw new InvalidDataException("This vault file is empty or unreadable.");
        if (File.Kdf is null) File.Kdf = new VaultFile.KdfParams();
        if (File.Policy is null) File.Policy = new VaultPolicy();
        if (File.Fails is null) File.Fails = new FailState();
        if (File.Seals is null) File.Seals = new List<SealEntry>();
    }

    public string CreateNew(string password, VaultPayload payload)
    {
        File = VaultCrypto.CreateVault(password, payload, VaultPath, out string recoveryCode);
        UnlockWithPassword(password);
        return recoveryCode;
    }


    public TimeSpan CooldownRemaining()
    {
        try
        {
            if (File != null) return SecurityEnforcer.CooldownRemaining(File);
            if (!VaultExists) return TimeSpan.Zero;
            string json = System.IO.File.ReadAllText(VaultPath);
            var peek = System.Text.Json.JsonSerializer.Deserialize(json, VaultJsonContext.Default.VaultFile);
            return peek == null ? TimeSpan.Zero : SecurityEnforcer.CooldownRemaining(peek);
        }
        catch { return TimeSpan.Zero; }
    }

    public void UnlockWithPassword(string password)
    {
        LoadFile();

        TimeSpan cooldown = SecurityEnforcer.CooldownRemaining(File!);
        if (cooldown > TimeSpan.Zero)
            throw new InvalidOperationException(
                $"Too many failed attempts. Try again in {Math.Ceiling(cooldown.TotalSeconds)} seconds.");

        try
        {
            (Master, Payload) = VaultCrypto.Open(File!, password);
        }
        catch (InvalidDataException)
        {
            HandleFailedAttempt();
            throw;
        }

        UnlockedViaRecovery = false;

        SecurityEnforcer.ClearFailures(File!, VaultPath);
        AfterUnlock();
    }

    public bool UnlockWithRecoveryCode(string typedCode)
    {
        LoadFile();

        TimeSpan cooldown = SecurityEnforcer.CooldownRemaining(File!);
        if (cooldown > TimeSpan.Zero)
            throw new InvalidOperationException(
                $"Too many failed attempts. Try again in {Math.Ceiling(cooldown.TotalSeconds)} seconds.");

        if (File!.Recovery == null)
            throw new InvalidOperationException(
                "This vault has no Recovery Code. Open Settings after unlocking to create one.");

        var opened = VaultCrypto.OpenWithRecoveryCode(File, typedCode);
        if (opened == null)
        {
            HandleFailedAttempt();
            return false;
        }

        (Master, Payload) = opened.Value;
        UnlockedViaRecovery = true;
        SecurityEnforcer.ClearFailures(File, VaultPath);
        AfterUnlock();
        return true;
    }

    private void HandleFailedAttempt()
    {
        var outcome = SecurityEnforcer.RecordFailure(File!, VaultPath);
        if (!outcome.VaultDestroyed) return;

        Master = null;
        Payload = null;
        File = null;
        OnUi(() => VaultDestroyed?.Invoke());
    }

    public async Task<bool> UnlockWithHelloAsync()
    {
        if (File == null) LoadFile();
        string machineId = VaultCrypto.CurrentMachineId();
        SealEntry? seal = File!.Seals.FirstOrDefault(s => s.MachineId == machineId && !string.IsNullOrEmpty(s.KeyId));
        if (seal == null) return false;

        byte[]? signature = await WindowsHello.GetSignatureAsync(seal.KeyId);
        if (signature == null) return false;

        var vaultFile = File;
        var (master, payload) = await Task.Run<(byte[]?, VaultPayload?)>(() =>
        {
            byte[]? m = VaultCrypto.UnsealTpm(vaultFile, seal, signature);
            CryptographicOperations.ZeroMemory(signature);
            if (m == null) return (null, null);

            try
            {
                var p = VaultCrypto.OpenPayload(vaultFile, m);
                return (m, p);
            }
            catch
            {
                CryptographicOperations.ZeroMemory(m);
                return (null, null);
            }
        });

        if (master == null || payload == null) return false;

        Master = master;
        Payload = payload;
        UnlockedViaRecovery = false;
        SecurityEnforcer.ClearFailures(File, VaultPath);
        AfterUnlock();
        return true;
    }

    public bool HelloSealAvailable()
    {
        try
        {
            string machineId = VaultCrypto.CurrentMachineId();
            if (File != null)
                return File.Seals.Any(s => s.MachineId == machineId && !string.IsNullOrEmpty(s.KeyId));

            if (!VaultExists) return false;
            string json = System.IO.File.ReadAllText(VaultPath);
            var peek = System.Text.Json.JsonSerializer.Deserialize(json, VaultJsonContext.Default.VaultFile);
            return peek?.Seals.Any(s => s.MachineId == machineId && !string.IsNullOrEmpty(s.KeyId)) ?? false;
        }
        catch { return false; }
    }

    private void AfterUnlock()
    {
        Touch();
        ApplySweepConfig();
        _ = Task.Run(TraceCleaner.Sweep);
        RdpLauncher.ApplyPendingCertUpdates(Payload);
        StartTimers();
        OnUi(() => Unlocked?.Invoke());

        if (Payload?.Settings.WarnIfDriveNotEncrypted == true)
        {
            string? root = Path.GetPathRoot(VaultPath);
            if (!string.IsNullOrEmpty(root))
            {
                string drive = root.TrimEnd('\\', '/');
                _ = Task.Run(() =>
                {
                    var status = SecurityEnforcer.CheckDrive(drive);
                    if (status == BitLockerStatus.NotEncrypted)
                        OnUi(() => Notice?.Invoke(
                            $"Warning: drive {drive} is not encrypted. If this device is lost, only your master password protects the vault file."));
                });
            }
        }

        if (!string.IsNullOrEmpty(PendingLaunchId) && Payload != null)
        {
            string target = PendingLaunchId;
            PendingLaunchId = null;
            var p = FindProfile(target);
            if (p != null)
            {
                OnUi(async () =>
                {
                    await Task.Delay(250);
                    LaunchRequested?.Invoke(p, true);
                });
            }
            else OnUi(() => Notice?.Invoke("That shortcut points at a profile that no longer exists."));
        }
    }

    private void ApplySweepConfig()
    {
        var settings = Payload?.Settings;
        TraceCleaner.Configure(
            settings?.SweepScope ?? SweepScope.OwnHostsOnly,
            Payload?.Profiles.Select(p => p.Host) ?? Enumerable.Empty<string>());
    }


    public void Save(string? newPassword = null)
    {
        if (File == null || Master == null || Payload == null)
            throw new InvalidOperationException("The vault is locked - unlock it before saving changes.");

        VaultCrypto.Save(File, Master, Payload, VaultPath, newPassword);
        ApplySweepConfig();
    }

    public bool TrySave(out string? error)
    {
        try { Save(); error = null; return true; }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    public string RegenerateRecoveryCode()
    {
        if (File == null || Master == null || Payload == null)
            throw new InvalidOperationException("The vault is locked.");

        string? code = null;
        VaultCrypto.Save(File, Master, Payload, VaultPath,
                         regenerateRecovery: true, recoveryCodeOut: c => code = c);
        return code ?? throw new InvalidOperationException("Could not generate a Recovery Code.");
    }

    public bool HasRecoveryCode => File?.Recovery != null;

    public void ChangePassword(string? oldPassword, string newPassword)
    {
        if (File == null || Master == null || Payload == null)
            throw new InvalidOperationException("The vault is locked.");
        if (newPassword.Length < 10)
            throw new ArgumentException("The new master password must be at least 10 characters.");

        if (!UnlockedViaRecovery)
        {
            if (string.IsNullOrEmpty(oldPassword))
                throw new ArgumentException("Enter your current master password.");

            byte[] verifyMaster;
            VaultPayload verifyPayload;
            try { (verifyMaster, verifyPayload) = VaultCrypto.Open(File, oldPassword); }
            catch (InvalidDataException) { throw new InvalidDataException("The current password is not correct."); }

            CryptographicOperations.ZeroMemory(verifyMaster);
            _ = verifyPayload;
        }

        VaultCrypto.Save(File, Master, Payload, VaultPath, newPassword, newSeals: new List<SealEntry>());

        UnlockedViaRecovery = false;
    }


    public void Lock(bool killSessions)
    {
        bool deep = Payload?.Settings.DeepSweep == true;

        RdpLauncher.SnapshotActiveCertPins();

        if (Master != null) CryptographicOperations.ZeroMemory(Master);
        Master = null;
        Payload = null;
        UnlockedViaRecovery = false;
        Touch();

        OnUi(() => Locked?.Invoke());
        if (killSessions) RdpLauncher.KillAll();

        _ = Task.Run(() =>
        {
            if (deep) TraceCleaner.DeepSweep(); else TraceCleaner.Sweep();
            TraceCleaner.ForgetHosts();
        });
    }

    public void Touch() => _lastActivity = DateTime.UtcNow;

    public TimeSpan IdleRemaining()
    {
        int minutes = Math.Clamp(Payload?.Settings.LockMinutes ?? 60, 1, 1440);
        TimeSpan left = TimeSpan.FromMinutes(minutes) - (DateTime.UtcNow - _lastActivity);
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    private void CheckAutoLock()
    {
        if (!IsUnlocked || Payload == null) return;
        if (IdleRemaining() == TimeSpan.Zero) Lock(killSessions: false);
    }

    private void CheckUsbStillPresent()
    {
        if (_exiting) return;

        bool present;
        try
        {
            present = Directory.Exists(AppPaths.ExeDir);
            string? exe = Environment.ProcessPath;
            if (present && exe != null) present = System.IO.File.Exists(exe);
        }
        catch { present = false; }

        if (present) { _usbMisses = 0; return; }
        if (++_usbMisses < UsbMissesBeforeShutdown) return;

        _exiting = true;
        bool kill = Payload?.Settings.KillSessionsOnUsbRemoval ?? true;
        OnUi(() => UsbRemoved?.Invoke());

        _ = Task.Run(async () =>
        {
            try
            {
                Lock(killSessions: kill);
                if (kill) TraceCleaner.DeepSweep();
            }
            catch { }
            await Task.Delay(2500);
            Environment.Exit(0);
        });
    }


    public async Task<HelloEnrollResult> EnableHelloSealAsync()
    {
        if (File == null || Master == null || Payload == null) return HelloEnrollResult.NotSupported;

        var (result, keyId, signature) = await WindowsHello.EnrollAndSignAsync();
        if (result != HelloEnrollResult.Success) return result;

        try
        {
            var seal = VaultCrypto.SealTpm(Master, keyId, signature, File);

            byte[]? proof = VaultCrypto.UnsealTpm(File, seal, signature);
            if (proof == null) return HelloEnrollResult.SignatureNotReproducible;
            CryptographicOperations.ZeroMemory(proof);

            var seals = File.Seals.Where(s => s.MachineId != seal.MachineId).ToList();
            seals.Add(seal);
            VaultCrypto.Save(File, Master, Payload, VaultPath, newSeals: seals);
            return HelloEnrollResult.Success;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signature);
        }
    }

    public void DisableHelloSeal()
    {
        if (File == null || Master == null || Payload == null)
            throw new InvalidOperationException("The vault is locked.");
        string id = VaultCrypto.CurrentMachineId();
        var seals = File.Seals.Where(s => s.MachineId != id).ToList();
        VaultCrypto.Save(File, Master, Payload, VaultPath, newSeals: seals);
    }


    private async Task PipeLoop()
    {
        while (!_exiting)
        {
            try
            {
                var server = new NamedPipeServerStream(
                    "RDPVault_Show",
                    PipeDirection.In,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync();
                _ = Task.Run(() => HandlePipeClientAsync(server));
            }
            catch
            {
                if (!_exiting) await Task.Delay(200);
            }
        }
    }

    private async Task HandlePipeClientAsync(NamedPipeServerStream server)
    {
        using (server)
        {
            try
            {
                using var r = new StreamReader(server);
                string msg = (await r.ReadToEndAsync() ?? "").Trim();

                Touch();
                if (msg.StartsWith("LAUNCH:", StringComparison.Ordinal))
                {
                    string target = msg.Substring(7);
                    if (!IsUnlocked || Payload == null)
                    {
                        OnUi(() => ShowRequested?.Invoke());
                    }

                    if (IsUnlocked && Payload != null)
                    {
                        var p = FindProfile(target);
                        if (p != null)
                        {
                            if (LaunchRequested != null) OnUi(() => LaunchRequested.Invoke(p, true));
                            else OnUi(() => RdpLauncher.Launch(p));
                        }
                        else OnUi(() => Notice?.Invoke("That shortcut points at a profile that no longer exists."));
                    }
                    else
                    {
                        PendingLaunchId = target;
                    }
                }
                else
                {
                    OnUi(() => ShowRequested?.Invoke());
                }
            }
            catch { }
        }
    }

    public void Dispose()
    {
        if (_exiting) return;
        _exiting = true;

        RdpLauncher.SnapshotActiveCertPins();

        bool deep = Payload?.Settings.DeepSweep == true;
        if (Master != null) CryptographicOperations.ZeroMemory(Master);
        Master = null;
        Payload = null;
        UnlockedViaRecovery = false;

        OnUi(() =>
        {
            _lockTimer?.Stop();
            _usbTimer?.Stop();
        });

        _ = Task.Run(() =>
        {
            if (deep) TraceCleaner.DeepSweep(); else TraceCleaner.Sweep();
            TraceCleaner.ForgetHosts();
        });
    }
}
