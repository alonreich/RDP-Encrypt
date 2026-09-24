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

    /// <summary>
    /// ISSUE #1 (2026 review). True when this session was opened with the printed
    /// Recovery Code rather than the master password.
    ///
    /// What was broken: after a recovery unlock the app told the user to "set a new
    /// master password in Settings", but ChangePassword demanded the CURRENT password
    /// to prove knowledge - the exact thing the user had just proved they did not
    /// have. The instruction was impossible to follow and the vault stayed locked to a
    /// password nobody knew, with the Recovery Code as the only key forever.
    ///
    /// When this is true the proof-of-knowledge step is skipped: the master key is
    /// already unwrapped in memory, unwrapped by a 256-bit secret, so there is nothing
    /// further to prove.
    /// </summary>
    public bool UnlockedViaRecovery { get; private set; }

    // Timers are created lazily ON THE UI THREAD - see StartTimers (issue #18a).
    private DispatcherTimer? _lockTimer;
    private DispatcherTimer? _usbTimer;
    private DateTime _lastActivity = DateTime.UtcNow;
    private bool _exiting;

    /// <summary>Issue #3 (2026 review): consecutive 2 s polls that must all fail before
    /// the app tears itself down. One transient miss is not a removed drive.</summary>
    private const int UsbMissesBeforeShutdown = 3;
    private int _usbMisses;

    public event Action? Locked;
    public event Action? Unlocked;
    public event Action? UsbRemoved;
    public event Action? ShowRequested;
    /// <summary>Fired when the vault was destroyed by an armed self-destruct.</summary>
    public event Action? VaultDestroyed;
    /// <summary>Human-readable one-liner for the status bar (issue #17).</summary>
    public event Action<string>? Notice;
    /// <summary>Fired when an RDP profile launch is requested via shortcut, pipe, or pending launch. bool indicates fromShortcut.</summary>
    public event Action<RdpProfile, bool>? LaunchRequested;

    private SessionManager()
    {
        VaultPath = AppPaths.VaultPath;              // issue #5: one source of truth
        SecurityEnforcer.RemoveLegacyState();        // issue #4: kill the old plaintext counter

        ParseCommandLine();
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

    /// <summary>Seconds the user must wait before another attempt is accepted (issue #4 / #10).</summary>
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
        UnlockedViaRecovery = true;      // issue #1: lets ChangePassword skip the old password
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

    /// <summary>
    /// Issue #3: Offload Argon2id computation to Task.Run to prevent freezing the UI Dispatcher.
    /// </summary>
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

    /// <summary>
    /// Issue #10: Inspect in-memory File representation when unlocked to avoid redundant disk reads and state tearing.
    /// </summary>
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
            if (p != null)
            {
                if (LaunchRequested != null) OnUi(() => LaunchRequested.Invoke(p, true));
                else OnUi(() => RdpLauncher.Launch(p));
            }
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
    public void ChangePassword(string? oldPassword, string newPassword)
    {
        if (File == null || Master == null || Payload == null)
            throw new InvalidOperationException("The vault is locked.");
        if (newPassword.Length < 10)
            throw new ArgumentException("The new master password must be at least 10 characters.");

        // ISSUE #1 (2026 review): a session opened with the Recovery Code cannot be
        // asked for the old password - that is the whole reason the user is here. The
        // master key is already unwrapped, so proof of knowledge adds nothing.
        if (!UnlockedViaRecovery)
        {
            if (string.IsNullOrEmpty(oldPassword))
                throw new ArgumentException("Enter your current master password.");

            byte[] verifyMaster;
            VaultPayload verifyPayload;
            try { (verifyMaster, verifyPayload) = VaultCrypto.Open(File, oldPassword); }
            catch (InvalidDataException) { throw new InvalidDataException("The current password is not correct."); }

            // We only needed proof of knowledge; keep the already-open master key.
            CryptographicOperations.ZeroMemory(verifyMaster);
            _ = verifyPayload;
        }

        // A password change is treated as a possible compromise: every quick-unlock
        // seal is dropped, so each PC must re-enroll Windows Hello.
        VaultCrypto.Save(File, Master, Payload, VaultPath, newPassword, newSeals: new List<SealEntry>());

        // The vault is now on a password the user chose and knows.
        UnlockedViaRecovery = false;
    }

    // ---------------------------------------------------------------- lock

    public void Lock(bool killSessions)
    {
        bool deep = Payload?.Settings.DeepSweep == true;   // issue #7: setting now honoured

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
            TraceCleaner.ForgetHosts();   // issue #20: don't retain host names after locking
        });
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

    /// <summary>
    /// ISSUE #3 (2026 review).
    ///
    /// What was broken: a SINGLE failed Directory.Exists / File.Exists poll tore the
    /// whole app down - Lock, KillAll, DeepSweep, Environment.Exit - with no debounce,
    /// no retry and no confirmation. An antivirus lock, a sleep/resume cycle, a slow
    /// USB controller or a network-drive hiccup was enough to kill every open remote
    /// desktop. Worse, UsbRemoved was posted to the UI thread and the process exited
    /// before it could render, so the user got no explanation at all.
    ///
    /// Now: the drive must be missing for UsbMissesBeforeShutdown consecutive polls
    /// (3 x 2 s = 6 s), a single recovery resets the counter, and the shutdown work
    /// runs off the UI thread with a short grace period so the on-screen message is
    /// actually painted before the process ends.
    /// </summary>
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
            await Task.Delay(2500);   // let the explanation on screen actually render
            Environment.Exit(0);
        });
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
                    OnUi(() => ShowRequested?.Invoke());

                    if (IsUnlocked && Payload != null)
                    {
                        var p = Payload.Profiles.FirstOrDefault(x => x.Id == target);
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
