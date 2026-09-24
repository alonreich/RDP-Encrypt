using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Button = Avalonia.Controls.Button;
using CheckBox = Avalonia.Controls.CheckBox;
using ProgressBar = Avalonia.Controls.ProgressBar;
using TextBox = Avalonia.Controls.TextBox;
using Orientation = Avalonia.Layout.Orientation;
using Avalonia.Platform.Storage;
using RDPVault;
using RDPVault.Android.Net;
using RDPVault.Android.Platform;
using RDPVault.Android.Rdp;
using RDPVault.Android.Security;
using RDPVault.Android.Services;

namespace RDPVault.Android.Views;

public partial class MainView : UserControl
{
    private VaultFile? _vaultFile;
    private VaultPayload? _payload;
    private byte[]? _masterKey;
    private RdpProfile? _editingProfile;
    private RdpProfile? _activeLaunchProfile;
    private string _activeRecoveryCode = "";
    private CancellationTokenSource? _connectCts;
    private CancellationTokenSource? _searchCts;
    private bool _biometricPromptSuppressed;
    private volatile bool _skipWolWait;
    private bool _isFormattingRecovery;
    private byte[]? _stagedRestoreBytes;
    private VaultFile? _stagedRestoreVaultFile;
    private readonly UnlockThrottle _unlockThrottle = new();
    private bool _isLaunching;
    private DateTime _lastSensitiveCopyUtc = DateTime.MinValue;
    private string? _lastSensitiveCopiedText;

    /// <summary>
    /// Guards the Settings screen while it is being populated. Without it, assigning
    /// SelectedIndex to a ComboBox during load fires SelectionChanged, which used to write
    /// a half-initialised VaultSettings straight back to disk - the bug where merely
    /// OPENING Settings flipped the user's scaling choice (audit item 6).
    /// </summary>
    private bool _suppressSettingsSave;

    // ---- Foreground inactivity auto-lock (audit item 3 / suggestion 7) ----
    private DispatcherTimer? _idleTimer;
    private DateTime _lastActivityUtc = DateTime.UtcNow;
    private bool _idleTimerSuspended;

    // ---- Pre-flight reachability decision plumbing (suggestion 10) ----
    private enum UnreachableChoice { Cancel = 0, ConnectAnyway = 1, Retry = 2, SendWol = 3 }
    private TaskCompletionSource<UnreachableChoice>? _unreachableChoiceTcs;

    // ---- Deferred biometric seal repair (Finding 3) ----
    private bool _pendingSealRepair;

    private struct ProfileEditorState
    {
        public string Name;
        public string Host;
        public string Port;
        public string User;
        public string Pass;
        public string Gateway;
        public int ResIndex;
        public string CustomWidth;
        public string CustomHeight;
        public int MultiMonIndex;
        public bool PreserveNative;
        public bool EnableWol;
        public string WolMac;
        public string WolPort;
        public string WolWait;
        public bool EnableKnock;
        public string KnockSignature;
        public bool SuppressCert;
        public string Notes;
    }
    private ProfileEditorState _editorInitialState;

    private static string ResolveVaultPath()
    {
        string baseDir = global::Android.App.Application.Context?.FilesDir?.AbsolutePath
            ?? Environment.GetFolderPath(Environment.SpecialFolder.Personal);

        if (!Directory.Exists(baseDir))
        {
            Directory.CreateDirectory(baseDir);
        }

        string standardPath = Path.Combine(baseDir, AppPaths.VaultFileName);
        if (File.Exists(standardPath)) return standardPath;

        // Legacy migration: an earlier build wrote into the Documents subfolder.
        try
        {
            string legacyDir = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
            string legacyPath = Path.Combine(legacyDir, AppPaths.VaultFileName);
            if (File.Exists(legacyPath))
            {
                File.Move(legacyPath, standardPath, overwrite: true);
                string legacyBak = legacyPath + AppPaths.BackupSuffix;
                if (File.Exists(legacyBak))
                {
                    File.Move(legacyBak, standardPath + AppPaths.BackupSuffix, overwrite: true);
                }
                return standardPath;
            }
        }
        catch { }

        return standardPath;
    }

    private string VaultPath => ResolveVaultPath();

    private static global::Android.Content.Context AndroidContext =>
        (global::Android.Content.Context?)MainActivity.Instance ?? global::Android.App.Application.Context;

    public MainView()
    {
        InitializeComponent();
        WireEvents();
        SetupIdleTimer();
        Loaded += (_, _) => CheckInitialVaultState();
    }

    // ==================================================================
    //  FOREGROUND INACTIVITY AUTO-LOCK
    // ==================================================================

    private void SetupIdleTimer()
    {
        // Every touch anywhere in the app resets the clock. MainActivity.OnUserInteraction
        // also feeds NotifyUserActivity() so events Avalonia does not surface still count.
        AddHandler(InputElement.PointerPressedEvent, (_, _) => NotifyUserActivity(), RoutingStrategies.Tunnel);
        AddHandler(InputElement.PointerReleasedEvent, (_, _) => NotifyUserActivity(), RoutingStrategies.Tunnel);
        AddHandler(InputElement.KeyDownEvent, (_, _) => NotifyUserActivity(), RoutingStrategies.Tunnel);
        AddHandler(InputElement.TextInputEvent, (_, _) => NotifyUserActivity(), RoutingStrategies.Tunnel);

        _idleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _idleTimer.Tick += (_, _) => EvaluateIdleTimeout();
        _idleTimer.Start();
    }

    public void NotifyUserActivity() => _lastActivityUtc = DateTime.UtcNow;

    public void SuspendIdleTimer() => _idleTimerSuspended = true;

    public void ResumeIdleTimer()
    {
        _idleTimerSuspended = false;
        _lastActivityUtc = DateTime.UtcNow;
    }

    private void EvaluateIdleTimeout()
    {
        if (_idleTimerSuspended) return;
        if (_masterKey == null) return;                       // already locked
        if (OverlayLaunch.IsVisible) return;                  // never lock mid-connect

        int minutes = _payload?.Settings?.LockMinutes ?? 0;
        if (minutes <= 0) return;                             // "Never"

        if ((DateTime.UtcNow - _lastActivityUtc).TotalMinutes >= minutes)
        {
            LockVault();
            TxtLockNotice.Text = $"Vault locked automatically after {minutes} minute{(minutes == 1 ? "" : "s")} without activity.";
            TxtLockNotice.IsVisible = true;
        }
    }

    // ==================================================================
    //  EVENT WIRING
    // ==================================================================

