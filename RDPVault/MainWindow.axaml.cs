using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Threading;
using System;
using System.Threading.Tasks;

namespace RDPVault;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _uiTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _busy;
    private bool _closing;
    private System.Threading.CancellationTokenSource? _connectCts;

    public MainWindow()
    {
        InitializeComponent();

        var mgr = SessionManager.Current;
        mgr.Locked += OnLocked;
        mgr.Unlocked += OnUnlocked;
        mgr.ShowRequested += OnShowRequested;
        mgr.UsbRemoved += OnUsbRemoved;          // issue #17: was never wired up
        mgr.VaultDestroyed += OnVaultDestroyed;
        mgr.Notice += SetStatus;
        mgr.LaunchRequested += async (p, fromShortcut) => await ValidateAndLaunchProfileAsync(p, fromShortcut);

        RdpLauncher.SessionStarted += name => Dispatcher.UIThread.Post(() => SetStatus($"Connecting to {name}..."));
        RdpLauncher.SessionEnded += status => Dispatcher.UIThread.Post(() => SetStatus(status));
        RdpLauncher.LaunchFailed += msg => Dispatcher.UIThread.Post(() => SetStatus(msg));

        TxtSearch.TextChanged += (_, _) => RefreshProfiles();

        // Issue #11: real user activity postpones the auto-lock. The old code hooked
        // input into a private field that was never read, so simply using the app did
        // not stop the vault locking under the user's hands.
        AddHandler(InputElement.PointerPressedEvent, (_, _) => SessionManager.Current.Touch(),
                   RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(InputElement.KeyDownEvent, (_, _) => SessionManager.Current.Touch(),
                   RoutingStrategies.Tunnel, handledEventsToo: true);

        _uiTimer.Tick += (_, _) => UpdateIdleReadout();
        _uiTimer.Start();

        UpdateUIState();

        Loaded += async (_, _) =>
        {
            // Issue #21: the OS credential prompt has no owner-window API, so it
            // needs to know which of our windows to hand the foreground back to.
            SystemPromptFocus.SetOwner(TryGetPlatformHandle()?.Handle ?? IntPtr.Zero);

            await Task.Delay(300);   // let the window render before any OS Hello prompt
            if (SessionManager.Current.VaultExists && SessionManager.Current.HelloSealAvailable())
            {
                BtnHello.IsVisible = true;
                await AttemptHelloUnlockAsync();
            }
        };
    }

    // ---------------------------------------------------------------- state

    private void UpdateUIState()
    {
        var mgr = SessionManager.Current;
        bool unlocked = mgr.IsUnlocked;
        bool vaultExists = mgr.VaultExists;

        LockedPanel.IsVisible = !unlocked;
        MainPanel.IsVisible = unlocked;

        if (!unlocked)
        {
            PnlUnlock.IsVisible = vaultExists;
            PnlFirstRun.IsVisible = !vaultExists;
            TxtLockSubtitle.Text = vaultExists
                ? "Enter your master password."
                : "Welcome to RDP Vault.";
            TxtPassword.Text = "";
            TxtLockError.IsVisible = false;
            TxtLockStatus.IsVisible = false;
            ProgUnlock.IsVisible = false;
            BtnHello.IsVisible = vaultExists && mgr.HelloSealAvailable();
            BtnRecovery.IsVisible = vaultExists;
            if (vaultExists) TxtPassword.Focus();
        }
        else
        {
            RefreshProfiles();
            ShowWarning(null);
            if (!mgr.HasRecoveryCode)
                ShowWarning("This vault has no Recovery Code. If you forget the master password there is no way back in. " +
                            "Open Settings to create one.");
        }
    }

    private void RefreshProfiles()
    {
        var profiles = SessionManager.Current.Payload?.Profiles;
        if (profiles == null) return;

        string query = (TxtSearch?.Text ?? "").Trim();
        var filtered = string.IsNullOrEmpty(query)
            ? profiles
            : profiles.Where(p =>
                p.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                p.Host.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                p.Username.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                p.Notes.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();

        LstProfiles.ItemsSource = null;
        LstProfiles.ItemsSource = filtered;
        LstProfiles.IsVisible = filtered.Count > 0;
        TxtEmpty.IsVisible = filtered.Count == 0;

        if (string.IsNullOrEmpty(query))
        {
            TxtEmpty.Text = "No profiles yet. Use \"Add Profile\" to store your first connection.";
            TxtCount.Text = $"{profiles.Count} profile{(profiles.Count != 1 ? "s" : "")}";
        }
        else
        {
            TxtEmpty.Text = $"No profiles matching \"{query}\".";
            TxtCount.Text = $"{filtered.Count} of {profiles.Count} profile{(profiles.Count != 1 ? "s" : "")}";
        }
    }

    /// <summary>Issue #17: the status line used to be the hard-coded lie "Connected to Hardware TPM."</summary>
    private void SetStatus(string text)
    {
        TxtStatus.Text = text;
    }

    private void ShowWarning(string? text)
    {
        WarnBar.IsVisible = !string.IsNullOrEmpty(text);
        TxtWarn.Text = text ?? "";
    }

    private void UpdateIdleReadout()
    {
        var mgr = SessionManager.Current;
        if (!mgr.IsUnlocked) { TxtIdle.Text = ""; return; }

        TimeSpan left = mgr.IdleRemaining();
        int live = RdpLauncher.LiveCount();
        string sessions = live > 0 ? $"{live} session{(live != 1 ? "s" : "")} open  ·  " : "";
        TxtIdle.Text = $"{sessions}locks in {(int)left.TotalMinutes:00}:{left.Seconds:00}";
    }

    private void OnLocked() => Dispatcher.UIThread.Post(() => { UpdateUIState(); SetStatus("Vault locked."); });
    private void OnUnlocked() => Dispatcher.UIThread.Post(() => { UpdateUIState(); SetStatus("Vault unlocked."); });

    private void OnShowRequested() => Dispatcher.UIThread.Post(() =>
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    });

    private void OnUsbRemoved() => Dispatcher.UIThread.Post(() =>
        SetStatus("The drive holding RDP Vault was removed. Locking and closing."));

    private void OnVaultDestroyed() => Dispatcher.UIThread.Post(async () =>
    {
        await Dialogs.MessageAsync(this, "Vault destroyed",
            "Self-destruct was armed and the failed-attempt limit was reached. The vault file has been erased. " +
            "Restore a copy from your backups if you have one.", isError: true);
        UpdateUIState();
    });

    // ---------------------------------------------------------------- unlock

    private void BtnUnlock_Click(object? sender, RoutedEventArgs e) => _ = SubmitPasswordAsync();

    private void TxtPassword_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) _ = SubmitPasswordAsync();
    }

    private async Task SubmitPasswordAsync()
    {
        if (_busy) return;
        var mgr = SessionManager.Current;

        if (!mgr.VaultExists)
        {
            // Issue #3: creating a vault is never a side effect of typing in the unlock box.
            await CreateVaultFlowAsync();
            return;
        }

        string pwd = TxtPassword.Text ?? "";
        if (pwd.Length == 0) return;

        SetBusy(true, "Deriving encryption key via Argon2id (64 MiB RAM, 3 iterations)...");
        try
        {
            await Task.Run(() => mgr.UnlockWithPassword(pwd));
            UpdateUIState();
        }
        catch (Exception ex)
        {
            ShowLockError(ex.Message);
        }
        finally
        {
            SetBusy(false, null);
            TxtPassword.Text = "";
            if (!mgr.IsUnlocked) TxtPassword.Focus();
        }
    }

    private async void BtnCreateVault_Click(object? sender, RoutedEventArgs e) => await CreateVaultFlowAsync();

    private async Task CreateVaultFlowAsync()
    {
        string? password = await Dialogs.CreateVaultAsync(this);
        if (string.IsNullOrEmpty(password)) return;

        SetBusy(true, "Generating encryption keys via Argon2id & AES-256-GCM...");
        string recoveryCode;
        try
        {
            recoveryCode = await Task.Run(() => SessionManager.Current.CreateNew(password, new VaultPayload()));
        }
        catch (Exception ex)
        {
            SetBusy(false, null);
            ShowLockError(ex.Message);
            return;
        }
        SetBusy(false, null);

        await Dialogs.ShowRecoveryCodeAsync(this, recoveryCode);
        UpdateUIState();
    }

    /// <summary>Issue #2: a real "I forgot my password" path.</summary>
    private async void BtnRecovery_Click(object? sender, RoutedEventArgs e)
    {
        if (_busy) return;
        string? code = await Dialogs.AskRecoveryCodeAsync(this);
        if (string.IsNullOrWhiteSpace(code)) return;

        SetBusy(true, "Verifying Crockford-Base32 Recovery Code via Argon2id...");
        try
        {
            bool ok = await Task.Run(() => SessionManager.Current.UnlockWithRecoveryCode(code));
            if (!ok) { ShowLockError("That Recovery Code did not work."); return; }

            UpdateUIState();
            SetBusy(false, null);

            // ISSUE #1 (2026 review): this used to point the user at
            // Settings > Change master password, which demanded the CURRENT password -
            // the one they had just told us they had forgotten. The flow was a dead
            // end. The new password is now set right here, with no old password asked
            // for, and a fresh Recovery Code is offered immediately afterwards because
            // the one they just used is now the only key they have proven they hold.
            await OfferPasswordResetAfterRecoveryAsync();
            return;
        }
        catch (Exception ex)
        {
            ShowLockError(ex.Message);
        }
        finally
        {
            SetBusy(false, null);
        }
    }

    /// <summary>Issue #1 (2026 review): completes the "I forgot my master password" journey.</summary>
    private async Task OfferPasswordResetAfterRecoveryAsync()
    {
        await Dialogs.MessageAsync(this, "Unlocked with your Recovery Code",
            "Your Recovery Code opened the vault. Choose a new master password now - the old one still works " +
            "until you do, and right now the Recovery Code is the only key you are certain of.");

        var result = await Dialogs.ChangePasswordAsync(this, requireOldPassword: false);
        if (result == null)
        {
            SetStatus("Unlocked with your Recovery Code. The master password was NOT changed.");
            ShowWarning("You unlocked with your Recovery Code and did not set a new master password. " +
                        "Open Settings > Change master password to set one - you will not be asked for the old one.");
            return;
        }

        try
        {
            await Task.Run(() => SessionManager.Current.ChangePassword(null, result.Value.New));
        }
        catch (Exception ex)
        {
            await Dialogs.MessageAsync(this, "Password not changed", ex.Message, isError: true);
            return;
        }

        bool fresh = await Dialogs.ConfirmAsync(this, "Generate a new Recovery Code?",
            "Your master password has been changed and Windows Hello quick unlock was switched off on every PC. " +
            "The Recovery Code you just used still works. Replacing it now retires the copy you have been carrying around.",
            confirmText: "Generate a new code");

        if (fresh)
        {
            try
            {
                string code = await Task.Run(() => SessionManager.Current.RegenerateRecoveryCode());
                await Dialogs.ShowRecoveryCodeAsync(this, code);
            }
            catch (Exception ex)
            {
                await Dialogs.MessageAsync(this, "Could not create a Recovery Code", ex.Message, isError: true);
            }
        }

        UpdateUIState();
        SetStatus("Master password changed.");
    }

    private async void BtnHello_Click(object? sender, RoutedEventArgs e) => await AttemptHelloUnlockAsync();

    private async Task AttemptHelloUnlockAsync()
    {
        if (_busy) return;

        // Issue #21: Windows only lets the credential broker take the foreground if
        // OUR process holds it first, so make sure this window is genuinely active
        // and un-minimised immediately before the prompt is raised.
        SystemPromptFocus.SetOwner(TryGetPlatformHandle()?.Handle ?? IntPtr.Zero);
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show();
        Activate();

        SetBusy(true, "Awaiting Windows Hello biometric/PIN signature verification...");
        try
        {
            bool ok = await SessionManager.Current.UnlockWithHelloAsync();
            if (ok) UpdateUIState();
            else ShowLockError("Windows Hello could not unlock this vault. Use your master password.");
        }
        catch (Exception ex)
        {
            ShowLockError(ex.Message);
        }
        finally
        {
            SetBusy(false, null);
        }
    }

    private void SetBusy(bool busy, string? status)
    {
        _busy = busy;
        TxtPassword.IsEnabled = !busy;
        BtnUnlock.IsEnabled = !busy;
        BtnHello.IsEnabled = !busy;
        BtnRecovery.IsEnabled = !busy;
        BtnCreateVault.IsEnabled = !busy;
        ProgUnlock.IsVisible = busy;

        if (status != null)
        {
            TxtLockError.IsVisible = false;
            TxtLockStatus.Text = status;
            TxtLockStatus.IsVisible = true;
        }
        else if (!busy)
        {
            TxtLockStatus.IsVisible = false;
        }
    }

    private void ShowLockError(string message)
    {
        TxtLockStatus.IsVisible = false;
        TxtLockError.Text = message;
        TxtLockError.IsVisible = true;
    }

    // ---------------------------------------------------------------- profiles

    private void BtnLock_Click(object? sender, RoutedEventArgs e) => SessionManager.Current.Lock(killSessions: false);

    private async void BtnSettings_Click(object? sender, RoutedEventArgs e)
    {
        SessionManager.Current.Touch();
        await new SettingsWindow().ShowDialog(this);
        // Issue #21: Settings took ownership of the credential prompt; take it back.
        SystemPromptFocus.SetOwner(TryGetPlatformHandle()?.Handle ?? IntPtr.Zero);
        UpdateUIState();
    }

    private async void BtnAddProfile_Click(object? sender, RoutedEventArgs e)
    {
        SessionManager.Current.Touch();
        var editor = new ProfileEditorWindow();
        if (await editor.ShowDialog<bool>(this) && SessionManager.Current.Payload != null)
        {
            SessionManager.Current.Payload.Profiles.Add(editor.Profile);
            await SaveOrReport();
            RefreshProfiles();
        }
    }

    private async void BtnEdit_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not RdpProfile p) return;
        SessionManager.Current.Touch();

        // The editor works on a copy, so Cancel really cancels (issue #15).
        var editor = new ProfileEditorWindow(p);
        if (!await editor.ShowDialog<bool>(this)) return;

        editor.ApplyTo(p);
        await SaveOrReport();
        RefreshProfiles();
    }

    /// <summary>Issue #12: deleting a profile is irreversible, so it now asks first.</summary>
    private async void BtnDelete_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not RdpProfile p) return;
        if (SessionManager.Current.Payload == null) return;
        SessionManager.Current.Touch();

        bool go = await Dialogs.ConfirmAsync(this, "Delete this profile?",
            $"\"{(string.IsNullOrWhiteSpace(p.Name) ? p.Host : p.Name)}\" and its saved password will be removed from the vault. " +
            "This cannot be undone.",
            confirmText: "Delete", danger: true);
        if (!go) return;

        SessionManager.Current.Payload.Profiles.Remove(p);
        await SaveOrReport();
        RefreshProfiles();
    }

    private async void BtnConnect_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is RdpProfile p)
        {
            await ValidateAndLaunchProfileAsync(p);
        }
    }

    private async Task ValidateAndLaunchProfileAsync(RdpProfile p, bool fromShortcut = false)
    {
        if (_busy) return;
        SessionManager.Current.Touch();

        var active = RdpLauncher.FindActiveSession(p);
        if (active != null)
        {
            string hostDisplay = $"{active.Host}:{active.Port}";
            string nameDisplay = string.IsNullOrWhiteSpace(active.ProfileName) ? hostDisplay : active.ProfileName;
            bool disconnectAndReconnect = await Dialogs.ConfirmAsync(
                this,
                "Active Session Detected",
                $"A Remote Desktop session to \"{nameDisplay}\" ({hostDisplay}) is currently active.\n\n" +
                "Do you want to disconnect the active session and start a new connection, or abort and keep the current session running?",
                confirmText: "Disconnect & Connect",
                danger: true);

            if (!disconnectAndReconnect)
            {
                SetStatus($"Connection to {p.Name} cancelled. Active session was kept.");
                return;
            }

            SetStatus($"Disconnecting existing session to {hostDisplay}...");
            RdpLauncher.DisconnectSession(active);
            await Task.Delay(500); // Allow mstsc process to terminate cleanly
        }

        _connectCts?.Dispose();
        _connectCts = new System.Threading.CancellationTokenSource();

        TxtLaunchTitle.Text = p.EnableWol ? "WAKE-ON-LAN & CONNECT" : "CONNECTING TO REMOTE DESKTOP";
        TxtLaunchTarget.Text = $"{p.Name}  ·  {p.DisplayHost}";
        ProgLaunch.IsIndeterminate = true;
        ProgLaunch.Value = 0;
        TxtLaunchCountdown.IsVisible = false;
        TxtLaunchStep.Text = "Initializing connection...";
        TxtLaunchSubStatus.Text = "";
        BtnCancelLaunch.IsEnabled = true;
        OverlayLaunch.IsVisible = true;
        _busy = true;

        void OnProgress(LaunchProgressUpdate u)
        {
            Dispatcher.UIThread.Post(() =>
            {
                TxtLaunchStep.Text = u.Details;
                TxtLaunchSubStatus.Text = u.Step;
                if (u.IsIndeterminate)
                {
                    ProgLaunch.IsIndeterminate = true;
                    TxtLaunchCountdown.IsVisible = false;
                }
                else
                {
                    ProgLaunch.IsIndeterminate = false;
                    ProgLaunch.Value = u.ProgressPercent ?? 0;
                    if (u.SecondsRemaining.HasValue)
                    {
                        TxtLaunchCountdown.Text = $"{u.SecondsRemaining.Value}s remaining";
                        TxtLaunchCountdown.IsVisible = true;
                    }
                    else
                    {
                        TxtLaunchCountdown.IsVisible = false;
                    }
                }
            });
        }

        bool launched = false;
        try
        {
            launched = await RdpLauncher.LaunchAsync(p, OnProgress, _connectCts.Token);
        }
        catch (OperationCanceledException)
        {
            SetStatus($"Connection to {p.Name} cancelled.");
        }
        catch (Exception ex)
        {
            SetStatus($"Connection failed: {ex.Message}");
        }
        finally
        {
            _busy = false;
            await Task.Delay(350); // brief confirmation
            OverlayLaunch.IsVisible = false;
        }

        if (launched && fromShortcut)
        {
            Dispatcher.UIThread.Post(() =>
            {
                WindowState = WindowState.Minimized;
            });
        }
    }

    private void BtnCancelLaunch_Click(object? sender, RoutedEventArgs e)
    {
        BtnCancelLaunch.IsEnabled = false;
        TxtLaunchStep.Text = "Cancelling connection...";
        _connectCts?.Cancel();
    }

    private async void BtnShortcut_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not RdpProfile p) return;
        SessionManager.Current.Touch();

        var result = ShortcutGenerator.CreateDesktopShortcut(p, overwrite: false);
        if (!result.Ok && result.Message == "exists")
        {
            bool go = await Dialogs.ConfirmAsync(this, "Replace the existing shortcut?",
                $"There is already a shortcut named \"{System.IO.Path.GetFileName(result.Path)}\" on your desktop.",
                confirmText: "Replace");
            if (!go) return;
            result = ShortcutGenerator.CreateDesktopShortcut(p, overwrite: true);
        }

        SetStatus(result.Message);
        if (!result.Ok && result.Message != "exists")
            await Dialogs.MessageAsync(this, "Shortcut not created", result.Message, isError: true);
    }

    private async Task SaveOrReport()
    {
        if (SessionManager.Current.TrySave(out string? error)) return;
        // Issue #9: saving used to fail silently when the vault had auto-locked.
        await Dialogs.MessageAsync(this, "Changes were not saved",
            error ?? "The vault could not be saved.", isError: true);
    }

    // ---------------------------------------------------------------- shutdown

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // Issue #12: don't silently take running desktops down with the window.
        if (!_closing && RdpLauncher.AnyLive())
        {
            e.Cancel = true;
            _ = ConfirmExitAsync();
            return;
        }
        base.OnClosing(e);
    }

    private async Task ConfirmExitAsync()
    {
        int live = RdpLauncher.LiveCount();
        bool go = await Dialogs.ConfirmAsync(this, "Close RDP Vault?",
            $"{live} Remote Desktop window{(live != 1 ? "s are" : " is")} still open. " +
            "Closing RDP Vault leaves them running, but the vault will lock and local traces will be cleaned.",
            confirmText: "Close anyway");
        if (!go) return;

        _closing = true;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _uiTimer.Stop();
        SessionManager.Current.Dispose();
        base.OnClosed(e);
        Environment.Exit(0);
    }
}
