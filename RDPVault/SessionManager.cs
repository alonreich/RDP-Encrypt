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

    // Timers are created lazily ON THE UI THREAD - see StartTimers (issue #18a).
    private DispatcherTimer? _lockTimer;
    private DispatcherTimer? _usbTimer;

    private readonly Mutex _singleMutex;
    private DateTime _lastActivity = DateTime.UtcNow;
    private bool _exiting;

    public event Action? Locked;
    public event Action? Unlocked;
    public event Action? UsbRemoved;
    public event Action? ShowRequested;
    /// <summary>Fired when the vault was destroyed by an armed self-destruct.</summary>
    public event Action? VaultDestroyed;
    /// <summary>Human-readable one-liner for the status bar (issue #17).</summary>
    public event Action<string>? Notice;

    private SessionManager()
    {
        VaultPath = AppPaths.VaultPath;              // issue #5: one source of truth
        SecurityEnforcer.RemoveLegacyState();        // issue #4: kill the old plaintext counter

        string pipeMsg = ParseCommandLine();

        _singleMutex = new Mutex(true, @"Local\RDPVault_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", "RDPVault_Show", PipeDirection.Out);
                client.Connect(1000);
                using var w = new StreamWriter(client) { AutoFlush = true };
                w.Write(pipeMsg);
            }
            catch { }
            Environment.Exit(0);
        }

        _ = Task.Run(PipeLoop);
    }

    /// <summary>
    /// Issue #6: shortcuts now pass "--launch &lt;profileId&gt;" directly. The legacy
    /// "--launch &lt;file.rdpvlink&gt;" form is still accepted so old shortcuts keep working.
    /// </summary>
    private string ParseCommandLine()
    {
        var args = Environment.GetCommandLineArgs();
        for (int i = 1; i < args.Length - 1; i++)
        {
            if (!string.Equals(args[i], "--launch", StringComparison.OrdinalIgnoreCase)) continue;

            string value = args[i + 1].Trim();
            string? id = null;

            if (System.IO.File.Exists(value))
            {
                try
                {
                    string content = System.IO.File.ReadAllText(value).Trim();
                    const string prefix = "TargetProfileId=";
                    if (content.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        id = content.Substring(prefix.Length).Trim();
                }
                catch { }
            }
            else if (Guid.TryParse(value, out Guid g))
            {
                id = g.ToString("N");
            }

            if (!string.IsNullOrEmpty(id))
            {
                PendingLaunchId = id;
                return "LAUNCH:" + id;
            }
        }
        return "SHOW";
    }

    // ---------------------------------------------------------------- timers

    private static void OnUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }

    /// <summary>
    /// Issue #18a: unlock runs inside Task.Run, and DispatcherTimer has UI-thread
    /// affinity in Avalonia. Creating/starting the timers off the UI thread threw.
    /// </summary>
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

    // ---------------------------------------------------------------- load / create

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

    /// <summary>Creates a brand new vault and hands back the Recovery Code to show the user (issue #2/#3).</summary>
    public string CreateNew(string password, VaultPayload payload)
    {
        File = VaultCrypto.CreateVault(password, payload, VaultPath, out string recoveryCode);
        UnlockWithPassword(password);
        return recoveryCode;
    }

    // ---------------------------------------------------------------- unlock

    /// <summary>Seconds the user must wait before another attempt is accepted (issue #4).</summary>
    public TimeSpan CooldownRemaining()
    {
        try
        {
            if (File == null && VaultExists) LoadFile();
            return File == null ? TimeSpan.Zero : SecurityEnforcer.CooldownRemaining(File);
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

        SecurityEnforcer.ClearFailures(File!, VaultPath);
        AfterUnlock();
    }

    /// <summary>Issue #2: the printed Recovery Code is a real second way in.</summary>
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
        LoadFile();
        string machineId = VaultCrypto.CurrentMachineId();
        SealEntry? seal = File!.Seals.FirstOrDefault(s => s.MachineId == machineId && !string.IsNullOrEmpty(s.KeyId));
        if (seal == null) return false;

        byte[]? signature = await WindowsHello.GetSignatureAsync(seal.KeyId);
        if (signature == null) return false;

        byte[]? master = VaultCrypto.UnsealTpm(File, seal, signature);
        CryptographicOperations.ZeroMemory(signature);
        if (master == null) return false;

        try { Payload = VaultCrypto.OpenPayload(File, master); }
        catch { CryptographicOperations.ZeroMemory(master); return false; }

        Master = master;
        SecurityEnforcer.ClearFailures(File, VaultPath);
        AfterUnlock();
        return true;
    }

    public bool HelloSealAvailable()
    {
        try
        {
            if (!VaultExists) return false;
            LoadFile();
            string machineId = VaultCrypto.CurrentMachineId();
            return File!.Seals.Any(s => s.MachineId == machineId && !string.IsNullOrEmpty(s.KeyId));
        }
        catch { return false; }
    }

    private void AfterUnlock()
    {
        Touch();
        ApplySweepConfig();
        TraceCleaner.Sweep();
        StartTimers();
        OnUi(() => Unlocked?.Invoke());

        // Issue #7: the BitLocker setting is now actually acted on, as a warning.
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
            var p = Payload.Profiles.FirstOrDefault(x => x.Id == target);
            if (p != null) OnUi(() => RdpLauncher.Launch(p));
            else OnUi(() => Notice?.Invoke("That shortcut points at a profile that no longer exists."));
        }
    }

    /// <summary>Issue #20: tell the cleaner which hosts belong to us so it can stay in its lane.</summary>
    private void ApplySweepConfig()
    {
        var settings = Payload?.Settings;
        TraceCleaner.Configure(
            settings?.SweepScope ?? SweepScope.OwnHostsOnly,
            Payload?.Profiles.Select(p => p.Host) ?? Enumerable.Empty<string>());
    }

    // ---------------------------------------------------------------- save

    /// <summary>
    /// Issue #9: this used to return silently when the vault had auto-locked, so
    /// changes were lost without a word. It now reports the problem.
    /// </summary>
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

    /// <summary>Issue #2: creates or replaces the Recovery Code and returns it for display.</summary>
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

    /// <summary>
    /// Issue #2/#19: this existed but was wired to nothing, discarded the key it
    /// derived without zeroing it, and dereferenced possibly-null state.
    /// </summary>
    public void ChangePassword(string oldPassword, string newPassword)
    {
        if (File == null || Master == null || Payload == null)
            throw new InvalidOperationException("The vault is locked.");
        if (newPassword.Length < 10)
            throw new ArgumentException("The new master password must be at least 10 characters.");

        byte[] verifyMaster;
        VaultPayload verifyPayload;
        try { (verifyMaster, verifyPayload) = VaultCrypto.Open(File, oldPassword); }
        catch (InvalidDataException) { throw new InvalidDataException("The current password is not correct."); }

        // We only needed proof of knowledge; keep the already-open master key.
        CryptographicOperations.ZeroMemory(verifyMaster);
        _ = verifyPayload;

        // A password change is treated as a possible compromise: every quick-unlock
        // seal is dropped, so each PC must re-enroll Windows Hello.
        VaultCrypto.Save(File, Master, Payload, VaultPath, newPassword, newSeals: new List<SealEntry>());
    }

    // ---------------------------------------------------------------- lock

    public void Lock(bool killSessions)
    {
        bool deep = Payload?.Settings.DeepSweep == true;   // issue #7: setting now honoured

        if (Master != null) CryptographicOperations.ZeroMemory(Master);
        Master = null;
        Payload = null;
        Touch();

        OnUi(() => Locked?.Invoke());
        if (killSessions) RdpLauncher.KillAll();

        if (deep) TraceCleaner.DeepSweep(); else TraceCleaner.Sweep();
        TraceCleaner.ForgetHosts();   // issue #20: don't retain host names after locking
    }

    /// <summary>Issue #11: ANY meaningful user action postpones the auto-lock.</summary>
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
        try
        {
            if (!Directory.Exists(AppPaths.ExeDir)) throw new IOException("root gone");
            string? exe = Environment.ProcessPath;
            if (exe != null && !System.IO.File.Exists(exe)) throw new IOException("exe gone");
        }
        catch
        {
            _exiting = true;
            bool kill = Payload?.Settings.KillSessionsOnUsbRemoval ?? true;
            OnUi(() => UsbRemoved?.Invoke());
            Lock(killSessions: kill);
            if (kill) TraceCleaner.DeepSweep();
            Environment.Exit(0);
        }
    }

    // ---------------------------------------------------------------- Windows Hello enrollment

    public async Task<HelloEnrollResult> EnableHelloSealAsync()
    {
        if (File == null || Master == null || Payload == null) return HelloEnrollResult.NotSupported;

        var (result, keyId, signature) = await WindowsHello.EnrollAndSignAsync();
        if (result != HelloEnrollResult.Success) return result;

        try
        {
            var seal = VaultCrypto.SealTpm(Master, keyId, signature, File);

            // Issue #18c: prove the seal can actually be opened before we save it.
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

    // ---------------------------------------------------------------- IPC

    private async Task PipeLoop()
    {
        while (!_exiting)
        {
            try
            {
                using var server = new NamedPipeServerStream("RDPVault_Show", PipeDirection.In);
                await server.WaitForConnectionAsync();
                using var r = new StreamReader(server);
                string msg = (await r.ReadToEndAsync() ?? "").Trim();

                Touch();
                if (msg.StartsWith("LAUNCH:", StringComparison.Ordinal))
                {
                    string target = msg.Substring(7);
                    if (IsUnlocked && Payload != null)
                    {
                        var p = Payload.Profiles.FirstOrDefault(x => x.Id == target);
                        if (p != null) OnUi(() => RdpLauncher.Launch(p));
                        else OnUi(() => Notice?.Invoke("That shortcut points at a profile that no longer exists."));
                    }
                    else
                    {
                        PendingLaunchId = target;
                        OnUi(() => ShowRequested?.Invoke());
                    }
                }
                else
                {
                    OnUi(() => ShowRequested?.Invoke());
                }
            }
            catch
            {
                if (!_exiting) await Task.Delay(200);
            }
        }
    }

    public void Dispose()
    {
        if (_exiting) return;
        _exiting = true;

        bool deep = Payload?.Settings.DeepSweep == true;
        if (Master != null) CryptographicOperations.ZeroMemory(Master);
        Master = null;
        Payload = null;

        OnUi(() =>
        {
            _lockTimer?.Stop();
            _usbTimer?.Stop();
        });

        if (deep) TraceCleaner.DeepSweep(); else TraceCleaner.Sweep();
        TraceCleaner.ForgetHosts();

        try { _singleMutex.ReleaseMutex(); } catch { }
        try { _singleMutex.Dispose(); } catch { }
    }
}
