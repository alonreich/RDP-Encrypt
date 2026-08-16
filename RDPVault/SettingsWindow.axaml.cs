using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;

namespace RDPVault;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();

        var settings = SessionManager.Current.Payload?.Settings;
        var policy = SessionManager.Current.File?.Policy ?? new VaultPolicy();

        if (settings != null)
        {
            TxtLockMinutes.Text = settings.LockMinutes.ToString();
            ChkKillSessions.IsChecked = settings.KillSessionsOnUsbRemoval;
            ChkForceMultiMon.IsChecked = settings.ForceMultiMon;
            ChkDeepSweep.IsChecked = settings.DeepSweep;
            ChkWarnBitLocker.IsChecked = settings.WarnIfDriveNotEncrypted;
            CmbSweepScope.SelectedIndex = settings.SweepScope == SweepScope.Everything ? 1 : 0;
        }

        ChkSelfDestruct.IsChecked = policy.SelfDestructEnabled;
        TxtSelfDestructAttempts.Text = policy.MaxAttempts.ToString();
        TxtSelfDestructWindow.Text = policy.WindowMinutes.ToString();

        CmbSweepScope.SelectionChanged += (_, _) => UpdateScopeHint();
        UpdateScopeHint();
        UpdateHelloUI();
        UpdateRecoveryUI();
        UpdateSelfDestructNote();

        _ = CheckBitLockerAsync();
    }

    // ---------------------------------------------------------------- live read-outs

    private void UpdateScopeHint()
    {
        // Issue #20: say plainly what the aggressive option destroys.
        TxtScopeHint.Text = CmbSweepScope.SelectedIndex == 1
            ? "Warning: this also erases Remote Desktop history for connections you made outside RDP Vault, plus the mstsc entries in Recent items, UserAssist and Prefetch. That cannot be undone."
            : "Only registry entries, Default.rdp, jump-list and Recent entries that mention a host stored in this vault are removed. Your own separate Remote Desktop history is left alone.";
    }

    private void UpdateRecoveryUI()
    {
        // Issue #2: tell the user the truth about whether a way back in exists.
        bool has = SessionManager.Current.HasRecoveryCode;
        TxtRecoveryStatus.Text = has
            ? "A Recovery Code exists for this vault. Generating a new one immediately invalidates the old one."
            : "This vault has NO Recovery Code. If you forget the master password, the contents are gone for good.";
        TxtRecoveryStatus.Foreground = has
            ? new SolidColorBrush(Color.Parse("#8A8A93"))
            : new SolidColorBrush(Color.Parse("#E8A030"));
        BtnRecoveryCode.Content = has ? "Replace Recovery Code" : "Create Recovery Code";
        UpdateSelfDestructNote();
    }

    private void UpdateHelloUI()
    {
        bool enrolled = SessionManager.Current.HelloSealAvailable();
        BtnToggleHello.Content = enrolled ? "Turn off quick unlock" : "Enable quick unlock";
        TxtHelloStatus.Text = enrolled
            ? "On for this PC and this Windows account."
            : "Not set up on this PC.";
        TxtHelloStatus.Foreground = new SolidColorBrush(Color.Parse(enrolled ? "#2FBF71" : "#8A8A93"));
    }

    private void UpdateSelfDestructNote()
    {
        bool armed = ChkSelfDestruct.IsChecked == true;
        bool hasRecovery = SessionManager.Current.HasRecoveryCode;

        TxtSelfDestructAttempts.IsEnabled = armed;
        TxtSelfDestructWindow.IsEnabled = armed;

        TxtSelfDestructNote.Text = !armed
            ? "Off. Wrong attempts are only slowed down - nothing is deleted."
            : hasRecovery
                ? "Armed. Keep your Recovery Code and a copy of the vault file somewhere safe."
                : "Armed WITHOUT a Recovery Code. Create one first - otherwise a run of typos deletes everything permanently.";
        TxtSelfDestructNote.Foreground = new SolidColorBrush(Color.Parse(
            !armed ? "#8A8A93" : hasRecovery ? "#E8A030" : "#E86060"));
    }

    private async Task CheckBitLockerAsync()
    {
        // Issue #7: this used to be a checkbox with no code behind it whatsoever.
        string? root = Path.GetPathRoot(SessionManager.Current.VaultPath);
        string drive = (root ?? "C:\\").TrimEnd('\\', '/');
        var status = await Task.Run(() => SecurityEnforcer.CheckDrive(drive));

        Dispatcher.UIThread.Post(() =>
        {
            TxtBitLockerStatus.Text = status switch
            {
                BitLockerStatus.Encrypted => $"{drive} is encrypted by BitLocker.",
                BitLockerStatus.NotEncrypted => $"{drive} is NOT encrypted. Anyone who takes this drive gets the vault file - only your master password stands in the way.",
                _ => $"Could not determine whether {drive} is encrypted (manage-bde was unavailable or its output was not recognised)."
            };
            TxtBitLockerStatus.Foreground = new SolidColorBrush(Color.Parse(
                status == BitLockerStatus.Encrypted ? "#2FBF71" :
                status == BitLockerStatus.NotEncrypted ? "#E8A030" : "#8A8A93"));
        });
    }

    // ---------------------------------------------------------------- actions

    private async void BtnChangePassword_Click(object? sender, RoutedEventArgs e)
    {
        SessionManager.Current.Touch();
        var result = await Dialogs.ChangePasswordAsync(this);
        if (result == null) return;

        try
        {
            await Task.Run(() => SessionManager.Current.ChangePassword(result.Value.Old, result.Value.New));
            UpdateHelloUI();
            await Dialogs.MessageAsync(this, "Password changed",
                "Your master password has been changed. Windows Hello quick unlock was switched off on every PC and must be set up again.");
        }
        catch (Exception ex)
        {
            await Dialogs.MessageAsync(this, "Password not changed", ex.Message, isError: true);
        }
    }

    private async void BtnRecoveryCode_Click(object? sender, RoutedEventArgs e)
    {
        SessionManager.Current.Touch();

        if (SessionManager.Current.HasRecoveryCode)
        {
            bool go = await Dialogs.ConfirmAsync(this, "Replace the Recovery Code?",
                "The code you have written down now will stop working immediately. Only the new one will open this vault.",
                confirmText: "Generate a new code", danger: true);
            if (!go) return;
        }

        try
        {
            string code = await Task.Run(() => SessionManager.Current.RegenerateRecoveryCode());
            await Dialogs.ShowRecoveryCodeAsync(this, code);
            UpdateRecoveryUI();
        }
        catch (Exception ex)
        {
            await Dialogs.MessageAsync(this, "Could not create a Recovery Code", ex.Message, isError: true);
        }
    }

    private async void BtnToggleHello_Click(object? sender, RoutedEventArgs e)
    {
        BtnToggleHello.IsEnabled = false;
        SessionManager.Current.Touch();

        // Issue #21: enrollment raises the same OS prompt as unlocking, so it needs
        // the same foreground handling - and the owner is THIS window, not MainWindow.
        SystemPromptFocus.SetOwner(TryGetPlatformHandle()?.Handle ?? IntPtr.Zero);
        Activate();

        try
        {
            if (SessionManager.Current.HelloSealAvailable())
            {
                SessionManager.Current.DisableHelloSeal();
            }
            else
            {
                var result = await SessionManager.Current.EnableHelloSealAsync();
                if (result != HelloEnrollResult.Success)
                {
                    string message = result switch
                    {
                        HelloEnrollResult.Cancelled => "Windows Hello was cancelled, so nothing was changed.",
                        HelloEnrollResult.NotSupported => "This PC does not offer Windows Hello with a hardware-backed key.",
                        // Issue #18c
                        HelloEnrollResult.SignatureNotReproducible =>
                            "This PC's Windows Hello key does not produce a repeatable signature, so it cannot be used to unlock the vault. " +
                            "Quick unlock has been left off; your master password still works normally.",
                        _ => "Quick unlock could not be enabled."
                    };
                    await Dialogs.MessageAsync(this, "Quick unlock not enabled", message,
                        isError: result == HelloEnrollResult.SignatureNotReproducible);
                    return;
                }
            }
            UpdateHelloUI();
        }
        catch (Exception ex)
        {
            await Dialogs.MessageAsync(this, "Quick unlock error", ex.Message, isError: true);
        }
        finally
        {
            BtnToggleHello.IsEnabled = true;
        }
    }

    /// <summary>Issue #4/#12: arming self-destruct is a deliberate, typed confirmation.</summary>
    private async void ChkSelfDestruct_Click(object? sender, RoutedEventArgs e)
    {
        if (ChkSelfDestruct.IsChecked != true) { UpdateSelfDestructNote(); return; }

        if (!SessionManager.Current.HasRecoveryCode)
        {
            ChkSelfDestruct.IsChecked = false;
            UpdateSelfDestructNote();
            await Dialogs.MessageAsync(this, "Create a Recovery Code first",
                "Self-destruct permanently erases the vault. Arming it without a Recovery Code means one bad week of typos could destroy every credential you have. " +
                "Create a Recovery Code, then try again.", isError: true);
            return;
        }

        bool go = await Dialogs.ConfirmAsync(this, "Arm self-destruct?",
            "If the wrong password is entered too many times inside the counting window, this vault file will be overwritten and deleted. " +
            "There is no undo and no support line. Type ERASE to confirm.",
            confirmText: "Arm self-destruct", danger: true, typeToConfirm: "ERASE");

        ChkSelfDestruct.IsChecked = go;
        UpdateSelfDestructNote();
    }

    private async void BtnSave_Click(object? sender, RoutedEventArgs e)
    {
        var settings = SessionManager.Current.Payload?.Settings;
        var file = SessionManager.Current.File;
        if (settings == null || file == null) { Close(); return; }

        // Issue #4/#15: values are validated instead of being accepted blindly.
        if (!int.TryParse((TxtLockMinutes.Text ?? "").Trim(), out int lockMinutes) ||
            lockMinutes < 1 || lockMinutes > 1440)
        {
            ShowError("Auto-lock must be a whole number of minutes between 1 and 1440.");
            return;
        }

        int attempts = file.Policy.MaxAttempts;
        int window = file.Policy.WindowMinutes;
        bool armed = ChkSelfDestruct.IsChecked == true;

        if (armed)
        {
            if (!int.TryParse((TxtSelfDestructAttempts.Text ?? "").Trim(), out attempts) ||
                attempts < 5 || attempts > 500)
            {
                ShowError("Self-destruct needs an attempt limit between 5 and 500.");
                return;
            }
            if (!int.TryParse((TxtSelfDestructWindow.Text ?? "").Trim(), out window) ||
                window < 1 || window > 10080)
            {
                ShowError("The counting window must be between 1 and 10080 minutes (7 days).");
                return;
            }
        }

        settings.LockMinutes = lockMinutes;
        settings.KillSessionsOnUsbRemoval = ChkKillSessions.IsChecked == true;
        settings.ForceMultiMon = ChkForceMultiMon.IsChecked == true;
        settings.DeepSweep = ChkDeepSweep.IsChecked == true;
        settings.WarnIfDriveNotEncrypted = ChkWarnBitLocker.IsChecked == true;
        settings.SweepScope = CmbSweepScope.SelectedIndex == 1 ? SweepScope.Everything : SweepScope.OwnHostsOnly;

        file.Policy.SelfDestructEnabled = armed;
        file.Policy.MaxAttempts = attempts;
        file.Policy.WindowMinutes = window;

        if (!SessionManager.Current.TrySave(out string? error))
        {
            await Dialogs.MessageAsync(this, "Settings were not saved",
                error ?? "The vault could not be saved.", isError: true);
            return;
        }
        Close();
    }

    private void ShowError(string message)
    {
        TxtError.Text = message;
        TxtError.IsVisible = true;
    }

    private void BtnCancel_Click(object? sender, RoutedEventArgs e) => Close();
}