    private void WireEvents()
    {
        // 1. Password visibility toggles.
        //    NOTE: the connection PASSWORD toggle exists ONLY inside the profile editor.
        //    Cards, the launch overlay and Settings deliberately expose no way to read or
        //    copy a stored remote password.
        SetupPasswordToggle(TxtFirstRunPass, BtnToggleFirstRunPass);
        SetupPasswordToggle(TxtFirstRunConfirm, BtnToggleFirstRunConfirm);
        SetupPasswordToggle(TxtPassword, BtnToggleUnlockPass);
        SetupPasswordToggle(TxtRecoveryNewPass, BtnToggleRecoveryNewPass);
        SetupPasswordToggle(TxtSettingsCurrentPass, BtnToggleSettingsCurrentPass);
        SetupPasswordToggle(TxtSettingsNewPass, BtnToggleSettingsNewPass);
        SetupPasswordToggle(TxtSettingsConfirmPass, BtnToggleSettingsConfirmPass);
        SetupPasswordToggle(TxtVerifyPassForRecovery, BtnToggleVerifyRecoveryPass);
        SetupPasswordToggle(TxtRecoveryConfirmPass, BtnToggleRecoveryConfirmPass);
        SetupPasswordToggle(TxtProfilePass, BtnToggleProfilePass);
        SetupPasswordToggle(TxtVerifyBackupPass, BtnToggleVerifyBackupPass);

        // 2. Numeric input filtering
        RestrictToDigits(TxtProfilePort);
        RestrictToDigits(TxtProfileWolPort);
        RestrictToDigits(TxtProfileWolWait);
        RestrictToDigits(TxtProfileCustomWidth);
        RestrictToDigits(TxtProfileCustomHeight);

        // 3. First-Time Setup Wizard
        BtnFirstRunCreate.Click += async (_, _) => await CreateVaultFirstTimeAsync();
        BtnFirstRunImportExisting.Click += async (_, _) => await ImportVaultFileAsync();

        // 4. Recovery Code Display - three independent escape routes
        BtnCopyRecoveryCode.Click += (_, _) => CopyRecoveryCodeToClipboard();
        BtnSaveRecoveryCodeFile.Click += async (_, _) => await SaveRecoveryCodeToFileAsync();
        BtnShareRecoveryCode.Click += (_, _) => ShareRecoveryCodeText();
        ChkConfirmRecoverySaved.IsCheckedChanged += (_, _) =>
        {
            BtnFinishSetupAndEnter.IsEnabled = ChkConfirmRecoverySaved.IsChecked == true;
        };
        BtnFinishSetupAndEnter.Click += (_, _) =>
        {
            _activeRecoveryCode = "";
            if (_payload != null)
            {
                _payload.PendingRecoveryCode = "";
                SaveVault();
            }
            PanelRecoveryDisplay.IsVisible = false;
            SwitchToUnlocked();
        };

        // 5. Lock Screen
        BtnBiometricUnlock.Click += async (_, _) =>
        {
            _biometricPromptSuppressed = false;
            await UnlockWithBiometricsAsync();
        };
        BtnUnlock.Click += async (_, _) => await UnlockWithPasswordAsync();
        TxtPassword.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter) await UnlockWithPasswordAsync();
        };
        BtnShowRecovery.Click += (_, _) => ShowRecoveryUnlock();
        BtnLockRestoreBackup.Click += async (_, _) => await RestoreVaultFromFileAsync();

        // 6. Recovery Unlock
        BtnPasteRecoveryCode.Click += async (_, _) => await PasteRecoveryCodeAsync();
        BtnClearRecoveryInput.Click += (_, _) =>
        {
            _isFormattingRecovery = true;
            try { TxtRecoveryInput.Text = ""; }
            finally { _isFormattingRecovery = false; }
            TxtRecoveryCharCount.Text = "0 / 52 characters";
            TxtRecoveryCharCount.Foreground = new SolidColorBrush(Color.Parse("#9A9AA3"));
        };
        TxtRecoveryInput.TextChanged += (_, _) => OnRecoveryInputChanged();
        BtnSubmitRecoveryUnlock.Click += async (_, _) => await SubmitRecoveryUnlockAsync();
        BtnCancelRecoveryUnlock.Click += (_, _) => ShowLockScreen();

        // 7. Unlocked Screen
        BtnAddProfile.Click += (_, _) => ShowProfileEditor(null);
        BtnSettings.Click += (_, _) => ShowSettings();
        BtnLock.Click += (_, _) => LockVault();

        TxtSearch.TextChanged += (_, _) =>
        {
            _searchCts?.Cancel();
            var cts = new CancellationTokenSource();
            _searchCts = cts;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(150, cts.Token);
                    if (!cts.Token.IsCancellationRequested)
                    {
                        await Dispatcher.UIThread.InvokeAsync(RefreshProfilesList);
                    }
                }
                catch (TaskCanceledException) { }
                catch (OperationCanceledException) { }
            });
        };
        BtnClearSearch.Click += (_, _) =>
        {
            _searchCts?.Cancel();
            TxtSearch.Text = "";
            RefreshProfilesList();
        };

        BtnReturnToRdp.Click += (_, _) => RdpLauncher.ResumeRemoteDesktop(AndroidContext);
        BtnEndActiveSession.Click += (_, _) => OverlayEndSession.IsVisible = true;

        BtnEndSessionOpenRdp.Click += (_, _) =>
        {
            OverlayEndSession.IsVisible = false;
            RdpLauncher.ResumeRemoteDesktop(AndroidContext);
        };
        BtnEndSessionStopTracking.Click += (_, _) =>
        {
            OverlayEndSession.IsVisible = false;
            EndActiveSession();
        };
        BtnEndSessionCancel.Click += (_, _) => OverlayEndSession.IsVisible = false;

        // Backup reminder banner
        BtnBackupNow.Click += async (_, _) => await ShareVaultBackupAsync();
        BtnDismissBackupReminder.Click += (_, _) =>
        {
            AppPrefs.DismissBackupReminder();
            BannerBackupReminder.IsVisible = false;
        };

        // 8. Profile Editor - Save and Cancel in header and bottom, plus knock
        BtnTopSaveProfile.Click += (_, _) => SaveProfile();
        BtnTopCancelProfile.Click += (_, _) => OnCancelProfileEditor();
        BtnBottomSaveProfile.Click += (_, _) => SaveProfile();
        BtnBottomCancelProfile.Click += (_, _) => OnCancelProfileEditor();
        BtnKeepEditingProfile.Click += (_, _) => OverlayConfirmDiscardProfile.IsVisible = false;
        BtnConfirmDiscardProfile.Click += (_, _) =>
        {
            OverlayConfirmDiscardProfile.IsVisible = false;
            PanelProfileEditor.IsVisible = false;
            PanelUnlocked.IsVisible = true;
        };

        BtnToggleAdvanced.Click += (_, _) => SetAdvancedVisible(!PnlProfileAdvanced.IsVisible);

        ChkProfileEnableKnock.IsCheckedChanged += (_, _) =>
        {
            PnlKnockDetails.IsVisible = ChkProfileEnableKnock.IsChecked == true;
        };
        BtnGenerateKnockSignature.Click += (_, _) =>
        {
            TxtProfileKnockSignature.Text = IcmpKnock.GenerateSignature();
        };

        ChkProfileEnableWol.IsCheckedChanged += (_, _) =>
        {
            PnlWolDetails.IsVisible = ChkProfileEnableWol.IsChecked == true;
        };
        TxtProfileWolMac.LostFocus += (_, _) =>
        {
            if (MacAddressHelper.TryNormalizeMac(TxtProfileWolMac.Text, out string formatted, out _))
            {
                TxtProfileWolMac.Text = formatted;
            }
        };

        BtnDeleteProfile.Click += (_, _) =>
        {
            if (_editingProfile != null)
            {
                TxtConfirmDeleteMessage.Text = $"Are you sure you want to delete '{_editingProfile.Name}' ({_editingProfile.Host})? This cannot be undone.";
                OverlayConfirmDelete.IsVisible = true;
            }
        };
        BtnCancelDelete.Click += (_, _) => OverlayConfirmDelete.IsVisible = false;
        BtnConfirmDelete.Click += (_, _) =>
        {
            OverlayConfirmDelete.IsVisible = false;
            ConfirmDeleteProfile();
        };

        CmbProfileResolution.SelectionChanged += (_, _) =>
        {
            int idx = CmbProfileResolution.SelectedIndex;
            PnlProfileCustomRes.IsVisible = idx == 8;
            if (idx == 7) // Match Mobile Device Screen
            {
                ChkProfilePreserveNative.IsChecked = false;
            }
            UpdateProfilePreserveNativeHint();
        };
        TxtProfileCustomWidth.TextChanged += (_, _) => UpdateProfilePreserveNativeHint();
        TxtProfileCustomHeight.TextChanged += (_, _) => UpdateProfilePreserveNativeHint();
        ChkProfilePreserveNative.IsCheckedChanged += (_, _) => UpdateProfilePreserveNativeHint();

        // Suggestion 8: no hardcoded pixel jumps. Whatever gains focus scrolls itself into
        // view above the soft keyboard, on any screen size or keyboard height.
        PanelProfileEditor.AddHandler(InputElement.GotFocusEvent, OnEditorChildGotFocus, RoutingStrategies.Bubble);
        PanelSettings.AddHandler(InputElement.GotFocusEvent, OnEditorChildGotFocus, RoutingStrategies.Bubble);

        // 9. Settings
        BtnTopCloseSettings.Click += (_, _) =>
        {
            PanelSettings.IsVisible = false;
            PanelUnlocked.IsVisible = true;
            RefreshBackupReminder();
        };
        BtnToggleBiometrics.Click += async (_, _) => await ToggleBiometricsAsync();
        BtnExportVault.Click += async (_, _) => await ExportVaultFileAsync();
        BtnShareVault.Click += async (_, _) => await ShareVaultBackupAsync();
        BtnImportVault.Click += async (_, _) => await RestoreVaultFromFileAsync();
        BtnCancelRestoreVault.Click += (_, _) =>
        {
            _stagedRestoreBytes = null;
            _stagedRestoreVaultFile = null;
            OverlayConfirmRestoreVault.IsVisible = false;
        };
        BtnConfirmRestoreVault.Click += async (_, _) => await ExecuteVaultRestoreAsync();
        BtnCancelVerifyBackupPass.Click += (_, _) =>
        {
            OverlayVerifyBackupPassword.IsVisible = false;
            _stagedRestoreBytes = null;
            _stagedRestoreVaultFile = null;
        };
        BtnSubmitVerifyBackupPass.Click += async (_, _) => await SubmitVerifyBackupPasswordAsync();
        TxtVerifyBackupPass.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter) await SubmitVerifyBackupPasswordAsync();
        };
        BtnRegenerateRecoveryCode.Click += (_, _) =>
        {
            TxtVerifyPassForRecovery.Text = "";
            TxtVerifyPassForRecoveryError.IsVisible = false;
            OverlayPromptPasswordForRecovery.IsVisible = true;
        };
        BtnCancelVerifyPassForRecovery.Click += (_, _) => OverlayPromptPasswordForRecovery.IsVisible = false;
        BtnSubmitVerifyPassForRecovery.Click += async (_, _) => await SubmitRegenerateRecoveryCodeAsync();
        BtnChangeMasterPassword.Click += async (_, _) => await ChangeMasterPasswordAsync();

        ChkSettingsAllowScreenshots.IsCheckedChanged += (_, _) =>
        {
            if (_suppressSettingsSave) return;
            AppPrefs.SetBool(AppPrefs.KeyAllowScreenshots, ChkSettingsAllowScreenshots.IsChecked == true);
            MainActivity.Instance?.ApplyScreenSecurity();
        };

        BtnResetAutoTypePrompt.Click += (_, _) =>
        {
            AppPrefs.SetBool(AppPrefs.KeyAutoTypePromptSuppressed, false);
            BtnResetAutoTypePrompt.IsVisible = false;
            TxtVaultBackupStatus.IsVisible = false;
            TxtAccessibilityStatus.Text = "The Auto-Type prompt will be offered again the next time you connect.";
            TxtAccessibilityStatus.Foreground = new SolidColorBrush(Color.Parse("#2FBF71"));
        };

        CmbSettingsAutoLock.SelectionChanged += (_, _) => SaveSettingsDefaults();
        ChkSettingsLockOnBackground.IsCheckedChanged += (_, _) => SaveSettingsDefaults();
        CmbSettingsResolution.SelectionChanged += (_, _) =>
        {
            if (!_suppressSettingsSave && CmbSettingsResolution.SelectedIndex == 6)
            {
                // "Match Mobile Device Screen" has no fixed native size to preserve.
                ChkSettingsPreserveNative.IsChecked = false;
            }
            UpdateSettingsPreserveNativeHint();
            SaveSettingsDefaults();
        };
        CmbSettingsMultiMon.SelectionChanged += (_, _) => SaveSettingsDefaults();
        ChkSettingsPreserveNative.IsCheckedChanged += (_, _) =>
        {
            UpdateSettingsPreserveNativeHint();
            SaveSettingsDefaults();
        };
        ChkSettingsSuppressCert.IsCheckedChanged += (_, _) => SaveSettingsDefaults();
        ChkSettingsPreflight.IsCheckedChanged += (_, _) =>
        {
            if (_suppressSettingsSave) return;
            AppPrefs.SetBool(AppPrefs.KeyPreflightEnabled, ChkSettingsPreflight.IsChecked == true);
        };

        // 10. Auto-Type Accessibility
        BtnSettingsAccessibility.Click += (_, _) =>
        {
            MainActivity.Instance?.BeginExternalActivity();
            RdpAutoTypeService.OpenAccessibilitySettings(AndroidContext);
        };
        BtnSettingsAppInfo.Click += (_, _) =>
        {
            MainActivity.Instance?.BeginExternalActivity();
            RdpAutoTypeService.OpenAppInfo(AndroidContext);
        };
        BtnOpenAccessibilitySettings.Click += (_, _) =>
        {
            OverlayEnableAccessibility.IsVisible = false;
            MainActivity.Instance?.BeginExternalActivity();
            RdpAutoTypeService.OpenAccessibilitySettings(AndroidContext);
        };
        BtnOpenAppInfo.Click += (_, _) =>
        {
            MainActivity.Instance?.BeginExternalActivity();
            RdpAutoTypeService.OpenAppInfo(AndroidContext);
        };
        BtnContinueWithoutAccessibility.Click += async (_, _) =>
        {
            PersistAccessibilityPromptChoice();
            OverlayEnableAccessibility.IsVisible = false;
            if (_activeLaunchProfile != null)
            {
                await ProceedLaunchAsync(_activeLaunchProfile);
            }
        };
        BtnCancelAccessibilityPrompt.Click += (_, _) =>
        {
            PersistAccessibilityPromptChoice();
            OverlayEnableAccessibility.IsVisible = false;
        };

        // 11. Launch overlay controls
        BtnSkipWolWait.Click += (_, _) => SkipWolWait();
        BtnCancelLaunch.Click += (_, _) => CancelLaunch();
        BtnLaunchConnectAnyway.Click += (_, _) => _unreachableChoiceTcs?.TrySetResult(UnreachableChoice.ConnectAnyway);
        BtnLaunchRetryProbe.Click += (_, _) => _unreachableChoiceTcs?.TrySetResult(UnreachableChoice.Retry);
        BtnLaunchSendWol.Click += (_, _) => _unreachableChoiceTcs?.TrySetResult(UnreachableChoice.SendWol);

        // 12. Biometric seal repair
        BtnSkipRepairBiometrics.Click += (_, _) =>
        {
            _pendingSealRepair = false;
            OverlayRepairBiometrics.IsVisible = false;
        };
        BtnDoRepairBiometrics.Click += async (_, _) => await RepairBiometricSealAsync();
    }

    private void PersistAccessibilityPromptChoice()
    {
        if (ChkDontAskAccessibilityAgain.IsChecked == true)
        {
            AppPrefs.SetBool(AppPrefs.KeyAutoTypePromptSuppressed, true);
        }
    }

    private void OnEditorChildGotFocus(object? sender, GotFocusEventArgs e)
    {
        if (e.Source is not Control c) return;
        Dispatcher.UIThread.Post(() =>
        {
            try { c.BringIntoView(); } catch { }
        }, DispatcherPriority.Background);
    }

    private void SetAdvancedVisible(bool visible)
    {
        PnlProfileAdvanced.IsVisible = visible;
        BtnToggleAdvanced.Content = visible
            ? "▾  Advanced options (display, wake-on-LAN, gateway, notes)"
            : "▸  Advanced options (display, wake-on-LAN, gateway, notes)";
    }

    // ==================================================================
    //  RESOLUTION LABELS (suggestion 2 / audit item 8)
    // ==================================================================

    /// <summary>
    /// The resolution the profile editor is currently configured for, as a human string.
    /// The old UI hardcoded "1920x1080" into the checkbox label no matter what the user
    /// picked, which read as "the app ignored my choice".
    /// </summary>
    private string CurrentEditorResolutionLabel()
    {
        int idx = CmbProfileResolution.SelectedIndex;
        switch (idx)
        {
            case 1: return "1920x1080";
            case 2: return "1280x720";
            case 3: return "1600x900";
            case 4: return "1366x768";
            case 5: return "2560x1440";
            case 6: return "3840x2160";
            case 7: return "your phone's screen size";
            case 8:
                string w = string.IsNullOrWhiteSpace(TxtProfileCustomWidth.Text) ? "1920" : TxtProfileCustomWidth.Text!.Trim();
                string h = string.IsNullOrWhiteSpace(TxtProfileCustomHeight.Text) ? "1080" : TxtProfileCustomHeight.Text!.Trim();
                return $"{w}x{h}";
            default:
                string global = _payload?.Settings?.DefaultResolution ?? "1920x1080";
                return global.Equals("Device", StringComparison.OrdinalIgnoreCase) ? "your phone's screen size" : global;
        }
    }

    private void UpdateProfilePreserveNativeHint()
    {
        string res = CurrentEditorResolutionLabel();
        bool deviceNative = CmbProfileResolution.SelectedIndex == 7;

        ChkProfilePreserveNative.Content = deviceNative
            ? "Keep native size (not applicable when matching the phone screen)"
            : $"Keep native {res} (never squash or distort - scroll to view)";

        if (ChkProfilePreserveNative.IsChecked == true)
        {
            TxtProfilePreserveNativeHint.Text = deviceNative
                ? "The remote desktop is resized to fit your phone, so there is nothing to preserve."
                : $"Recommended: the Windows desktop stays at full {res} in both portrait and landscape. Nothing is squashed. You scroll side-to-side and up/down to reach the rest of the screen.";
            TxtProfilePreserveNativeHint.Foreground = new SolidColorBrush(Color.Parse("#2FBF71"));
        }
        else
        {
            TxtProfilePreserveNativeHint.Text = deviceNative
                ? "The remote desktop is resized to match your phone screen."
                : $"Warning: squeezes the whole {res} desktop into your phone screen. Apps and icons look squashed, especially in portrait.";
            TxtProfilePreserveNativeHint.Foreground = new SolidColorBrush(Color.Parse("#E5A93C"));
        }
    }

    private string CurrentSettingsResolutionLabel()
    {
        return CmbSettingsResolution.SelectedIndex switch
        {
            1 => "1280x720",
            2 => "1600x900",
            3 => "1366x768",
            4 => "2560x1440",
            5 => "3840x2160",
            6 => "your phone's screen size",
            _ => "1920x1080"
        };
    }

    private void UpdateSettingsPreserveNativeHint()
    {
        string res = CurrentSettingsResolutionLabel();
        bool deviceNative = CmbSettingsResolution.SelectedIndex == 6;

        ChkSettingsPreserveNative.Content = deviceNative
            ? "Keep native size by default (not applicable when matching the phone screen)"
            : $"Keep native {res} by default (never squash or distort - scroll to view)";

        if (ChkSettingsPreserveNative.IsChecked == true)
        {
            TxtSettingsPreserveNativeHint.Text = deviceNative
                ? "The remote desktop is resized to fit your phone, so there is nothing to preserve."
                : $"Recommended: remote sessions stay at full {res}. Prevents remote apps and desktop icons from being squashed.";
            TxtSettingsPreserveNativeHint.Foreground = new SolidColorBrush(Color.Parse("#2FBF71"));
        }
        else
        {
            TxtSettingsPreserveNativeHint.Text = deviceNative
                ? "The remote desktop is resized to match your phone screen."
                : $"Warning: squeezes {res} remote desktops into your phone screen. Apps and icons look squashed in portrait.";
            TxtSettingsPreserveNativeHint.Foreground = new SolidColorBrush(Color.Parse("#E5A93C"));
        }
    }

    private void SetupPasswordToggle(TextBox tb, Button btn)
    {
        btn.Click += (_, _) =>
        {
            if (tb.PasswordChar == '\0')
            {
                tb.PasswordChar = '●';
                btn.Content = "👁";
            }
            else
            {
                tb.PasswordChar = '\0';
                btn.Content = "🙈";
            }
        };
    }

    private static void RestrictToDigits(TextBox tb)
    {
        tb.TextInput += (_, e) =>
        {
            if (e.Text != null && !e.Text.All(char.IsDigit))
            {
                e.Handled = true;
            }
        };
    }

    // ==================================================================
    //  BACK NAVIGATION
    // ==================================================================

    public bool HandleBackPressed()
    {
        NotifyUserActivity();

        if (OverlayRepairBiometrics.IsVisible)
        {
            _pendingSealRepair = false;
            OverlayRepairBiometrics.IsVisible = false;
            return true;
        }

        if (OverlayEndSession.IsVisible)
        {
            OverlayEndSession.IsVisible = false;
            return true;
        }

        if (OverlayEnableAccessibility.IsVisible)
        {
            PersistAccessibilityPromptChoice();
            OverlayEnableAccessibility.IsVisible = false;
            return true;
        }

        if (OverlayPromptPasswordForRecovery.IsVisible)
        {
            OverlayPromptPasswordForRecovery.IsVisible = false;
            return true;
        }

        if (OverlayConfirmDiscardProfile.IsVisible)
        {
            OverlayConfirmDiscardProfile.IsVisible = false;
            return true;
        }

        if (OverlayConfirmRestoreVault.IsVisible)
        {
            OverlayConfirmRestoreVault.IsVisible = false;
            _stagedRestoreBytes = null;
            return true;
        }

        if (OverlayConfirmDelete.IsVisible)
        {
            OverlayConfirmDelete.IsVisible = false;
            return true;
        }

        if (OverlayLaunch.IsVisible)
        {
            CancelLaunch();
            return true;
        }

        if (PanelProfileEditor.IsVisible)
        {
            OnCancelProfileEditor();
            return true;
        }

        if (PanelSettings.IsVisible)
        {
            PanelSettings.IsVisible = false;
            PanelUnlocked.IsVisible = true;
            RefreshBackupReminder();
            return true;
        }

        if (PanelRecoveryUnlock.IsVisible)
        {
            ShowLockScreen();
            return true;
        }

        // Recovery code screen: the ONLY way off it is ticking the confirmation box.
        // Losing this code means losing the vault, so Back is deliberately trapped here
        // (audit item 7).
        if (PanelRecoveryDisplay.IsVisible)
        {
            if (ChkConfirmRecoverySaved.IsChecked == true && _payload != null)
            {
                _activeRecoveryCode = "";
                PanelRecoveryDisplay.IsVisible = false;
                SwitchToUnlocked();
            }
            else
            {
                TxtRecoveryGateHint.Text = "Save the code first, then tick the box below it. If you lose this code and forget your master password, the vault can never be opened again.";
                TxtRecoveryGateHint.Foreground = new SolidColorBrush(Color.Parse("#E8A030"));
            }
            return true;
        }

        if (PanelUnlocked.IsVisible && !string.IsNullOrEmpty(TxtSearch.Text))
        {
            _searchCts?.Cancel();
            TxtSearch.Text = "";
            RefreshProfilesList();
            return true;
        }

        // Root level: MainActivity decides whether to lock before minimising.
        return false;
    }

    /// <summary>Locks the vault if it is currently open. Safe to call from any state.</summary>
    public void AutoLockIfUnlocked()
    {
        if (_masterKey != null)
        {
            LockVault();
        }
    }

    private void ShowPanel(Control panel)
    {
        PanelFirstRun.IsVisible = panel == PanelFirstRun;
        PanelRecoveryDisplay.IsVisible = panel == PanelRecoveryDisplay;
        PanelLocked.IsVisible = panel == PanelLocked;
        PanelRecoveryUnlock.IsVisible = panel == PanelRecoveryUnlock;
        PanelUnlocked.IsVisible = panel == PanelUnlocked;
        PanelProfileEditor.IsVisible = panel == PanelProfileEditor;
        PanelSettings.IsVisible = panel == PanelSettings;
    }

    // ==================================================================
    //  STATE MANAGEMENT
    // ==================================================================

    private void CheckInitialVaultState()
    {
        if (!File.Exists(VaultPath))
        {
            ShowFirstRunWizard();
        }
        else
        {
            ShowLockScreen();
        }
    }

    private void ShowFirstRunWizard()
    {
        ShowPanel(PanelFirstRun);
        TxtFirstRunPass.Text = "";
        TxtFirstRunPass.PasswordChar = '●';
        BtnToggleFirstRunPass.Content = "👁";
        TxtFirstRunConfirm.Text = "";
        TxtFirstRunConfirm.PasswordChar = '●';
        BtnToggleFirstRunConfirm.Content = "👁";
        TxtFirstRunError.IsVisible = false;
        PnlFirstRunProgress.IsVisible = false;
        BtnFirstRunCreate.IsEnabled = true;

        string? unavailable = AndroidHardwareKeyStore.DescribeBiometricUnavailability(MainActivity.Instance);
        bool bioAvail = unavailable == null;
        ChkFirstRunBiometrics.IsChecked = bioAvail;
        ChkFirstRunBiometrics.IsEnabled = bioAvail;
        if (!bioAvail)
        {
            ChkFirstRunBiometrics.Content = "Fingerprint / Face Unlock unavailable";
            TxtFirstRunBiometricsHint.Text = unavailable + " You can still create the vault and turn this on later in Settings.";
            TxtFirstRunBiometricsHint.Foreground = new SolidColorBrush(Color.Parse("#E5A93C"));
        }
    }

    private async Task CreateVaultFirstTimeAsync()
    {
        string pass = TxtFirstRunPass.Text ?? "";
        string confirm = TxtFirstRunConfirm.Text ?? "";

        if (pass.Length < 8)
        {
            TxtFirstRunError.Text = "Master password must be at least 8 characters.";
            TxtFirstRunError.IsVisible = true;
            return;
        }

        if (pass != confirm)
        {
            TxtFirstRunError.Text = "Passwords do not match. Please re-enter.";
            TxtFirstRunError.IsVisible = true;
            return;
        }

        TxtFirstRunError.IsVisible = false;
        PnlFirstRunProgress.IsVisible = true;
        BtnFirstRunCreate.IsEnabled = false;

        string recoveryCode = "";
        var payload = new VaultPayload();
        payload.Settings.SuppressCertWarnings = true;
        payload.Settings.DefaultResolution = "1920x1080";
        payload.Settings.DefaultWidth = 1920;
        payload.Settings.DefaultHeight = 1080;
        payload.Settings.DefaultSmartSizing = false;
        payload.Settings.DefaultUseMultiMon = false;   // safe single-monitor default for mobile

        // Audit item 3: a security vault must not sit unlocked for an hour by default.
        payload.Settings.LockMinutes = 1;
        payload.Settings.LockImmediatelyOnBackground = true;

        try
        {
            var file = await Task.Run(() => VaultCrypto.CreateVault(pass, payload, VaultPath, out recoveryCode, retainRecoveryUntilAcknowledged: true));
            var (masterKey, _) = await Task.Run(() => VaultCrypto.Open(file, pass));

            _vaultFile = file;
            _masterKey = masterKey;
            _payload = payload;
            ApplyLockSettingsToActivity();

            if (ChkFirstRunBiometrics.IsChecked == true && MainActivity.Instance != null)
            {
                var enroll = await AndroidHardwareKeyStore.EnrollAndSealAsync(
                    MainActivity.Instance, masterKey,
                    "Protect your vault",
                    "Confirm your fingerprint or face to seal the master key in this phone's security chip",
                    "Skip for now");

                if (enroll.Success && enroll.Seal != null && enroll.KeyId != null)
                {
                    var seals = new List<SealEntry>
                    {
                        new SealEntry
                        {
                            MachineId = VaultCrypto.CurrentMachineId(),
                            KeyId = enroll.KeyId,
                            TpmBlob = enroll.Seal
                        }
                    };
                    VaultCrypto.Save(file, masterKey, payload, VaultPath, newSeals: seals);
                    file.Seals = seals;
                    _vaultFile = file;
                }
                else if (!enroll.Cancelled && !string.IsNullOrEmpty(enroll.Error))
                {
                    // Not fatal: the vault is password-protected regardless.
                    global::Android.Util.Log.Warn("RDPVault", "Biometric enrolment skipped: " + enroll.Error);
                }
            }

            AppPrefs.MarkVaultChanged();
            ShowRecoveryDisplay(recoveryCode);
        }
        catch (Exception ex)
        {
            TxtFirstRunError.Text = "Failed to create vault: " + ex.Message;
            TxtFirstRunError.IsVisible = true;
        }
        finally
        {
            PnlFirstRunProgress.IsVisible = false;
            BtnFirstRunCreate.IsEnabled = true;
        }
    }

    private void ShowRecoveryDisplay(string code)
    {
        _activeRecoveryCode = RecoveryCode.Normalize(code);
        TxtRecoveryDisplayCode.Text = RecoveryCode.Format(_activeRecoveryCode);
        TxtCopyNotice.IsVisible = false;
        ChkConfirmRecoverySaved.IsChecked = false;
        BtnFinishSetupAndEnter.IsEnabled = false;
        TxtRecoveryGateHint.Text = "The Back button is disabled on this screen until the box above is ticked - losing this code means losing the vault.";
        TxtRecoveryGateHint.Foreground = new SolidColorBrush(Color.Parse("#8A8A93"));
        ShowPanel(PanelRecoveryDisplay);
    }

    private void CopyRecoveryCodeToClipboard()
    {
        if (string.IsNullOrEmpty(_activeRecoveryCode)) return;
        CopySensitiveTextToClipboard(RecoveryCode.Format(_activeRecoveryCode), "RDP Vault Recovery Code");
        ShowRecoveryNotice("Copied. The clipboard clears itself in 60 seconds - paste it somewhere safe now.", true);
    }

    private void ShowRecoveryNotice(string text, bool good)
    {
        TxtCopyNotice.Text = text;
        TxtCopyNotice.Foreground = new SolidColorBrush(Color.Parse(good ? "#2FBF71" : "#FF6B6B"));
        TxtCopyNotice.IsVisible = true;
    }

    /// <summary>
    /// Writes the recovery code to a plain text file the user chooses. This is the escape
    /// route that does not involve the clipboard at all (audit item 7).
    /// </summary>
    private async Task SaveRecoveryCodeToFileAsync()
    {
        if (string.IsNullOrEmpty(_activeRecoveryCode)) return;

        try
        {
            var top = TopLevel.GetTopLevel(this);
            if (top == null) return;

            MainActivity.Instance?.BeginExternalActivity();
            var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save recovery code",
                DefaultExtension = "txt",
                SuggestedFileName = $"rdpvault_recovery_{DateTime.Now:yyyyMMdd_HHmm}.txt"
            });

            if (file == null) return;

            string body =
                "RDP VAULT - EMERGENCY RECOVERY CODE\r\n" +
                "Created: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm") + "\r\n\r\n" +
                RecoveryCode.Format(_activeRecoveryCode) + "\r\n\r\n" +
                "Anyone holding this code can unlock the vault. Store it somewhere private,\r\n" +
                "and somewhere other than the phone the vault lives on.\r\n";

            await using var stream = await file.OpenWriteAsync();
            byte[] bytes = Encoding.UTF8.GetBytes(body);
            await stream.WriteAsync(bytes);

            ShowRecoveryNotice("Saved. Move it somewhere off this phone when you can.", true);
        }
        catch (Exception ex)
        {
            ShowRecoveryNotice("Could not save the file: " + ex.Message, false);
        }
    }

    /// <summary>
    /// Shares the code as plain TEXT through the Android share sheet. Deliberately not a
    /// file: this leaves no plaintext copy of the recovery code anywhere on disk.
    /// </summary>
    private void ShareRecoveryCodeText()
    {
        if (string.IsNullOrEmpty(_activeRecoveryCode)) return;

        try
        {
            var intent = new global::Android.Content.Intent(global::Android.Content.Intent.ActionSend);
            intent.SetType("text/plain");
            intent.PutExtra(global::Android.Content.Intent.ExtraSubject, "RDP Vault recovery code");
            intent.PutExtra(global::Android.Content.Intent.ExtraText, RecoveryCode.Format(_activeRecoveryCode));

            var chooser = global::Android.Content.Intent.CreateChooser(intent, "Send recovery code to...");
            chooser?.AddFlags(global::Android.Content.ActivityFlags.NewTask);

            MainActivity.Instance?.BeginExternalActivity();
            AndroidContext.StartActivity(chooser);
            ShowRecoveryNotice("Sent to the share sheet.", true);
        }
        catch (Exception ex)
        {
            ShowRecoveryNotice("Could not open the share sheet: " + ex.Message, false);
        }
    }

    // ==================================================================
    //  LOCK SCREEN & UNLOCK
    // ==================================================================

    private void ShowLockScreen()
    {
        ShowPanel(PanelLocked);
        TxtPassword.Text = "";
        TxtPassword.PasswordChar = '●';
        BtnToggleUnlockPass.Content = "👁";
        TxtLockNotice.IsVisible = false;
        TxtLockError.IsVisible = false;
        PnlUnlockProgress.IsVisible = false;
        BtnUnlock.IsEnabled = true;

        try
        {
            if (File.Exists(VaultPath))
            {
                byte[] raw = File.ReadAllBytes(VaultPath);
                _vaultFile = VaultValidation.Read(raw);
            }
        }
        catch
        {
            _vaultFile = null;
        }

        var seal = FindDeviceSeal();
        PnlBiometricCard.IsVisible = seal != null;

        if (seal != null && !_biometricPromptSuppressed && MainActivity.Instance != null)
        {
            Dispatcher.UIThread.Post(async () => await UnlockWithBiometricsAsync(), DispatcherPriority.Background);
        }
    }

    private SealEntry? FindDeviceSeal()
    {
        string machineId = VaultCrypto.CurrentMachineId();
        return _vaultFile?.Seals?.FirstOrDefault(s =>
            s.MachineId == machineId && !string.IsNullOrEmpty(s.KeyId) && !string.IsNullOrEmpty(s.TpmBlob));
    }

    private async Task UnlockWithBiometricsAsync()
    {
        if (MainActivity.Instance == null || _vaultFile == null) return;
        TxtLockNotice.IsVisible = false;

        var seal = FindDeviceSeal();
        if (seal == null)
        {
            TxtLockError.Text = "Fingerprint unlock is not set up on this device. Use your master password.";
            TxtLockError.IsVisible = true;
            return;
        }

        var result = await AndroidHardwareKeyStore.AuthenticateAndUnsealAsync(
            MainActivity.Instance,
            seal.KeyId,
            seal.TpmBlob,
            "Unlock RDP Vault",
            "Confirm your fingerprint or face to open your connections",
            "Use Master Password");

        if (result.Success && result.MasterKey != null)
        {
            try
            {
                var payload = VaultCrypto.OpenPayload(_vaultFile, result.MasterKey);
                _masterKey = result.MasterKey;
                _payload = payload;
                _biometricPromptSuppressed = false;
                ApplyLockSettingsToActivity();
                SwitchToUnlocked();
            }
            catch (Exception ex)
            {
                CryptographicOperations.ZeroMemory(result.MasterKey);
                TxtLockError.Text = "The vault could not be opened with the stored key: " + ex.Message;
                TxtLockError.IsVisible = true;
            }
            return;
        }

        _biometricPromptSuppressed = true;

        if (result.Invalidated)
        {
            // Finding 3: the seal is genuinely dead. Say so plainly and offer to rebuild it
            // AFTER a master-password unlock - never silently regenerate it.
            _pendingSealRepair = true;
            PnlBiometricCard.IsVisible = false;
            TxtLockError.Text = "Fingerprint unlock stopped working - a new fingerprint or face was added to this phone, or Android was updated. Unlock with your master password and RDP Vault will offer to set it up again.";
            TxtLockError.IsVisible = true;
            return;
        }

        if (!result.Cancelled && !string.IsNullOrEmpty(result.Error))
        {
            TxtLockError.Text = "Fingerprint unlock: " + result.Error;
            TxtLockError.IsVisible = true;
        }
    }

    private async Task UnlockWithPasswordAsync()
    {
        string password = TxtPassword.Text ?? "";
        if (string.IsNullOrEmpty(password))
        {
            TxtLockError.Text = "Please enter your master password.";
            TxtLockError.IsVisible = true;
            return;
        }

        PnlUnlockProgress.IsVisible = true;
        BtnUnlock.IsEnabled = false;
        TxtLockNotice.IsVisible = false;
        TxtLockError.IsVisible = false;

        VaultFile? file = null;
        try
        {
            if (!File.Exists(VaultPath))
            {
                ShowFirstRunWizard();
                return;
            }

            byte[] raw = await File.ReadAllBytesAsync(VaultPath);
            file = VaultValidation.Read(raw);

            var remaining = _unlockThrottle.Remaining(file);
            if (remaining.TotalSeconds > 0.5)
            {
                int sec = (int)Math.Ceiling(remaining.TotalSeconds);
                TxtLockError.Text = $"Too many failed attempts. Please wait {sec} second{(sec == 1 ? "" : "s")}.";
                TxtLockError.IsVisible = true;
                return;
            }

            var (master, payload) = await Task.Run(() => VaultCrypto.Open(file, password));
            _unlockThrottle.Clear(file, VaultPath);
            _vaultFile = file;
            _masterKey = master;
            _payload = payload;
            ApplyLockSettingsToActivity();

            TxtPassword.Text = "";
            _biometricPromptSuppressed = false;

            // Finding 3: detect a dead seal, but NEVER rebuild it silently. Rebuilding
            // requires a fresh biometric authorisation, so it needs the user's consent.
            string machineId = VaultCrypto.CurrentMachineId();
            var deviceSeal = file.Seals?.FirstOrDefault(s => s.MachineId == machineId);
            if (deviceSeal != null && (string.IsNullOrEmpty(deviceSeal.KeyId) || !AndroidHardwareKeyStore.HasKey(deviceSeal.KeyId)))
            {
                _pendingSealRepair = true;
            }

            SwitchToUnlocked();

            if (_pendingSealRepair && AndroidHardwareKeyStore.DescribeBiometricUnavailability(MainActivity.Instance) == null)
            {
                OverlayRepairBiometrics.IsVisible = true;
            }
        }
        catch (Exception ex)
        {
            if (file != null && ex is CryptographicException or InvalidDataException)
            {
                try { _unlockThrottle.Record(file, VaultPath); } catch { }
            }
            TxtLockError.Text = "Unlock failed: " + (ex is InvalidDataException ? "Incorrect master password." : ex.Message);
            TxtLockError.IsVisible = true;
        }
        finally
        {
            PnlUnlockProgress.IsVisible = false;
            BtnUnlock.IsEnabled = true;
        }
    }

    private async Task RepairBiometricSealAsync()
    {
        OverlayRepairBiometrics.IsVisible = false;
        _pendingSealRepair = false;

        if (MainActivity.Instance == null || _vaultFile == null || _masterKey == null || _payload == null) return;

        string machineId = VaultCrypto.CurrentMachineId();

        // Drop the dead alias before creating a new one so the Keystore does not accumulate
        // orphaned keys across OS updates.
        var old = _vaultFile.Seals?.FirstOrDefault(s => s.MachineId == machineId);
        if (old != null && !string.IsNullOrEmpty(old.KeyId))
        {
            AndroidHardwareKeyStore.DeleteHardwareKey(old.KeyId);
        }

        var enroll = await AndroidHardwareKeyStore.EnrollAndSealAsync(
            MainActivity.Instance, _masterKey,
            "Set up fingerprint unlock again",
            "Confirm your fingerprint or face to re-seal the master key",
            "Cancel");

        if (enroll.Success && enroll.Seal != null && enroll.KeyId != null)
        {
            var seals = (_vaultFile.Seals ?? new List<SealEntry>()).Where(s => s.MachineId != machineId).ToList();
            seals.Add(new SealEntry { MachineId = machineId, KeyId = enroll.KeyId, TpmBlob = enroll.Seal });
            VaultCrypto.Save(_vaultFile, _masterKey, _payload, VaultPath, newSeals: seals);
            _vaultFile.Seals = seals;
            TxtUnlockedStatus.Text = "Vault Unlocked (Fingerprint / Face Protected)";
        }
    }

    private void ShowRecoveryUnlock()
    {
        ShowPanel(PanelRecoveryUnlock);
        _isFormattingRecovery = true;
        try { TxtRecoveryInput.Text = ""; }
        finally { _isFormattingRecovery = false; }
        TxtRecoveryCharCount.Text = "0 / 52 characters";
        TxtRecoveryCharCount.Foreground = new SolidColorBrush(Color.Parse("#9A9AA3"));
        TxtRecoveryNewPass.Text = "";
        TxtRecoveryNewPass.PasswordChar = '●';
        BtnToggleRecoveryNewPass.Content = "👁";
        TxtRecoveryConfirmPass.Text = "";
        TxtRecoveryConfirmPass.PasswordChar = '●';
        BtnToggleRecoveryConfirmPass.Content = "👁";
        TxtRecoveryUnlockError.IsVisible = false;
        PnlRecoveryUnlockProgress.IsVisible = false;
    }

    private async Task SubmitRecoveryUnlockAsync()
    {
        string code = RecoveryCode.Normalize(TxtRecoveryInput.Text ?? "");
        string newPass = TxtRecoveryNewPass.Text ?? "";
        string confirmPass = TxtRecoveryConfirmPass.Text ?? "";

        if (code.Length != 52)
        {
            TxtRecoveryUnlockError.Text = $"The recovery code is 52 characters. You have entered {code.Length}.";
            TxtRecoveryUnlockError.IsVisible = true;
            return;
        }

        if (newPass.Length < 8)
        {
            TxtRecoveryUnlockError.Text = "New master password must be at least 8 characters.";
            TxtRecoveryUnlockError.IsVisible = true;
            return;
        }

        if (newPass != confirmPass)
        {
            TxtRecoveryUnlockError.Text = "New passwords do not match.";
            TxtRecoveryUnlockError.IsVisible = true;
            return;
        }

        PnlRecoveryUnlockProgress.IsVisible = true;
        BtnSubmitRecoveryUnlock.IsEnabled = false;
        TxtRecoveryUnlockError.IsVisible = false;

        try
        {
            if (_vaultFile == null)
            {
                string json = await File.ReadAllTextAsync(VaultPath);
                _vaultFile = System.Text.Json.JsonSerializer.Deserialize(json, VaultJsonContext.Default.VaultFile);
            }

            if (_vaultFile == null) throw new InvalidOperationException("Vault file could not be loaded.");

            var result = await Task.Run(() => VaultCrypto.OpenWithRecoveryCode(_vaultFile, code));
            if (result == null)
            {
                TxtRecoveryUnlockError.Text = "That recovery code does not match this vault. Check for mistyped characters.";
                TxtRecoveryUnlockError.IsVisible = true;
                return;
            }

            var (masterKey, payload) = result.Value;
            _masterKey = masterKey;
            _payload = payload;
            ApplyLockSettingsToActivity();

            // The master password changed, so every existing hardware seal now wraps a key
            // the user can no longer reach by biometric alone. Clear them and let the user
            // re-enrol deliberately.
            var survivingSeals = new List<SealEntry>();
            foreach (var s in _vaultFile.Seals ?? new List<SealEntry>())
            {
                if (!string.IsNullOrEmpty(s.KeyId)) AndroidHardwareKeyStore.DeleteHardwareKey(s.KeyId);
            }

            string freshCode = "";
            VaultCrypto.Save(_vaultFile, masterKey, payload, VaultPath,
                newPassword: newPass, newSeals: survivingSeals,
                regenerateRecovery: true, recoveryCodeOut: c => freshCode = c);
            _vaultFile.Seals = survivingSeals;

            _isFormattingRecovery = true;
            try { TxtRecoveryInput.Text = ""; }
            finally { _isFormattingRecovery = false; }

            AppPrefs.MarkVaultChanged();
            ShowRecoveryDisplay(freshCode);
        }
        catch (Exception ex)
        {
            TxtRecoveryUnlockError.Text = "Recovery failed: " + ex.Message;
            TxtRecoveryUnlockError.IsVisible = true;
        }
        finally
        {
            PnlRecoveryUnlockProgress.IsVisible = false;
            BtnSubmitRecoveryUnlock.IsEnabled = true;
        }
    }

    private void ApplyLockSettingsToActivity()
    {
        if (MainActivity.Instance == null || _payload?.Settings == null) return;
        MainActivity.Instance.ConfiguredLockMinutes = _payload.Settings.LockMinutes;
        MainActivity.Instance.LockImmediatelyOnBackground = _payload.Settings.LockImmediatelyOnBackground;
    }

    private void SwitchToUnlocked()
    {
        if (!string.IsNullOrEmpty(_payload?.PendingRecoveryCode))
        {
            ShowRecoveryDisplay(_payload.PendingRecoveryCode);
            return;
        }

        ShowPanel(PanelUnlocked);
        ApplyLockSettingsToActivity();
        NotifyUserActivity();

        bool hasBio = FindDeviceSeal() != null;
        TxtUnlockedStatus.Text = hasBio
            ? "Vault Unlocked (Fingerprint / Face Protected)"
            : "Vault Unlocked (Password Mode)";

        RefreshProfilesList();
        RefreshBackupReminder();
        OnAppResumed();
    }

    private void LockVault()
    {
        CancelLaunch();

        OverlayLaunch.IsVisible = false;
        OverlayConfirmDelete.IsVisible = false;
        OverlayConfirmDiscardProfile.IsVisible = false;
        OverlayVerifyBackupPassword.IsVisible = false;
        OverlayConfirmRestoreVault.IsVisible = false;
        OverlayPromptPasswordForRecovery.IsVisible = false;
        OverlayEnableAccessibility.IsVisible = false;
        OverlayEndSession.IsVisible = false;
        OverlayRepairBiometrics.IsVisible = false;

        TxtPassword.Text = "";
        TxtFirstRunPass.Text = "";
        TxtFirstRunConfirm.Text = "";
        TxtRecoveryNewPass.Text = "";
        TxtRecoveryConfirmPass.Text = "";
        TxtRecoveryInput.Text = "";
        TxtSettingsCurrentPass.Text = "";
        TxtSettingsNewPass.Text = "";
        TxtSettingsConfirmPass.Text = "";
        TxtVerifyPassForRecovery.Text = "";
        TxtVerifyBackupPass.Text = "";
        TxtProfilePass.Text = "";
        TxtRecoveryDisplayCode.Text = "";

        _payload = null;
        if (_masterKey != null)
        {
            CryptographicOperations.ZeroMemory(_masterKey);
            _masterKey = null;
        }
        _vaultFile = null;
        _editingProfile = null;
        _activeLaunchProfile = null;
        _activeRecoveryCode = "";
        _stagedRestoreBytes = null;
        _stagedRestoreVaultFile = null;
        _biometricPromptSuppressed = false;

        // Any armed auto-type credential dies with the lock.
        RdpAutoTypeService.Disarm();

        // Locking the vault does NOT end a remote session - that session belongs to the
        // external RDP client and is none of our business. The banner state is rebuilt
        // from the service on the next unlock.
        PnlProfilesList.Children.Clear();
        TxtSearch.Text = "";

        ShowLockScreen();
        TxtLockNotice.Text = "Vault locked";
        TxtLockNotice.IsVisible = true;
    }

    // ==================================================================
    //  PROFILES
    // ==================================================================

    private void SaveVault()
    {
        if (_payload == null || _vaultFile == null || _masterKey == null) return;
        VaultCrypto.Save(_vaultFile, _masterKey, _payload, VaultPath);
        AppPrefs.MarkVaultChanged();
        RefreshBackupReminder();
    }

    private void RefreshBackupReminder()
    {
        bool hasProfiles = (_payload?.Profiles?.Count ?? 0) > 0;
        BannerBackupReminder.IsVisible = PanelUnlocked.IsVisible && hasProfiles && AppPrefs.ShouldNagAboutBackup();
    }

    private void RefreshProfilesList()
    {
        PnlProfilesList.Children.Clear();
        if (_payload?.Profiles == null || _payload.Profiles.Count == 0)
        {
            TxtEmptyProfiles.Text = "No connections yet. Tap '+ Add' above to configure your first computer.";
            TxtEmptyProfiles.IsVisible = true;
            return;
        }

        string query = TxtSearch.Text?.Trim() ?? "";
        var profiles = _payload.Profiles.AsEnumerable();

        if (!string.IsNullOrEmpty(query))
        {
            profiles = profiles.Where(p =>
                p.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                p.Host.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                p.Username.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                p.GatewayHost.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                p.Notes.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        var list = profiles.ToList();
        if (list.Count == 0)
        {
            TxtEmptyProfiles.Text = $"No connections found matching '{query}'.";
            TxtEmptyProfiles.IsVisible = true;
        }
        else
        {
            TxtEmptyProfiles.IsVisible = false;
            foreach (var profile in list)
            {
                PnlProfilesList.Children.Add(CreateProfileCard(profile));
            }
        }
    }

    /// <summary>
    /// Builds one chip. Chips replace the old single cramped line that ran
    /// "host:port • user • WOL • 1080p (Scrollable)" off the right edge of the screen
    /// (suggestion 4).
    /// </summary>
    private static Border MakeChip(string text, string foreground, string background, string border)
    {
        return new Border
        {
            Background = new SolidColorBrush(Color.Parse(background)),
            BorderBrush = new SolidColorBrush(Color.Parse(border)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 3),
            Margin = new Thickness(0, 0, 6, 6),
            Child = new TextBlock
            {
                Text = text,
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.Parse(foreground)),
                TextWrapping = TextWrapping.NoWrap
            }
        };
    }

    private Control CreateProfileCard(RdpProfile profile)
    {
        var border = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#141417")),
            CornerRadius = new CornerRadius(8),
            BorderBrush = new SolidColorBrush(Color.Parse("#2E2E35")),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 12)
        };

        var rootStack = new StackPanel { Spacing = 10 };

        // Row 1: name + host  |  Connect
        var topGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };

        var info = new StackPanel
        {
            Spacing = 3,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        };

        info.Children.Add(new TextBlock
        {
            Text = profile.Name,
            FontSize = 16,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.Parse("#FFFFFF")),
            TextWrapping = TextWrapping.Wrap
        });

        info.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(profile.Username)
                ? $"{profile.Host}:{profile.Port}"
                : $"{profile.Host}:{profile.Port}  •  {profile.Username}",
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.Parse("#9A9AA3")),
            TextWrapping = TextWrapping.Wrap
        });

        var btnConnect = new Button
        {
            Content = "Connect",
            Background = new SolidColorBrush(Color.Parse("#005FB8")),
            Foreground = Brushes.White,
            CornerRadius = new CornerRadius(6),
            MinHeight = 44,
            Padding = new Thickness(18, 8),
            VerticalAlignment = VerticalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            FontWeight = FontWeight.SemiBold,
            FontSize = 14
        };
        btnConnect.Click += async (_, e) =>
        {
            e.Handled = true;
            await StartSessionAsync(profile);
        };

        Grid.SetColumn(info, 0);
        Grid.SetColumn(btnConnect, 1);
        topGrid.Children.Add(info);
        topGrid.Children.Add(btnConnect);
        rootStack.Children.Add(topGrid);

        // Row 2: status chips on their own wrapping row
        // Spacing is applied per-chip via Margin: WrapPanel.ItemSpacing / LineSpacing do not
        // exist in Avalonia 11.1, which is the version this project pins.
        var chips = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, -6, -6) };

        bool isInherit = string.IsNullOrWhiteSpace(profile.ResolutionPreset) || profile.ResolutionPreset.Equals("InheritGlobal", StringComparison.OrdinalIgnoreCase);
        string resText = isInherit
            ? (_payload?.Settings?.DefaultResolution ?? "1920x1080")
            : profile.ResolutionPreset;
        if (resText.Equals("Custom", StringComparison.OrdinalIgnoreCase)) resText = $"{profile.Width}x{profile.Height}";
        if (resText.Equals("Device", StringComparison.OrdinalIgnoreCase)) resText = "Phone screen";

        bool preserveNative = profile.SmartSizingOverride switch
        {
            TriStateOverride.Enabled => false,
            TriStateOverride.Disabled => true,
            _ => !(_payload?.Settings?.DefaultSmartSizing ?? false)
        };
        chips.Children.Add(MakeChip(
            preserveNative ? $"🖥 {resText} · scroll" : $"🖥 {resText} · fit to phone",
            "#C7D6EA", "#16202C", "#2A3B4F"));

        if (profile.EnableWol)
        {
            chips.Children.Add(MakeChip("⚡ Wake-on-LAN", "#FFE0A3", "#2A1F0A", "#5A431A"));
        }

        if (profile.EnableIcmpKnock)
        {
            chips.Children.Add(MakeChip("🚪 Port Knock", "#C7EAE5", "#162C2A", "#2A4F4A"));
        }

        if (!string.IsNullOrWhiteSpace(profile.GatewayHost))
        {
            chips.Children.Add(MakeChip("🌐 RD Gateway", "#D6C7EA", "#221A2C", "#3D2F4F"));
        }

        chips.Children.Add(profile.HasPassword
            ? MakeChip("🔑 Password saved", "#A9E5C0", "#13251A", "#27452F")
            : MakeChip("🔑 No password saved", "#B9B9C2", "#1C1C21", "#2E2E35"));

        rootStack.Children.Add(chips);

        // Row 3: actions. No password view / copy control exists anywhere on this card,
        // by design - a stored password is readable only inside the profile editor.
        var actionsRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

        var btnEdit = new Button
        {
            Content = "✏ Edit",
            Background = new SolidColorBrush(Color.Parse("#1C1C21")),
            Foreground = new SolidColorBrush(Color.Parse("#EDEDED")),
            BorderBrush = new SolidColorBrush(Color.Parse("#2E2E35")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            MinHeight = 40,
            Padding = new Thickness(14, 6),
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            FontSize = 13
        };
        btnEdit.Click += (_, e) =>
        {
            e.Handled = true;
            ShowProfileEditor(profile);
        };
        actionsRow.Children.Add(btnEdit);

        var btnDelete = new Button
        {
            Content = "🗑 Delete",
            Background = new SolidColorBrush(Color.Parse("#1C1C21")),
            Foreground = new SolidColorBrush(Color.Parse("#E85050")),
            BorderBrush = new SolidColorBrush(Color.Parse("#3A2020")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            MinHeight = 40,
            Padding = new Thickness(12, 6),
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            FontSize = 13
        };
        btnDelete.Click += (_, e) =>
        {
            e.Handled = true;
            _editingProfile = profile;
            TxtConfirmDeleteMessage.Text = $"Are you sure you want to delete '{profile.Name}' ({profile.Host})? This cannot be undone.";
            OverlayConfirmDelete.IsVisible = true;
        };
        actionsRow.Children.Add(btnDelete);

        rootStack.Children.Add(actionsRow);
        border.Child = rootStack;
        return border;
    }

    private void CaptureProfileEditorInitialState()
    {
        _editorInitialState = new ProfileEditorState
        {
            Name = TxtProfileName.Text ?? "",
            Host = TxtProfileHost.Text ?? "",
            Port = TxtProfilePort.Text ?? "3389",
            User = TxtProfileUser.Text ?? "",
            Pass = TxtProfilePass.Text ?? "",
            Gateway = TxtProfileGateway.Text ?? "",
            ResIndex = CmbProfileResolution.SelectedIndex,
            CustomWidth = TxtProfileCustomWidth.Text ?? "1920",
            CustomHeight = TxtProfileCustomHeight.Text ?? "1080",
            MultiMonIndex = CmbProfileMultiMon.SelectedIndex,
            PreserveNative = ChkProfilePreserveNative.IsChecked == true,
            EnableWol = ChkProfileEnableWol.IsChecked == true,
            WolMac = TxtProfileWolMac.Text ?? "",
            WolPort = TxtProfileWolPort.Text ?? "9",
            WolWait = TxtProfileWolWait.Text ?? "25",
            EnableKnock = ChkProfileEnableKnock.IsChecked == true,
            KnockSignature = TxtProfileKnockSignature.Text ?? "",
            SuppressCert = ChkProfileSuppressCert.IsChecked == true,
            Notes = TxtProfileNotes.Text ?? ""
        };
    }

    private bool HasUnsavedProfileEdits()
    {
        if (!PanelProfileEditor.IsVisible) return false;
        return (TxtProfileName.Text ?? "") != _editorInitialState.Name
            || (TxtProfileHost.Text ?? "") != _editorInitialState.Host
            || (TxtProfilePort.Text ?? "") != _editorInitialState.Port
            || (TxtProfileUser.Text ?? "") != _editorInitialState.User
            || (TxtProfilePass.Text ?? "") != _editorInitialState.Pass
            || (TxtProfileGateway.Text ?? "") != _editorInitialState.Gateway
            || CmbProfileResolution.SelectedIndex != _editorInitialState.ResIndex
            || (TxtProfileCustomWidth.Text ?? "") != _editorInitialState.CustomWidth
            || (TxtProfileCustomHeight.Text ?? "") != _editorInitialState.CustomHeight
            || CmbProfileMultiMon.SelectedIndex != _editorInitialState.MultiMonIndex
            || (ChkProfilePreserveNative.IsChecked == true) != _editorInitialState.PreserveNative
            || (ChkProfileEnableWol.IsChecked == true) != _editorInitialState.EnableWol
            || (TxtProfileWolMac.Text ?? "") != _editorInitialState.WolMac
            || (TxtProfileWolPort.Text ?? "") != _editorInitialState.WolPort
            || (TxtProfileWolWait.Text ?? "") != _editorInitialState.WolWait
            || (ChkProfileEnableKnock.IsChecked == true) != _editorInitialState.EnableKnock
            || (TxtProfileKnockSignature.Text ?? "") != _editorInitialState.KnockSignature
            || (ChkProfileSuppressCert.IsChecked == true) != _editorInitialState.SuppressCert
            || (TxtProfileNotes.Text ?? "") != _editorInitialState.Notes;
    }

    private void OnCancelProfileEditor()
    {
        if (HasUnsavedProfileEdits())
        {
            OverlayConfirmDiscardProfile.IsVisible = true;
        }
        else
        {
            PanelProfileEditor.IsVisible = false;
            PanelUnlocked.IsVisible = true;
            RefreshBackupReminder();
        }
    }

    private void ShowProfileEditor(RdpProfile? profile)
    {
        _editingProfile = profile;
        TxtProfileError.IsVisible = false;
        TxtProfilePass.PasswordChar = '●';
        BtnToggleProfilePass.Content = "👁";

        if (profile == null)
        {
            TxtEditorTitle.Text = "New Connection";
            TxtProfileName.Text = "";
            TxtProfileHost.Text = "";
            TxtProfilePort.Text = "3389";
            TxtProfileUser.Text = "";
            TxtProfilePass.Text = "";
            TxtProfileGateway.Text = "";

            CmbProfileResolution.SelectedIndex = 0;
            PnlProfileCustomRes.IsVisible = false;
            TxtProfileCustomWidth.Text = "1920";
            TxtProfileCustomHeight.Text = "1080";
            CmbProfileMultiMon.SelectedIndex = 0;
            ChkProfilePreserveNative.IsChecked = true;

            ChkProfileEnableWol.IsChecked = false;
            PnlWolDetails.IsVisible = false;
            TxtProfileWolMac.Text = "";
            TxtProfileWolPort.Text = "9";
            TxtProfileWolWait.Text = "25";
            ChkProfileEnableKnock.IsChecked = false;
            PnlKnockDetails.IsVisible = false;
            TxtProfileKnockSignature.Text = "";
            TxtProfileNotes.Text = "";
            ChkProfileSuppressCert.IsChecked = _payload?.Settings?.SuppressCertWarnings ?? true;
            BtnDeleteProfile.IsVisible = false;
            SetAdvancedVisible(false);
        }
        else
        {
            TxtEditorTitle.Text = "Edit Connection";
            TxtProfileName.Text = profile.Name;
            TxtProfileHost.Text = profile.Host;
            TxtProfilePort.Text = profile.Port.ToString();
            TxtProfileUser.Text = profile.Username;
            TxtProfilePass.Text = profile.Password;
            TxtProfileGateway.Text = profile.GatewayHost;

            string preset = profile.ResolutionPreset ?? "InheritGlobal";
            CmbProfileResolution.SelectedIndex = preset.ToLowerInvariant() switch
            {
                "1920x1080" => 1,
                "1280x720" => 2,
                "1600x900" => 3,
                "1366x768" => 4,
                "2560x1440" => 5,
                "3840x2160" => 6,
                "device" => 7,
                "custom" => 8,
                _ => 0
            };
            PnlProfileCustomRes.IsVisible = CmbProfileResolution.SelectedIndex == 8;
            TxtProfileCustomWidth.Text = (profile.Width > 0 ? profile.Width : 1920).ToString();
            TxtProfileCustomHeight.Text = (profile.Height > 0 ? profile.Height : 1080).ToString();

            CmbProfileMultiMon.SelectedIndex = profile.MultiMonOverride switch
            {
                TriStateOverride.Disabled => 1,
                TriStateOverride.Enabled => 2,
                _ => 0
            };

            ChkProfilePreserveNative.IsChecked = profile.SmartSizingOverride switch
            {
                TriStateOverride.Enabled => false,
                _ => true
            };

            ChkProfileEnableWol.IsChecked = profile.EnableWol;
            PnlWolDetails.IsVisible = profile.EnableWol;
            TxtProfileWolMac.Text = profile.WolMacAddress;
            TxtProfileWolPort.Text = profile.WolPort.ToString();
            TxtProfileWolWait.Text = profile.WolWaitSeconds.ToString();
            ChkProfileEnableKnock.IsChecked = profile.EnableIcmpKnock;
            PnlKnockDetails.IsVisible = profile.EnableIcmpKnock;
            TxtProfileKnockSignature.Text = profile.IcmpKnockSignature;
            TxtProfileNotes.Text = profile.Notes;
            ChkProfileSuppressCert.IsChecked = profile.SuppressCertWarningsOverride switch
            {
                TriStateOverride.Enabled => true,
                TriStateOverride.Disabled => false,
                _ => _payload?.Settings?.SuppressCertWarnings ?? true
            };
            BtnDeleteProfile.IsVisible = true;

            // Open "Advanced" automatically if this profile actually uses any of it, so a
            // configured gateway, knock or WOL setup is never hidden behind a collapsed section.
            bool usesAdvanced = profile.EnableWol
                || profile.EnableIcmpKnock
                || !string.IsNullOrWhiteSpace(profile.GatewayHost)
                || !string.IsNullOrWhiteSpace(profile.Notes)
                || CmbProfileResolution.SelectedIndex != 0
                || CmbProfileMultiMon.SelectedIndex != 0;
            SetAdvancedVisible(usesAdvanced);
        }

        UpdateProfilePreserveNativeHint();
        CaptureProfileEditorInitialState();
        ScrollProfileEditor.Offset = new Vector(0, 0);
        ShowPanel(PanelProfileEditor);
    }

    private void SaveProfile()
    {
        string name = TxtProfileName.Text?.Trim() ?? "";
        string host = TxtProfileHost.Text?.Trim() ?? "";

        if (string.IsNullOrWhiteSpace(name))
        {
            ShowProfileError("Connection name is required.", false);
            return;
        }

        if (string.IsNullOrWhiteSpace(host))
        {
            ShowProfileError("Host IP or hostname is required.", false);
            return;
        }

        if (!int.TryParse(TxtProfilePort.Text, out int port) || port < 1 || port > 65535)
        {
            ShowProfileError("Port must be a number between 1 and 65535.", false);
            return;
        }

        if (!ConnectionEndpoint.TryParse(host, out var parsedEp, out string epErr, port))
        {
            ShowProfileError(epErr, false);
            return;
        }
        host = parsedEp.Host;
        port = parsedEp.Port;

        bool enableKnock = ChkProfileEnableKnock.IsChecked == true;
        string knockSig = (TxtProfileKnockSignature.Text ?? "").Trim();
        if (enableKnock && !string.IsNullOrEmpty(knockSig))
        {
            try
            {
                IcmpKnock.ParseSignature(knockSig);
            }
            catch (Exception ex)
            {
                ShowProfileError(ex.Message, true);
                return;
            }
        }

        int wolPort = 9;
        int wolWait = 25;
        string wolMac = (TxtProfileWolMac.Text ?? "").Trim();
        if (ChkProfileEnableWol.IsChecked == true)
        {
            if (!MacAddressHelper.TryNormalizeMac(wolMac, out string formattedMac, out string macError))
            {
                ShowProfileError(macError, true);
                return;
            }
            wolMac = formattedMac;
            TxtProfileWolMac.Text = formattedMac;

            if (!int.TryParse(TxtProfileWolPort.Text, out wolPort) || wolPort < 1 || wolPort > 65535)
            {
                ShowProfileError("Wake-on-LAN port must be between 1 and 65535 (standard is 9).", true);
                return;
            }

            if (!int.TryParse(TxtProfileWolWait.Text, out wolWait) || wolWait < 0 || wolWait > 300)
            {
                ShowProfileError("Wake-on-LAN wait must be between 0 and 300 seconds.", true);
                return;
            }
        }

        string resPreset = CmbProfileResolution.SelectedIndex switch
        {
            1 => "1920x1080",
            2 => "1280x720",
            3 => "1600x900",
            4 => "1366x768",
            5 => "2560x1440",
            6 => "3840x2160",
            7 => "Device",
            8 => "Custom",
            _ => "InheritGlobal"
        };

        int.TryParse(TxtProfileCustomWidth.Text, out int customWidth);
        if (customWidth <= 0) customWidth = 1920;

        int.TryParse(TxtProfileCustomHeight.Text, out int customHeight);
        if (customHeight <= 0) customHeight = 1080;

        TriStateOverride multiMonOverride = CmbProfileMultiMon.SelectedIndex switch
        {
            1 => TriStateOverride.Disabled,
            2 => TriStateOverride.Enabled,
            _ => TriStateOverride.InheritGlobal
        };

        TriStateOverride smartSizingOverride = ChkProfilePreserveNative.IsChecked == true
            ? TriStateOverride.Disabled
            : TriStateOverride.Enabled;

        if (_payload == null || _vaultFile == null || _masterKey == null) return;

        string gateway = TxtProfileGateway.Text?.Trim() ?? "";

        if (_editingProfile == null)
        {
            var p = new RdpProfile
            {
                Name = name,
                Host = host,
                Port = port,
                Username = TxtProfileUser.Text?.Trim() ?? "",
                Password = TxtProfilePass.Text ?? "",
                GatewayHost = gateway,
                ResolutionPreset = resPreset,
                Width = customWidth,
                Height = customHeight,
                MultiMonOverride = multiMonOverride,
                SmartSizingOverride = smartSizingOverride,
                EnableWol = ChkProfileEnableWol.IsChecked == true,
                WolMacAddress = wolMac,
                WolPort = wolPort > 0 ? wolPort : 9,
                WolWaitSeconds = wolWait >= 0 ? wolWait : 25,
                EnableIcmpKnock = enableKnock,
                IcmpKnockSignature = knockSig,
                Notes = TxtProfileNotes.Text?.Trim() ?? "",
                SuppressCertWarningsOverride = ChkProfileSuppressCert.IsChecked == true ? TriStateOverride.Enabled : TriStateOverride.Disabled
            };
            _payload.Profiles.Add(p);
        }
        else
        {
            _editingProfile.Name = name;
            _editingProfile.Host = host;
            _editingProfile.Port = port;
            _editingProfile.Username = TxtProfileUser.Text?.Trim() ?? "";
            _editingProfile.Password = TxtProfilePass.Text ?? "";
            _editingProfile.GatewayHost = gateway;
            _editingProfile.ResolutionPreset = resPreset;
            _editingProfile.Width = customWidth;
            _editingProfile.Height = customHeight;
            _editingProfile.MultiMonOverride = multiMonOverride;
            _editingProfile.SmartSizingOverride = smartSizingOverride;
            _editingProfile.EnableWol = ChkProfileEnableWol.IsChecked == true;
            _editingProfile.WolMacAddress = wolMac;
            _editingProfile.WolPort = wolPort > 0 ? wolPort : 9;
            _editingProfile.WolWaitSeconds = wolWait >= 0 ? wolWait : 25;
            _editingProfile.EnableIcmpKnock = enableKnock;
            _editingProfile.IcmpKnockSignature = knockSig;
            _editingProfile.Notes = TxtProfileNotes.Text?.Trim() ?? "";
            _editingProfile.SuppressCertWarningsOverride = ChkProfileSuppressCert.IsChecked == true ? TriStateOverride.Enabled : TriStateOverride.Disabled;
        }

        SaveVault();

        PanelProfileEditor.IsVisible = false;
        PanelUnlocked.IsVisible = true;
        RefreshProfilesList();
        RefreshBackupReminder();
    }

    /// <summary>
    /// Shows a validation error and, when the offending field lives under "Advanced",
    /// opens that section so the user is not told off about a field they cannot see.
    /// </summary>
    private void ShowProfileError(string message, bool inAdvancedSection)
    {
        if (inAdvancedSection) SetAdvancedVisible(true);
        TxtProfileError.Text = message;
        TxtProfileError.IsVisible = true;
        Dispatcher.UIThread.Post(() =>
        {
            try { TxtProfileError.BringIntoView(); } catch { }
        }, DispatcherPriority.Background);
    }

    private void ConfirmDeleteProfile()
    {
        if (_editingProfile != null && _payload != null && _vaultFile != null && _masterKey != null)
        {
            _payload.Profiles.Remove(_editingProfile);
            SaveVault();
        }

        _editingProfile = null;
        PanelProfileEditor.IsVisible = false;
        PanelUnlocked.IsVisible = true;
        RefreshProfilesList();
        RefreshBackupReminder();
    }

    // ==================================================================
    //  SETTINGS
    // ==================================================================

    private void ShowSettings()
    {
        ShowPanel(PanelSettings);

        // Audit item 6: populate with every write-back disabled, otherwise assigning
        // SelectedIndex fires SelectionChanged and persists half-initialised state.
        _suppressSettingsSave = true;
        try
        {
            TxtSettingsCurrentPass.Text = "";
            TxtSettingsCurrentPass.PasswordChar = '●';
            BtnToggleSettingsCurrentPass.Content = "👁";
            TxtSettingsNewPass.Text = "";
            TxtSettingsNewPass.PasswordChar = '●';
            BtnToggleSettingsNewPass.Content = "👁";
            TxtSettingsConfirmPass.Text = "";
            TxtSettingsConfirmPass.PasswordChar = '●';
            BtnToggleSettingsConfirmPass.Content = "👁";
            TxtChangePasswordError.IsVisible = false;
            PnlChangePassProgress.IsVisible = false;
            TxtVaultBackupStatus.IsVisible = false;

            ChkSettingsSuppressCert.IsChecked = _payload?.Settings?.SuppressCertWarnings ?? true;
            ChkSettingsAllowScreenshots.IsChecked = AppPrefs.GetBool(AppPrefs.KeyAllowScreenshots, false);
            ChkSettingsPreflight.IsChecked = AppPrefs.GetBool(AppPrefs.KeyPreflightEnabled, true);
            ChkSettingsLockOnBackground.IsChecked = _payload?.Settings?.LockImmediatelyOnBackground ?? true;

            int lockMinutes = _payload?.Settings?.LockMinutes ?? 1;
            CmbSettingsAutoLock.SelectedIndex = lockMinutes switch
            {
                1 => 0,
                5 => 1,
                15 => 2,
                30 => 3,
                60 => 4,
                <= 0 => 5,
                _ => 1
            };

            string defRes = _payload?.Settings?.DefaultResolution ?? "1920x1080";
            CmbSettingsResolution.SelectedIndex = defRes.ToLowerInvariant() switch
            {
                "1280x720" => 1,
                "1600x900" => 2,
                "1366x768" => 3,
                "2560x1440" => 4,
                "3840x2160" => 5,
                "device" => 6,
                _ => 0
            };

            CmbSettingsMultiMon.SelectedIndex = (_payload?.Settings?.DefaultUseMultiMon == true) ? 1 : 0;
            ChkSettingsPreserveNative.IsChecked = !(_payload?.Settings?.DefaultSmartSizing ?? false);
            UpdateSettingsPreserveNativeHint();

            bool enrolled = FindDeviceSeal() != null;
            string? bioUnavailable = AndroidHardwareKeyStore.DescribeBiometricUnavailability(MainActivity.Instance);

            if (enrolled)
            {
                TxtBiometricStatus.Text = "Fingerprint / Face Unlock: active on this device";
                BtnToggleBiometrics.Content = "Remove Biometric Seal";
                BtnToggleBiometrics.IsEnabled = true;
            }
            else if (bioUnavailable != null)
            {
                TxtBiometricStatus.Text = "Fingerprint / Face Unlock: unavailable. " + bioUnavailable;
                BtnToggleBiometrics.Content = "Enroll Biometrics";
                BtnToggleBiometrics.IsEnabled = false;
            }
            else
            {
                TxtBiometricStatus.Text = "Fingerprint / Face Unlock: not set up on this device";
                BtnToggleBiometrics.Content = "Enroll Biometrics";
                BtnToggleBiometrics.IsEnabled = true;
            }

            bool autoTypeActive = RdpAutoTypeService.IsServiceEnabled(AndroidContext);
            bool promptSuppressed = AppPrefs.GetBool(AppPrefs.KeyAutoTypePromptSuppressed, false);

            TxtAccessibilityStatus.Text = autoTypeActive
                ? "On: RDP Vault types your password into Remote Desktop for you."
                : "Off: you will need to type the remote password yourself.";
            TxtAccessibilityStatus.Foreground = new SolidColorBrush(Color.Parse(autoTypeActive ? "#2FBF71" : "#E5A93C"));
            CardAccessibilityGuide.IsVisible = !autoTypeActive;
            BtnResetAutoTypePrompt.IsVisible = promptSuppressed && !autoTypeActive;

            TxtSettingsVersion.Text = "RDP Vault for Android " + (AppVersionName() ?? "");
        }
        finally
        {
            _suppressSettingsSave = false;
        }
    }

    private static string? AppVersionName()
    {
        try
        {
            var ctx = AndroidContext;
            var info = ctx.PackageManager?.GetPackageInfo(ctx.PackageName ?? "", (global::Android.Content.PM.PackageInfoFlags)0);
            return info?.VersionName;
        }
        catch
        {
            return null;
        }
    }

    private void SaveSettingsDefaults()
    {
        if (_suppressSettingsSave) return;
        if (_payload?.Settings == null || _vaultFile == null || _masterKey == null) return;

        int lockMins = CmbSettingsAutoLock.SelectedIndex switch
        {
            0 => 1,
            1 => 5,
            2 => 15,
            3 => 30,
            4 => 60,
            5 => 0,
            _ => 5
        };
        _payload.Settings.LockMinutes = lockMins;
        _payload.Settings.LockImmediatelyOnBackground = ChkSettingsLockOnBackground.IsChecked == true;
        ApplyLockSettingsToActivity();
        NotifyUserActivity();

        _payload.Settings.DefaultResolution = CmbSettingsResolution.SelectedIndex switch
        {
            1 => "1280x720",
            2 => "1600x900",
            3 => "1366x768",
            4 => "2560x1440",
            5 => "3840x2160",
            6 => "Device",
            _ => "1920x1080"
        };

        var (w, h) = _payload.Settings.DefaultResolution switch
        {
            "1280x720" => (1280, 720),
            "1600x900" => (1600, 900),
            "1366x768" => (1366, 768),
            "2560x1440" => (2560, 1440),
            "3840x2160" => (3840, 2160),
            _ => (1920, 1080)
        };
        _payload.Settings.DefaultWidth = w;
        _payload.Settings.DefaultHeight = h;

        _payload.Settings.DefaultUseMultiMon = CmbSettingsMultiMon.SelectedIndex == 1;
        _payload.Settings.DefaultSmartSizing = ChkSettingsPreserveNative.IsChecked != true;
        _payload.Settings.SuppressCertWarnings = ChkSettingsSuppressCert.IsChecked == true;

        VaultCrypto.Save(_vaultFile, _masterKey, _payload, VaultPath);
        AppPrefs.MarkVaultChanged();
    }

    private async Task ToggleBiometricsAsync()
    {
        if (MainActivity.Instance == null || _vaultFile == null || _masterKey == null || _payload == null) return;

        string machineId = VaultCrypto.CurrentMachineId();
        var existing = _vaultFile.Seals?.FirstOrDefault(s => s.MachineId == machineId);

        if (existing != null)
        {
            if (!string.IsNullOrEmpty(existing.KeyId)) AndroidHardwareKeyStore.DeleteHardwareKey(existing.KeyId);
            var updated = (_vaultFile.Seals ?? new List<SealEntry>()).Where(s => s.MachineId != machineId).ToList();
            VaultCrypto.Save(_vaultFile, _masterKey, _payload, VaultPath, newSeals: updated);
            _vaultFile.Seals = updated;

            TxtBiometricStatus.Text = "Fingerprint / Face Unlock: not set up on this device";
            BtnToggleBiometrics.Content = "Enroll Biometrics";
            TxtUnlockedStatus.Text = "Vault Unlocked (Password Mode)";
            return;
        }

        var enroll = await AndroidHardwareKeyStore.EnrollAndSealAsync(
            MainActivity.Instance, _masterKey,
            "Set up fingerprint unlock",
            "Confirm your fingerprint or face to seal the master key in this phone's security chip",
            "Cancel");

        if (enroll.Success && enroll.Seal != null && enroll.KeyId != null)
        {
            var seals = (_vaultFile.Seals ?? new List<SealEntry>()).Where(s => s.MachineId != machineId).ToList();
            seals.Add(new SealEntry { MachineId = machineId, KeyId = enroll.KeyId, TpmBlob = enroll.Seal });
            VaultCrypto.Save(_vaultFile, _masterKey, _payload, VaultPath, newSeals: seals);
            _vaultFile.Seals = seals;

            TxtBiometricStatus.Text = "Fingerprint / Face Unlock: active on this device";
            BtnToggleBiometrics.Content = "Remove Biometric Seal";
            TxtUnlockedStatus.Text = "Vault Unlocked (Fingerprint / Face Protected)";
        }
        else if (!enroll.Cancelled && !string.IsNullOrEmpty(enroll.Error))
        {
            TxtBiometricStatus.Text = "Could not set up fingerprint unlock: " + enroll.Error;
        }
    }

    private async Task SubmitRegenerateRecoveryCodeAsync()
    {
        string pass = TxtVerifyPassForRecovery.Text ?? "";
        if (string.IsNullOrEmpty(pass))
        {
            TxtVerifyPassForRecoveryError.Text = "Please enter your master password.";
            TxtVerifyPassForRecoveryError.IsVisible = true;
            return;
        }

        try
        {
            if (_vaultFile == null || _masterKey == null || _payload == null) return;

            await Task.Run(() => VaultCrypto.Open(_vaultFile, pass));

            string freshCode = "";
            VaultCrypto.Save(_vaultFile, _masterKey, _payload, VaultPath, regenerateRecovery: true, recoveryCodeOut: c => freshCode = c);

            TxtVerifyPassForRecovery.Text = "";
            OverlayPromptPasswordForRecovery.IsVisible = false;
            AppPrefs.MarkVaultChanged();
            ShowRecoveryDisplay(freshCode);
        }
        catch
        {
            TxtVerifyPassForRecoveryError.Text = "Incorrect master password. Please try again.";
            TxtVerifyPassForRecoveryError.IsVisible = true;
        }
    }

    private async Task ChangeMasterPasswordAsync()
    {
        string current = TxtSettingsCurrentPass.Text ?? "";
        string newPass = TxtSettingsNewPass.Text ?? "";
        string confirm = TxtSettingsConfirmPass.Text ?? "";

        if (string.IsNullOrEmpty(current))
        {
            TxtChangePasswordError.Text = "Current password is required.";
            TxtChangePasswordError.IsVisible = true;
            return;
        }

        if (newPass.Length < 8)
        {
            TxtChangePasswordError.Text = "New password must be at least 8 characters.";
            TxtChangePasswordError.IsVisible = true;
            return;
        }

        if (newPass != confirm)
        {
            TxtChangePasswordError.Text = "New passwords do not match.";
            TxtChangePasswordError.IsVisible = true;
            return;
        }

        PnlChangePassProgress.IsVisible = true;
        BtnChangeMasterPassword.IsEnabled = false;
        TxtChangePasswordError.IsVisible = false;

        try
        {
            if (_vaultFile == null || _masterKey == null || _payload == null) return;

            await Task.Run(() => VaultCrypto.Open(_vaultFile, current));

            if (_vaultFile.Seals != null)
            {
                foreach (var s in _vaultFile.Seals)
                {
                    if (!string.IsNullOrEmpty(s.KeyId))
                    {
                        AndroidHardwareKeyStore.DeleteHardwareKey(s.KeyId);
                    }
                }
                _vaultFile.Seals = new List<SealEntry>();
            }

            string freshCode = "";
            VaultCrypto.Save(_vaultFile, _masterKey, _payload, VaultPath,
                newPassword: newPass, newSeals: new List<SealEntry>(), regenerateRecovery: true,
                recoveryCodeOut: c => freshCode = c, retainRecoveryUntilAcknowledged: true);

            TxtSettingsCurrentPass.Text = "";
            TxtSettingsNewPass.Text = "";
            TxtSettingsConfirmPass.Text = "";

            AppPrefs.MarkVaultChanged();
            ShowRecoveryDisplay(freshCode);
        }
        catch (Exception ex)
        {
            TxtChangePasswordError.Text = "Password update failed: " + (ex is InvalidDataException ? "Current password incorrect." : ex.Message);
            TxtChangePasswordError.IsVisible = true;
        }
        finally
        {
            PnlChangePassProgress.IsVisible = false;
            BtnChangeMasterPassword.IsEnabled = true;
        }
    }

    // ==================================================================
    //  CLIPBOARD (recovery code only - remote passwords NEVER go here)
    // ==================================================================

    private void CopySensitiveTextToClipboard(string text, string label)
    {
        if (string.IsNullOrEmpty(text)) return;
        _lastSensitiveCopyUtc = DateTime.UtcNow;
        _lastSensitiveCopiedText = text;

        try
        {
            var context = AndroidContext;
            var clipboardManager = (global::Android.Content.ClipboardManager?)context.GetSystemService(global::Android.Content.Context.ClipboardService);
            if (clipboardManager != null)
            {
                var clip = global::Android.Content.ClipData.NewPlainText(label, text);
                if (clip != null && OperatingSystem.IsAndroidVersionAtLeast(33) && clip.Description != null)
                {
                    try
                    {
                        var extras = new global::Android.OS.PersistableBundle();
                        extras.PutBoolean("android.content.extra.IS_SENSITIVE", true);
                        clip.Description.Extras = extras;
                    }
                    catch { }
                }
                clipboardManager.PrimaryClip = clip;
            }
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("RDPVault", "Sensitive clip error: " + ex.Message);
        }

        string textToWipe = text;
        _ = Task.Run(async () =>
        {
            await Task.Delay(60000);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_lastSensitiveCopiedText == textToWipe)
                {
                    ClearSensitiveClipboard();
                }
            });
        });
    }

    public void CheckAndWipeExpiredClipboard()
    {
        if (_lastSensitiveCopiedText == null) return;
        if ((DateTime.UtcNow - _lastSensitiveCopyUtc).TotalSeconds >= 60)
        {
            ClearSensitiveClipboard();
        }
    }

    private void ClearSensitiveClipboard()
    {
        try
        {
            var context = AndroidContext;
            var clipboardManager = (global::Android.Content.ClipboardManager?)context.GetSystemService(global::Android.Content.Context.ClipboardService);
            clipboardManager?.ClearPrimaryClip();
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("RDPVault", "Clear clipboard error: " + ex.Message);
        }

        try
        {
            var top = TopLevel.GetTopLevel(this);
            top?.Clipboard?.ClearAsync();
        }
        catch { }

        _lastSensitiveCopiedText = null;
    }

    // ==================================================================
    //  RDP SESSION LAUNCH
    // ==================================================================

    private void CancelLaunch()
    {
        _unreachableChoiceTcs?.TrySetResult(UnreachableChoice.Cancel);
        _connectCts?.Cancel();
        OverlayLaunch.IsVisible = false;
        RdpAutoTypeService.Disarm();
    }

    private async Task StartSessionAsync(RdpProfile profile)
    {
        NotifyUserActivity();
        _activeLaunchProfile = profile;

        bool promptSuppressed = AppPrefs.GetBool(AppPrefs.KeyAutoTypePromptSuppressed, false);

        // Suggestion 3: ask once, remember the answer. The old build showed this wall of
        // text on EVERY connect, forever, with no way to opt out.
        if (profile.HasPassword && !promptSuppressed && !RdpAutoTypeService.IsServiceEnabled(AndroidContext))
        {
            ChkDontAskAccessibilityAgain.IsChecked = false;
            OverlayEnableAccessibility.IsVisible = true;
            return;
        }

        await ProceedLaunchAsync(profile);
    }

    private async Task ProceedLaunchAsync(RdpProfile profile)
    {
        if (_isLaunching) return;
        _isLaunching = true;

        _connectCts?.Dispose();
        _connectCts = new CancellationTokenSource();
        var ct = _connectCts.Token;
        _skipWolWait = false;

        var ep = ConnectionEndpoint.FromProfile(profile);
        string host = ep.Host;
        int port = ep.Port;

        TxtLaunchTitle.Text = profile.EnableWol ? "WAKING COMPUTER & CONNECTING" : "CONNECTING TO REMOTE COMPUTER";
        TxtLaunchTarget.Text = $"{profile.Name}  •  {ep.Address}";
        ProgLaunch.IsIndeterminate = true;
        ProgLaunch.Value = 0;
        TxtLaunchCountdown.IsVisible = false;
        BtnSkipWolWait.IsVisible = false;
        CardLaunchUnreachable.IsVisible = false;

        var (w, h, isDevice) = profile.ResolveResolution(_payload?.Settings);
        bool preserveNative = profile.SmartSizingOverride != TriStateOverride.Enabled;
        CardLaunchPanTip.IsVisible = preserveNative && !isDevice;
        TxtLaunchPanTip.Text = $"Your PC stays at full {w}x{h}. In Remote Desktop, tap the top bar and turn on the 🔍 pan/zoom control to scroll around the screen without squashing it.";

        CardLaunchPasswordTip.IsVisible = profile.HasPassword;
        if (profile.HasPassword)
        {
            bool autoTypeReady = RdpAutoTypeService.IsServiceEnabled(AndroidContext);
            if (autoTypeReady)
            {
                TxtLaunchPasswordTipTitle.Text = "AUTO-TYPE IS ON";
                TxtLaunchPasswordTipBody.Text = "Your password will be typed straight into Remote Desktop. It never touches the clipboard.";
                TxtLaunchPasswordTipBody.Foreground = new SolidColorBrush(Color.Parse("#2FBF71"));
            }
            else
            {
                TxtLaunchPasswordTipTitle.Text = "YOU WILL TYPE THE PASSWORD";
                TxtLaunchPasswordTipBody.Text = "Auto-Type is off, so type the password into Remote Desktop yourself. You can read it any time under Edit on this connection.";
                TxtLaunchPasswordTipBody.Foreground = new SolidColorBrush(Color.Parse("#E5A93C"));
            }
        }

        TxtLaunchSubStatus.Text = "";
        TxtLaunchStep.Text = "Preparing connection...";
        BtnCancelLaunch.IsEnabled = true;
        OverlayLaunch.IsVisible = true;

        try
        {
            await Task.Delay(150, ct);

            // 1. Wake-on-LAN
            if (profile.EnableWol && !string.IsNullOrWhiteSpace(profile.WolMacAddress))
            {
                await RunWolSequenceAsync(profile, host, ct);
            }

            if (ct.IsCancellationRequested) return;

            // 2. Port Knocking (Issue 31)
            if (profile.EnableIcmpKnock)
            {
                TxtLaunchSubStatus.Text = "Port Knocking";
                TxtLaunchStep.Text = $"Sending ICMP knock to {host}...";
                try
                {
                    await IcmpKnock.SendBeforeConnectAsync(host, profile.IcmpKnockSignature, ct);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    TxtLaunchSubStatus.Text = "Knock warning";
                    TxtLaunchStep.Text = $"Knock packet warning: {ex.Message}";
                    await Task.Delay(1000, ct);
                }
            }

            if (ct.IsCancellationRequested) return;

            // 3. Reachability pre-flight (suggestion 10)
            if (AppPrefs.GetBool(AppPrefs.KeyPreflightEnabled, true))
            {
                bool proceed = await RunPreflightAsync(profile, host, port, ct);
                if (!proceed) return;
            }

            if (ct.IsCancellationRequested) return;

            // 4. Hand off
            ProgLaunch.IsIndeterminate = true;
            TxtLaunchCountdown.IsVisible = false;
            CardLaunchUnreachable.IsVisible = false;
            TxtLaunchSubStatus.Text = "Opening Remote Desktop";
            TxtLaunchStep.Text = $"Opening Remote Desktop for {ep.Address}...";

            var result = RdpLauncher.LaunchRdp(AndroidContext, profile, _payload?.Settings, out string message);

            ProgLaunch.IsIndeterminate = false;
            ProgLaunch.Value = 100;
            TxtLaunchSubStatus.Text = result == RdpLaunchStatus.Failed ? "Hand-off failed" : "Handed off";
            TxtLaunchStep.Text = message;

            if (result == RdpLaunchStatus.Success)
            {
                MainActivity.Instance?.StartForegroundSession(profile);
                UpdateSessionBanner(profile.Name, host, port, unreachable: false);
                OverlayLaunch.IsVisible = false;
            }
            else if (result == RdpLaunchStatus.RedirectedToStore)
            {
                RdpAutoTypeService.Disarm();
                await Task.Delay(2500, ct);
            }
            else
            {
                RdpAutoTypeService.Disarm();
                await Task.Delay(3500, ct);
            }
        }
        catch (OperationCanceledException)
        {
            RdpAutoTypeService.Disarm();
        }
        catch (Exception ex)
        {
            RdpAutoTypeService.Disarm();
            TxtLaunchSubStatus.Text = "Error";
            TxtLaunchStep.Text = $"Connection failed: {ex.Message}";
            try { await Task.Delay(3000); } catch { }
        }
        finally
        {
            _isLaunching = false;
            OverlayLaunch.IsVisible = false;
            BtnSkipWolWait.IsVisible = false;
            CardLaunchPasswordTip.IsVisible = false;
            CardLaunchUnreachable.IsVisible = false;
            _unreachableChoiceTcs = null;
            _connectCts?.Dispose();
            _connectCts = null;
        }
    }

    private static string ExtractHost(RdpProfile profile)
    {
        string host = profile.Host.Trim();
        if (host.Contains(':') && !host.Contains('['))
        {
            var parts = host.Split(':');
            if (parts.Length == 2) host = parts[0];
        }
        return host;
    }

    private async Task RunWolSequenceAsync(RdpProfile profile, string host, CancellationToken ct)
    {
        TxtLaunchSubStatus.Text = "Sending wake signal";

        if (!HostProbe.IsWolCapableNetwork(AndroidContext))
        {
            // Finding 6: WOL is link-local. Silently "sending" it over mobile data and then
            // making the user sit out a 25 second countdown is a lie with a wait attached.
            TxtLaunchStep.Text = "This phone is not on Wi-Fi, so the wake-up signal cannot reach your PC. Skipping the wake step.";
            try { await Task.Delay(2200, ct); } catch (OperationCanceledException) { throw; }
            return;
        }

        TxtLaunchStep.Text = $"Sending wake-up signal to {profile.WolMacAddress}...";
        await DispatchWolAsync(profile, host);

        if (profile.WolWaitSeconds <= 0) return;

        BtnSkipWolWait.IsVisible = true;
        int total = profile.WolWaitSeconds;
        for (int s = total; s > 0; s--)
        {
            if (ct.IsCancellationRequested || _skipWolWait) break;

            ProgLaunch.IsIndeterminate = false;
            ProgLaunch.Value = 100.0 * (total - s) / total;
            TxtLaunchCountdown.Text = $"{s}s remaining";
            TxtLaunchCountdown.IsVisible = true;
            TxtLaunchSubStatus.Text = "Waking computer";
            TxtLaunchStep.Text = $"Wake-up signal sent. Waiting for the computer to start ({s}s remaining)...";

            await Task.Delay(1000, ct);
        }
        BtnSkipWolWait.IsVisible = false;
        TxtLaunchCountdown.IsVisible = false;
    }

    /// <summary>
    /// One-second TCP check before handing off, so an asleep or offline PC is reported by
    /// RDP Vault in plain words instead of by Remote Desktop's opaque 0x204 half a minute
    /// later (suggestion 10). Returns false when the user cancels.
    /// </summary>
    private async Task<bool> RunPreflightAsync(RdpProfile profile, string host, int port, CancellationToken ct)
    {
        while (true)
        {
            if (ct.IsCancellationRequested) return false;

            ProgLaunch.IsIndeterminate = true;
            CardLaunchUnreachable.IsVisible = false;
            TxtLaunchSubStatus.Text = "Checking the computer";

            if (!string.IsNullOrWhiteSpace(profile.GatewayHost))
            {
                string gwHost = profile.GatewayHost.Trim();
                int gwPort = 443;
                if (gwHost.Contains(':'))
                {
                    var parts = gwHost.Split(':');
                    gwHost = parts[0];
                    if (parts.Length > 1 && int.TryParse(parts[1], out int p)) gwPort = p;
                }

                TxtLaunchStep.Text = $"Checking RD Gateway ({gwHost}:{gwPort})...";
                bool gwReachable = await HostProbe.IsReachableAsync(gwHost, gwPort, 1500, ct);
                if (gwReachable)
                {
                    TxtLaunchStep.Text = "RD Gateway answered. Connecting...";
                    return true;
                }

                if (ct.IsCancellationRequested) return false;

                TxtLaunchUnreachableTitle.Text = "COULD NOT VERIFY GATEWAY";
                TxtLaunchUnreachableBody.Text = $"Could not verify connection to RD Gateway '{profile.GatewayHost}'. The remote computer '{host}:{port}' is routed through this gateway and cannot be probed directly. You can continue connecting anyway.";
                BtnLaunchSendWol.IsVisible = false;
                CardLaunchUnreachable.IsVisible = true;
                ProgLaunch.IsIndeterminate = false;
                ProgLaunch.Value = 0;
                TxtLaunchSubStatus.Text = "";
                TxtLaunchStep.Text = "Waiting for you to choose what to do.";
            }
            else
            {
                TxtLaunchStep.Text = $"Checking that {host}:{port} is awake...";

                bool reachable = await HostProbe.IsReachableAsync(host, port, 1500, ct);
                if (reachable)
                {
                    TxtLaunchStep.Text = $"{host}:{port} answered. Opening Remote Desktop...";
                    return true;
                }

                if (ct.IsCancellationRequested) return false;

                var kind = HostProbe.GetActiveNetworkKind(AndroidContext);
                string reason = kind switch
                {
                    NetworkKind.None => "This phone has no network connection right now.",
                    NetworkKind.Cellular => "You are on mobile data. If this is a home or office PC, it is probably only reachable from its own Wi-Fi network or through a VPN.",
                    _ => "The computer did not answer. It is most likely asleep, switched off, or not on this network."
                };

                bool canWol = !string.IsNullOrWhiteSpace(profile.WolMacAddress) && HostProbe.IsWolCapableNetwork(AndroidContext);

                TxtLaunchUnreachableTitle.Text = $"{host} DID NOT ANSWER";
                TxtLaunchUnreachableBody.Text = reason + " Opening Remote Desktop now would just spin for about 30 seconds and then show error 0x204.";
                BtnLaunchSendWol.IsVisible = canWol;
                CardLaunchUnreachable.IsVisible = true;
                ProgLaunch.IsIndeterminate = false;
                ProgLaunch.Value = 0;
                TxtLaunchSubStatus.Text = "";
                TxtLaunchStep.Text = "Waiting for you to choose what to do.";
            }

            _unreachableChoiceTcs = new TaskCompletionSource<UnreachableChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
            UnreachableChoice choice;
            try
            {
                choice = await _unreachableChoiceTcs.Task.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            finally
            {
                _unreachableChoiceTcs = null;
            }

            switch (choice)
            {
                case UnreachableChoice.ConnectAnyway:
                    CardLaunchUnreachable.IsVisible = false;
                    return true;

                case UnreachableChoice.SendWol:
                    CardLaunchUnreachable.IsVisible = false;
                    ProgLaunch.IsIndeterminate = true;
                    TxtLaunchSubStatus.Text = "Sending wake signal";
                    TxtLaunchStep.Text = $"Sending wake-up signal to {profile.WolMacAddress}...";
                    await DispatchWolAsync(profile, host);
                    // Give the machine a moment to come up before re-probing.
                    for (int s = 15; s > 0; s--)
                    {
                        if (ct.IsCancellationRequested) return false;
                        TxtLaunchStep.Text = $"Wake-up signal sent. Waiting for the computer to start ({s}s)...";
                        await Task.Delay(1000, ct);
                    }
                    continue;

                case UnreachableChoice.Retry:
                    continue;

                default:
                    CancelLaunch();
                    return false;
            }
        }
    }

    /// <summary>
    /// Wake-on-LAN dispatch.
    ///
    /// Finding 6 fixes: the magic packet now goes to the SUBNET-DIRECTED broadcast address
    /// (many Android builds drop 255.255.255.255), and it is no longer also fired at the
    /// host's RDP port, which was meaningless traffic aimed at a machine that is by
    /// definition switched off.
    /// </summary>
    private static async Task DispatchWolAsync(RdpProfile profile, string host)
    {
        try
        {
            string macClean = Regex.Replace(profile.WolMacAddress ?? "", "[^0-9A-Fa-f]", "");
            if (macClean.Length != 12) return;
            byte[] macBytes = Convert.FromHexString(macClean);

            byte[] packet = new byte[102];
            for (int i = 0; i < 6; i++) packet[i] = 0xFF;
            for (int i = 1; i <= 16; i++) Buffer.BlockCopy(macBytes, 0, packet, i * 6, 6);

            using var client = new UdpClient { EnableBroadcast = true };
            int wolPort = profile.WolPort > 0 ? profile.WolPort : 9;

            var targets = new List<IPAddress> { IPAddress.Broadcast };
            targets.AddRange(HostProbe.GetDirectedBroadcastAddresses());

            // Unicast to the host's last known address as well: it works on switches that
            // still hold the ARP entry, and costs one datagram.
            if (IPAddress.TryParse(host, out var hostIp))
            {
                targets.Add(hostIp);
            }
            else if (!string.IsNullOrWhiteSpace(host))
            {
                try
                {
                    var addresses = await Dns.GetHostAddressesAsync(host);
                    targets.AddRange(addresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork));
                }
                catch { }
            }

            foreach (var ip in targets.Distinct())
            {
                try
                {
                    await client.SendAsync(packet, packet.Length, new IPEndPoint(ip, wolPort));
                }
                catch { }
            }
        }
        catch
        {
            // WOL is best-effort by nature; the pre-flight check reports the real outcome.
        }
    }

    private void SkipWolWait()
    {
        _skipWolWait = true;
        BtnSkipWolWait.IsVisible = false;
        TxtLaunchStep.Text = "Skipping the countdown, connecting now...";
    }

    // ==================================================================
    //  SESSION BANNER
    // ==================================================================

    private void UpdateSessionBanner(string name, string host, int port, bool unreachable)
    {
        BannerActiveSession.IsVisible = true;
        TxtActiveSessionDetails.Text = $"{name}  •  {host}:{port}";

        if (unreachable)
        {
            BannerActiveSession.Background = new SolidColorBrush(Color.Parse("#2A1212"));
            BannerActiveSession.BorderBrush = new SolidColorBrush(Color.Parse("#7A2A2A"));
            TxtActiveSessionDot.Foreground = new SolidColorBrush(Color.Parse("#FF8A8A"));
            TxtActiveSessionHeading.Foreground = new SolidColorBrush(Color.Parse("#FF8A8A"));
            TxtActiveSessionHeading.Text = "REMOTE PC NOT RESPONDING";
            TxtActiveSessionSub.Text = "The computer stopped answering. It may have gone to sleep, or dropped off the network.";
            TxtActiveSessionSub.Foreground = new SolidColorBrush(Color.Parse("#F0C9C9"));
        }
        else
        {
            BannerActiveSession.Background = new SolidColorBrush(Color.Parse("#162719"));
            BannerActiveSession.BorderBrush = new SolidColorBrush(Color.Parse("#2FBF71"));
            TxtActiveSessionDot.Foreground = new SolidColorBrush(Color.Parse("#2FBF71"));
            TxtActiveSessionHeading.Foreground = new SolidColorBrush(Color.Parse("#2FBF71"));
            TxtActiveSessionHeading.Text = "HANDED OFF TO REMOTE DESKTOP";
            TxtActiveSessionSub.Text = "Your session is running inside Microsoft Remote Desktop.";
            TxtActiveSessionSub.Foreground = new SolidColorBrush(Color.Parse("#9FC7A6"));
        }
    }

    private void EndActiveSession()
    {
        MainActivity.Instance?.StopForegroundSession();
        BannerActiveSession.IsVisible = false;
        TxtActiveSessionDetails.Text = "";
    }

    public void OnSessionEnded()
    {
        BannerActiveSession.IsVisible = false;
        TxtActiveSessionDetails.Text = "";
    }

    public void OnAppResumed()
    {
        if (OverlayLaunch.IsVisible && _connectCts == null)
        {
            OverlayLaunch.IsVisible = false;
        }

        var activity = MainActivity.Instance;
        if (activity != null && activity.IsSessionActive)
        {
            var p = activity.ActiveSessionProfile;
            string name = p?.Name ?? "Remote PC";
            string host = p?.Host ?? "";
            int port = p?.Port ?? 3389;
            UpdateSessionBanner(name, host, port, activity.ActiveSessionHostUnreachable);
        }
        else
        {
            BannerActiveSession.IsVisible = false;
        }

        RefreshBackupReminder();
    }

    // ==================================================================
    //  RECOVERY CODE INPUT (caret-preserving - suggestion 1)
    // ==================================================================

    /// <summary>
    /// Mirrors exactly what RecoveryCode.Normalize accepts, so the caret arithmetic below
    /// cannot drift out of sync with the normaliser.
    /// </summary>
    private static bool IsSignificantRecoveryChar(char raw)
    {
        char c = char.ToUpperInvariant(raw);
        c = c switch
        {
            'I' or 'L' => '1',
            'O' => '0',
            'U' => 'V',
            _ => c
        };
        return "0123456789ABCDEFGHJKMNPQRSTVWXYZ".IndexOf(c) >= 0;
    }

    private void OnRecoveryInputChanged()
    {
        if (_isFormattingRecovery) return;

        var tb = TxtRecoveryInput;
        string raw = tb.Text ?? "";
        int caret = Math.Clamp(tb.CaretIndex, 0, raw.Length);

        // Count how many REAL code characters sit to the left of the caret. Hyphens are
        // decoration, so they must not affect the caret's logical position.
        int significantBefore = 0;
        for (int i = 0; i < caret; i++)
        {
            if (IsSignificantRecoveryChar(raw[i])) significantBefore++;
        }

        string clean = RecoveryCode.Normalize(raw);
        if (clean.Length > 52) clean = clean[..52];
        if (significantBefore > clean.Length) significantBefore = clean.Length;

        TxtRecoveryCharCount.Text = $"{clean.Length} / 52 characters";
        TxtRecoveryCharCount.Foreground = new SolidColorBrush(Color.Parse(clean.Length == 52 ? "#2FBF71" : "#9A9AA3"));

        string formatted = RecoveryCode.Format(clean);
        if (formatted == raw) return;

        // Translate the logical position back into an index in the hyphenated string.
        int newCaret = 0, seen = 0;
        while (newCaret < formatted.Length && seen < significantBefore)
        {
            if (formatted[newCaret] != '-') seen++;
            newCaret++;
        }
        // Sitting immediately before a separator reads as "after the group" to the user, so
        // step over it - otherwise the next keystroke appears on the wrong side of the dash.
        if (newCaret < formatted.Length && formatted[newCaret] == '-' && significantBefore > 0)
        {
            newCaret++;
        }

        _isFormattingRecovery = true;
        try
        {
            tb.Text = formatted;
            tb.CaretIndex = Math.Clamp(newCaret, 0, formatted.Length);
        }
        finally
        {
            _isFormattingRecovery = false;
        }
    }

    private async Task PasteRecoveryCodeAsync()
    {
        try
        {
            var top = TopLevel.GetTopLevel(this);
            if (top?.Clipboard != null)
            {
                string? text = await top.Clipboard.GetTextAsync();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    TxtRecoveryInput.Text = text.Trim();
                    TxtRecoveryInput.CaretIndex = TxtRecoveryInput.Text?.Length ?? 0;
                }
            }
        }
        catch { }
    }

    // ==================================================================
    //  VAULT FILE IMPORT / EXPORT / SHARE
    // ==================================================================

    /// <summary>
    /// Validates that the bytes really are an RDP Vault file before they are allowed to
    /// overwrite a live vault (Finding 8). The old check was "longer than 16 bytes", which
    /// happily accepted a photo.
    /// </summary>
    private static bool TryValidateVaultBytes(byte[] data, out string error, out int profileCountHint)
    {
        error = "";
        profileCountHint = -1;
        try
        {
            VaultValidation.Read(data);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private async Task ImportVaultFileAsync()
    {
        try
        {
            var top = TopLevel.GetTopLevel(this);
            if (top == null) return;

            MainActivity.Instance?.BeginExternalActivity();
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select your vault file (.rdpv)",
                AllowMultiple = false
            });

            if (files.Count == 0) return;

            await using var stream = await files[0].OpenReadAsync();
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            byte[] importedData = ms.ToArray();

            if (!TryValidateVaultBytes(importedData, out string error, out _))
            {
                TxtFirstRunError.Text = error;
                TxtFirstRunError.IsVisible = true;
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(VaultPath)!);
            await File.WriteAllBytesAsync(VaultPath, importedData);
            AppPrefs.MarkVaultExported();   // it came from a file, so a copy already exists

            ShowLockScreen();
            TxtLockNotice.Text = "Vault imported. Enter its master password to unlock.";
            TxtLockNotice.IsVisible = true;
            TxtLockError.IsVisible = false;
        }
        catch (Exception ex)
        {
            TxtFirstRunError.Text = $"Import failed: {ex.Message}";
            TxtFirstRunError.IsVisible = true;
        }
    }

    private void ShowBackupStatus(string message, bool good)
    {
        TxtVaultBackupStatus.Text = message;
        TxtVaultBackupStatus.Foreground = new SolidColorBrush(Color.Parse(good ? "#2FBF71" : "#FF6B6B"));
        TxtVaultBackupStatus.IsVisible = true;
    }

    private async Task ExportVaultFileAsync()
    {
        try
        {
            if (!File.Exists(VaultPath))
            {
                ShowBackupStatus("There is no vault file to export yet.", false);
                return;
            }

            var top = TopLevel.GetTopLevel(this);
            if (top == null) return;

            MainActivity.Instance?.BeginExternalActivity();
            var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save encrypted vault backup",
                DefaultExtension = "rdpv",
                SuggestedFileName = $"rdp_vault_backup_{DateTime.Now:yyyyMMdd_HHmm}.rdpv"
            });

            if (file == null) return;

            byte[] vaultBytes = await File.ReadAllBytesAsync(VaultPath);
            await using var stream = await file.OpenWriteAsync();
            await stream.WriteAsync(vaultBytes);

            AppPrefs.MarkVaultExported();
            RefreshBackupReminder();
            ShowBackupStatus($"Saved ({vaultBytes.Length:N0} bytes). It is still encrypted - your master password is required to open it.", true);
        }
        catch (Exception ex)
        {
            ShowBackupStatus($"Export failed: {ex.Message}", false);
        }
    }

    /// <summary>
    /// Suggestion 9: hands the encrypted vault straight to the Android share sheet, so it
    /// can go to Quick Share, Drive or email in one tap instead of a trip through a file
    /// manager. The staged copy lives in the app's own cache folder and is exposed through
    /// a scoped FileProvider, never through world-readable storage.
    /// </summary>
    private async Task ShareVaultBackupAsync()
    {
        try
        {
            if (!File.Exists(VaultPath))
            {
                ShowBackupStatus("There is no vault file to share yet.", false);
                return;
            }

            var ctx = AndroidContext;
            string cacheRoot = ctx.CacheDir?.AbsolutePath ?? Path.GetTempPath();
            string shareDir = Path.Combine(cacheRoot, "share");
            Directory.CreateDirectory(shareDir);

            // Clear previous staged copies so old vault snapshots do not accumulate.
            foreach (var stale in Directory.GetFiles(shareDir, "*.rdpv"))
            {
                try { File.Delete(stale); } catch { }
            }

            string shareName = $"rdp_vault_backup_{DateTime.Now:yyyyMMdd_HHmm}.rdpv";
            string sharePath = Path.Combine(shareDir, shareName);
            File.Copy(VaultPath, sharePath, overwrite: true);

            var javaFile = new Java.IO.File(sharePath);
            var uri = AndroidX.Core.Content.FileProvider.GetUriForFile(ctx, "com.rdpvault.app.fileprovider", javaFile);

            var intent = new global::Android.Content.Intent(global::Android.Content.Intent.ActionSend);
            intent.SetType("application/octet-stream");
            intent.PutExtra(global::Android.Content.Intent.ExtraSubject, "RDP Vault encrypted backup");
            intent.PutExtra(global::Android.Content.Intent.ExtraStream, uri);
            intent.AddFlags(global::Android.Content.ActivityFlags.GrantReadUriPermission);

            var chooser = global::Android.Content.Intent.CreateChooser(intent, "Send encrypted vault backup to...");
            chooser?.AddFlags(global::Android.Content.ActivityFlags.NewTask | global::Android.Content.ActivityFlags.GrantReadUriPermission);

            MainActivity.Instance?.BeginExternalActivity();
            ctx.StartActivity(chooser);

            ShowBackupStatus("Sent to share sheet. Complete sending or saving to finish your backup. (Use 'Save to File' to mark backed up).", true);

            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            ShowBackupStatus($"Could not open the share sheet: {ex.Message}", false);
        }
    }

    private async Task RestoreVaultFromFileAsync()
    {
        try
        {
            var top = TopLevel.GetTopLevel(this);
            if (top == null) return;

            MainActivity.Instance?.BeginExternalActivity();
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select the vault backup to restore (.rdpv)",
                AllowMultiple = false
            });

            if (files.Count == 0) return;

            await using var stream = await files[0].OpenReadAsync();
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            byte[] importedData = ms.ToArray();

            VaultFile staged;
            try
            {
                staged = VaultValidation.Read(importedData);
            }
            catch (Exception ex)
            {
                ShowRestoreError(ex.Message);
                return;
            }

            _stagedRestoreBytes = importedData;
            _stagedRestoreVaultFile = staged;

            TxtVerifyBackupPass.Text = "";
            TxtVerifyBackupPassError.IsVisible = false;
            OverlayVerifyBackupPassword.IsVisible = true;
        }
        catch (Exception ex)
        {
            ShowRestoreError($"Restore failed: {ex.Message}");
        }
    }

    private void ShowRestoreError(string message)
    {
        if (PanelLocked.IsVisible)
        {
            TxtLockError.Text = message;
            TxtLockError.IsVisible = true;
        }
        else
        {
            ShowBackupStatus(message, false);
        }
    }

    private async Task SubmitVerifyBackupPasswordAsync()
    {
        string pass = TxtVerifyBackupPass.Text ?? "";
        if (string.IsNullOrEmpty(pass))
        {
            TxtVerifyBackupPassError.Text = "Please enter the backup's master password.";
            TxtVerifyBackupPassError.IsVisible = true;
            return;
        }

        if (_stagedRestoreVaultFile == null || _stagedRestoreBytes == null)
        {
            OverlayVerifyBackupPassword.IsVisible = false;
            return;
        }

        TxtVerifyBackupPassError.IsVisible = false;

        try
        {
            var (testMaster, testPayload) = await Task.Run(() => VaultCrypto.Open(_stagedRestoreVaultFile, pass));
            try
            {
                VaultValidation.ValidatePayload(testPayload);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(testMaster);
            }

            OverlayVerifyBackupPassword.IsVisible = false;
            int count = testPayload.Profiles?.Count ?? 0;
            int current = _payload?.Profiles?.Count ?? 0;
            TxtConfirmRestoreMessage.Text =
                $"Backup verified successfully ({count} connection{(count == 1 ? "" : "s")} found).\n\n" +
                $"Restoring will replace the {current} connection{(current == 1 ? "" : "s")} and all settings currently on this phone with whatever is inside the backup. " +
                "A copy of your current vault is saved alongside it as a .bak first.";
            OverlayConfirmRestoreVault.IsVisible = true;
        }
        catch (Exception ex)
        {
            TxtVerifyBackupPassError.Text = "Cannot open backup: " + (ex is InvalidDataException ? ex.Message : "Incorrect master password.");
            TxtVerifyBackupPassError.IsVisible = true;
        }
    }

    private async Task ExecuteVaultRestoreAsync()
    {
        OverlayConfirmRestoreVault.IsVisible = false;
        if (_stagedRestoreBytes == null) return;

        try
        {
            if (File.Exists(VaultPath))
            {
                File.Copy(VaultPath, VaultPath + AppPaths.BeforeRestoreSuffix, overwrite: true);
                File.Copy(VaultPath, VaultPath + AppPaths.BackupSuffix, overwrite: true);
            }

            await File.WriteAllBytesAsync(VaultPath, _stagedRestoreBytes);
            _stagedRestoreBytes = null;
            _stagedRestoreVaultFile = null;
            AppPrefs.MarkVaultExported();

            LockVault();
            TxtLockNotice.Text = "Vault restored from backup. Enter that vault's master password to unlock.";
            TxtLockNotice.IsVisible = true;
            TxtLockError.IsVisible = false;
        }
        catch (Exception ex)
        {
            ShowRestoreError($"Restore failed: {ex.Message}");
        }
    }
}
