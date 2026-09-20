using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
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
using RDPVault.Android.Rdp;
using RDPVault.Android.Security;

namespace RDPVault.Android.Views;

public partial class MainView : UserControl
{
    private VaultFile? _vaultFile;
    private VaultPayload? _payload;
    private byte[]? _masterKey;
    private RdpProfile? _editingProfile;
    private string _activeRecoveryCode = "";
    private System.Threading.CancellationTokenSource? _connectCts;
    private System.Threading.CancellationTokenSource? _searchCts;
    private bool _biometricPromptSuppressed;
    private volatile bool _skipWolWait;
    private bool _isFormattingRecovery;
    private byte[]? _stagedRestoreBytes;
    private DateTime _lastSensitiveCopyUtc = DateTime.MinValue;
    private string? _lastSensitiveCopiedText;

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
        public bool SmartSizing;
        public bool EnableWol;
        public string WolMac;
        public string WolPort;
        public string WolWait;
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

        // Legacy migration check: if an earlier version wrote to Documents subfolder, migrate it forward
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

    public MainView()
    {
        InitializeComponent();
        WireEvents();
        Loaded += (_, _) => CheckInitialVaultState();
    }

    private void WireEvents()
    {
        // 1. Password Visibility Eye Toggles
        SetupPasswordToggle(TxtFirstRunPass, BtnToggleFirstRunPass);
        SetupPasswordToggle(TxtFirstRunConfirm, BtnToggleFirstRunConfirm);
        SetupPasswordToggle(TxtPassword, BtnToggleUnlockPass);
        SetupPasswordToggle(TxtRecoveryNewPass, BtnToggleRecoveryNewPass);
        SetupPasswordToggle(TxtRecoveryConfirmPass, BtnToggleRecoveryConfirmPass);
        SetupPasswordToggle(TxtProfilePass, BtnToggleProfilePass);
        SetupPasswordToggle(TxtSettingsCurrentPass, BtnToggleSettingsCurrentPass);
        SetupPasswordToggle(TxtSettingsNewPass, BtnToggleSettingsNewPass);
        SetupPasswordToggle(TxtSettingsConfirmPass, BtnToggleSettingsConfirmPass);
        SetupPasswordToggle(TxtVerifyPassForRecovery, BtnToggleVerifyRecoveryPass);

        // 2. Numeric input filtering
        RestrictToDigits(TxtProfilePort);
        RestrictToDigits(TxtProfileWolPort);
        RestrictToDigits(TxtProfileWolWait);
        RestrictToDigits(TxtProfileCustomWidth);
        RestrictToDigits(TxtProfileCustomHeight);

        // 3. First-Time Setup Wizard
        BtnFirstRunCreate.Click += async (_, _) => await CreateVaultFirstTimeAsync();
        BtnFirstRunImportExisting.Click += async (_, _) => await ImportVaultFileAsync();

        // 4. Recovery Code Display
        BtnCopyRecoveryCode.Click += (_, _) => CopyRecoveryCodeToClipboardAsync();
        ChkConfirmRecoverySaved.IsCheckedChanged += (_, _) =>
        {
            BtnFinishSetupAndEnter.IsEnabled = ChkConfirmRecoverySaved.IsChecked == true;
        };
        BtnFinishSetupAndEnter.Click += (_, _) =>
        {
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

        // 6. Recovery Unlock
        BtnPasteRecoveryCode.Click += async (_, _) => await PasteRecoveryCodeAsync();
        TxtRecoveryInput.TextChanged += (_, _) => OnRecoveryInputChanged();
        BtnSubmitRecoveryUnlock.Click += async (_, _) => await SubmitRecoveryUnlockAsync();
        BtnCancelRecoveryUnlock.Click += (_, _) => ShowLockScreen();

        // 7. Unlocked Screen
        BtnAddProfile.Click += (_, _) => ShowProfileEditor(null);
        BtnSettings.Click += (_, _) => ShowSettings();
        BtnLock.Click += (_, _) => LockVault();

        // Search with 150ms debounce to prevent mobile jank during typing
        TxtSearch.TextChanged += (_, _) =>
        {
            _searchCts?.Cancel();
            var cts = new System.Threading.CancellationTokenSource();
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
            });
        };
        BtnClearSearch.Click += (_, _) =>
        {
            _searchCts?.Cancel();
            TxtSearch.Text = "";
            RefreshProfilesList();
        };
        BtnDismissActiveBanner.Click += (_, _) => BannerActiveSession.IsVisible = false;
        BtnReturnToRdp.Click += (_, _) =>
        {
            var ctx = (global::Android.Content.Context?)MainActivity.Instance ?? global::Android.App.Application.Context;
            RdpLauncher.ResumeRemoteDesktop(ctx);
        };
        BtnEndActiveSession.Click += (_, _) => EndActiveSession();

        // 8. Profile Editor (Sticky Top Header and Bottom Buttons)
        BtnSaveProfile.Click += (_, _) => SaveProfile();
        BtnTopSaveProfile.Click += (_, _) => SaveProfile();
        BtnTopCancelProfile.Click += (_, _) => OnCancelProfileEditor();
        BtnBottomCancelProfile.Click += (_, _) => OnCancelProfileEditor();
        BtnKeepEditingProfile.Click += (_, _) => OverlayConfirmDiscardProfile.IsVisible = false;
        BtnConfirmDiscardProfile.Click += (_, _) =>
        {
            OverlayConfirmDiscardProfile.IsVisible = false;
            PanelProfileEditor.IsVisible = false;
            PanelUnlocked.IsVisible = true;
        };

        // Wake-on-LAN collapsible toggle
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

        // Delete with explicit confirmation
        BtnDeleteProfile.Click += (_, _) =>
        {
            if (_editingProfile != null)
            {
                TxtConfirmDeleteMessage.Text = $"Are you sure you want to delete '{_editingProfile.Name}' ({_editingProfile.Host})? This action cannot be undone.";
                OverlayConfirmDelete.IsVisible = true;
            }
        };
        BtnCancelDelete.Click += (_, _) => OverlayConfirmDelete.IsVisible = false;
        BtnConfirmDelete.Click += (_, _) =>
        {
            OverlayConfirmDelete.IsVisible = false;
            ConfirmDeleteProfile();
        };

        // Custom resolution field toggle in editor
        CmbProfileResolution.SelectionChanged += (_, _) =>
        {
            PnlProfileCustomRes.IsVisible = CmbProfileResolution.SelectedIndex == 8;
        };

        void ScrollToWol()
        {
            Dispatcher.UIThread.Post(() =>
            {
                ScrollProfileEditor.Offset = new Vector(0, 320);
            });
        }
        TxtProfileWolMac.GotFocus += (_, _) => ScrollToWol();
        TxtProfileWolPort.GotFocus += (_, _) => ScrollToWol();
        TxtProfileWolWait.GotFocus += (_, _) => ScrollToWol();
        TxtProfileNotes.GotFocus += (_, _) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                ScrollProfileEditor.Offset = new Vector(0, 520);
            });
        };

        // 9. Settings
        BtnTopCloseSettings.Click += (_, _) =>
        {
            PanelSettings.IsVisible = false;
            PanelUnlocked.IsVisible = true;
        };
        BtnToggleBiometrics.Click += async (_, _) => await ToggleBiometricsAsync();
        BtnExportVault.Click += async (_, _) => await ExportVaultFileAsync();
        BtnImportVault.Click += async (_, _) => await ImportVaultFromSettingsAsync();
        BtnCancelRestoreVault.Click += (_, _) =>
        {
            _stagedRestoreBytes = null;
            OverlayConfirmRestoreVault.IsVisible = false;
        };
        BtnConfirmRestoreVault.Click += async (_, _) => await ExecuteVaultRestoreAsync();
        BtnRegenerateRecoveryCode.Click += (_, _) =>
        {
            TxtVerifyPassForRecovery.Text = "";
            TxtVerifyPassForRecoveryError.IsVisible = false;
            OverlayPromptPasswordForRecovery.IsVisible = true;
        };
        BtnCancelVerifyPassForRecovery.Click += (_, _) =>
        {
            OverlayPromptPasswordForRecovery.IsVisible = false;
        };
        BtnSubmitVerifyPassForRecovery.Click += async (_, _) => await SubmitRegenerateRecoveryCodeAsync();

        BtnChangeMasterPassword.Click += async (_, _) => await ChangeMasterPasswordAsync();

        // Resolution, Auto-Lock & Security Defaults in Settings
        CmbSettingsAutoLock.SelectionChanged += (_, _) => SaveSettingsDefaults();
        CmbSettingsResolution.SelectionChanged += (_, _) => SaveSettingsDefaults();
        CmbSettingsMultiMon.SelectionChanged += (_, _) => SaveSettingsDefaults();
        ChkSettingsSmartSizing.IsCheckedChanged += (_, _) => SaveSettingsDefaults();
        ChkSettingsSuppressCert.IsCheckedChanged += (_, _) => SaveSettingsDefaults();

        BtnCloseSettings.Click += (_, _) =>
        {
            PanelSettings.IsVisible = false;
            PanelUnlocked.IsVisible = true;
        };

        // 10. WOL Skip Wait
        BtnSkipWolWait.Click += (_, _) => SkipWolWait();
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
                btn.Content = "👁‍🗨";
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

    /// <summary>
    /// Handles physical Android back button presses.
    /// Returns true if back press was consumed by closing an overlay, modal, or sub-screen;
    /// returns false if at root level (letting the OS minimize the app).
    /// </summary>
    public bool HandleBackPressed()
    {
        // 1. Password verification for recovery overlay
        if (OverlayPromptPasswordForRecovery.IsVisible)
        {
            OverlayPromptPasswordForRecovery.IsVisible = false;
            return true;
        }

        // 2. Discard profile confirmation modal
        if (OverlayConfirmDiscardProfile.IsVisible)
        {
            OverlayConfirmDiscardProfile.IsVisible = false;
            return true;
        }

        // 3. Restore vault confirmation modal
        if (OverlayConfirmRestoreVault.IsVisible)
        {
            OverlayConfirmRestoreVault.IsVisible = false;
            _stagedRestoreBytes = null;
            return true;
        }

        // 4. Delete confirmation modal
        if (OverlayConfirmDelete.IsVisible)
        {
            OverlayConfirmDelete.IsVisible = false;
            return true;
        }

        // 5. Launch overlay cancel
        if (OverlayLaunch.IsVisible)
        {
            _connectCts?.Cancel();
            OverlayLaunch.IsVisible = false;
            return true;
        }

        // 6. Profile editor -> check unsaved changes before returning to connections list
        if (PanelProfileEditor.IsVisible)
        {
            OnCancelProfileEditor();
            return true;
        }

        // 7. Settings -> return to connections list
        if (PanelSettings.IsVisible)
        {
            PanelSettings.IsVisible = false;
            PanelUnlocked.IsVisible = true;
            return true;
        }

        // 8. Recovery unlock -> return to lock screen
        if (PanelRecoveryUnlock.IsVisible)
        {
            ShowLockScreen();
            return true;
        }

        // 9. Recovery display -> enter vault if already initialized
        if (PanelRecoveryDisplay.IsVisible && _payload != null)
        {
            PanelRecoveryDisplay.IsVisible = false;
            SwitchToUnlocked();
            return true;
        }

        // 10. Active search query -> clear search filter
        if (PanelUnlocked.IsVisible && !string.IsNullOrEmpty(TxtSearch.Text))
        {
            _searchCts?.Cancel();
            TxtSearch.Text = "";
            RefreshProfilesList();
            return true;
        }

        // 11. Root level (Unlocked list, Lock screen, or Setup wizard)
        return false;
    }

    /// <summary>
    /// Auto-locks the vault when returning from background if unlocked.
    /// </summary>
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

    // ==================== STATE MANAGEMENT ====================

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

        bool bioAvail = MainActivity.Instance != null && AndroidHardwareKeyStore.IsBiometricAvailable(MainActivity.Instance);
        ChkFirstRunBiometrics.IsChecked = bioAvail;
        ChkFirstRunBiometrics.IsEnabled = bioAvail;
        if (!bioAvail)
        {
            ChkFirstRunBiometrics.Content = "Fingerprint / Face unavailable (Not enrolled in device settings)";
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
        payload.Settings.DefaultSmartSizing = true;
        payload.Settings.DefaultUseMultiMon = false; // Safe single-monitor default for mobile

        try
        {
            var file = await Task.Run(() => VaultCrypto.CreateVault(pass, payload, VaultPath, out recoveryCode));
            var (masterKey, _) = await Task.Run(() => VaultCrypto.Open(file, pass));

            _vaultFile = file;
            _masterKey = masterKey;
            _payload = payload;

            // Enroll hardware biometrics if requested
            if (ChkFirstRunBiometrics.IsChecked == true && MainActivity.Instance != null)
            {
                try
                {
                    var (success, _) = await AndroidHardwareKeyStore.AuthenticateBiometricAsync(
                        MainActivity.Instance, "Enroll Biometrics", "Confirm fingerprint or face to protect your master key", "Skip");

                    if (success)
                    {
                        string keyId = Guid.NewGuid().ToString("N");
                        AndroidHardwareKeyStore.GenerateHardwareKey(MainActivity.Instance, keyId, requireBiometrics: true);
                        var cipher = AndroidHardwareKeyStore.GetInitializedCipher(keyId, (int)Javax.Crypto.CipherMode.EncryptMode);

                        string tpmBlob = AndroidHardwareKeyStore.SealMasterKey(keyId, masterKey, cipher);
                        var seal = new SealEntry
                        {
                            MachineId = VaultCrypto.CurrentMachineId(),
                            KeyId = keyId,
                            TpmBlob = tpmBlob
                        };
                        var seals = new List<SealEntry> { seal };
                        VaultCrypto.Save(file, masterKey, payload, VaultPath, newSeals: seals);
                        _vaultFile = file;
                    }
                }
                catch
                {
                    // Fall back cleanly to password-only vault if canceled
                }
            }

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
        _activeRecoveryCode = code;
        TxtRecoveryDisplayCode.Text = FormatRecoveryCode(code);
        TxtCopyNotice.IsVisible = false;
        ChkConfirmRecoverySaved.IsChecked = false;
        BtnFinishSetupAndEnter.IsEnabled = false;
        ShowPanel(PanelRecoveryDisplay);
    }

    private static string FormatRecoveryCode(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        string clean = RecoveryCode.Normalize(raw);
        return RecoveryCode.Format(clean);
    }

    private void CopyRecoveryCodeToClipboardAsync()
    {
        if (string.IsNullOrEmpty(_activeRecoveryCode)) return;
        CopySensitiveTextToClipboard(_activeRecoveryCode, "RDP Vault Recovery Code");
        TxtCopyNotice.IsVisible = true;
    }

    // ==================== LOCK SCREEN & UNLOCK ====================

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
                string json = File.ReadAllText(VaultPath);
                _vaultFile = System.Text.Json.JsonSerializer.Deserialize(json, VaultJsonContext.Default.VaultFile);
            }
        }
        catch
        {
            _vaultFile = null;
        }

        string machineId = VaultCrypto.CurrentMachineId();
        bool hasBioSeal = _vaultFile?.Seals?.Any(s => s.MachineId == machineId && !string.IsNullOrEmpty(s.KeyId) && !string.IsNullOrEmpty(s.TpmBlob)) == true;
        PnlBiometricCard.IsVisible = hasBioSeal;

        // Auto-prompt biometrics if enrolled on this device and not previously dismissed
        if (hasBioSeal && !_biometricPromptSuppressed && MainActivity.Instance != null)
        {
            Dispatcher.UIThread.Post(async () =>
            {
                await UnlockWithBiometricsAsync();
            }, DispatcherPriority.Background);
        }
    }

    private async Task UnlockWithBiometricsAsync()
    {
        if (MainActivity.Instance == null || _vaultFile == null) return;
        TxtLockNotice.IsVisible = false;

        string machineId = VaultCrypto.CurrentMachineId();
        var seal = _vaultFile.Seals.FirstOrDefault(s => s.MachineId == machineId && !string.IsNullOrEmpty(s.KeyId) && !string.IsNullOrEmpty(s.TpmBlob));
        if (seal == null)
        {
            TxtLockError.Text = "Biometrics not enrolled for this device. Please unlock with Master Password.";
            TxtLockError.IsVisible = true;
            return;
        }

        try
        {
            var (success, error) = await AndroidHardwareKeyStore.AuthenticateBiometricAsync(
                MainActivity.Instance, "Unlock RDP Vault", "Touch sensor to unlock your connections", "Use Master Password");

            if (!success)
            {
                _biometricPromptSuppressed = true;
                if (!string.IsNullOrEmpty(error) && !error.Contains("cancel", StringComparison.OrdinalIgnoreCase))
                {
                    TxtLockError.Text = "Biometrics: " + error;
                    TxtLockError.IsVisible = true;
                }
                return;
            }

            string[] parts = seal.TpmBlob.Split(':');
            if (parts.Length != 2) throw new FormatException("Invalid seal format.");
            byte[] iv = Convert.FromBase64String(parts[0]);

            var cipher = AndroidHardwareKeyStore.GetInitializedCipher(seal.KeyId, (int)Javax.Crypto.CipherMode.DecryptMode, iv);
            byte[] masterKey = AndroidHardwareKeyStore.UnsealMasterKey(seal.TpmBlob, cipher);
            var payload = VaultCrypto.OpenPayload(_vaultFile, masterKey);

            _masterKey = masterKey;
            _payload = payload;
            _biometricPromptSuppressed = false;

            SwitchToUnlocked();
        }
        catch (Exception)
        {
            _biometricPromptSuppressed = true;
            TxtLockError.Text = "Biometric seal outdated or invalid. Please unlock with Master Password once to automatically repair.";
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

        try
        {
            if (!File.Exists(VaultPath))
            {
                ShowFirstRunWizard();
                return;
            }

            string json = await File.ReadAllTextAsync(VaultPath);
            var file = System.Text.Json.JsonSerializer.Deserialize(json, VaultJsonContext.Default.VaultFile)
                ?? throw new InvalidDataException("Vault file is corrupted.");

            var (master, payload) = await Task.Run(() => VaultCrypto.Open(file, password));
            _vaultFile = file;
            _masterKey = master;
            _payload = payload;

            // Auto-repair biometric seal if previously enrolled on this device
            string machineId = VaultCrypto.CurrentMachineId();
            if (file.Seals?.Any(s => s.MachineId == machineId) == true && MainActivity.Instance != null)
            {
                try
                {
                    string newKeyId = Guid.NewGuid().ToString("N");
                    AndroidHardwareKeyStore.GenerateHardwareKey(MainActivity.Instance, newKeyId);
                    var cipher = AndroidHardwareKeyStore.GetInitializedCipher(newKeyId, (int)Javax.Crypto.CipherMode.EncryptMode);
                    string tpmBlob = AndroidHardwareKeyStore.SealMasterKey(newKeyId, master, cipher);

                    var newSeals = file.Seals.Where(s => s.MachineId != machineId).ToList();
                    newSeals.Add(new SealEntry
                    {
                        MachineId = machineId,
                        KeyId = newKeyId,
                        TpmBlob = tpmBlob
                    });

                    VaultCrypto.Save(file, master, payload, VaultPath, newSeals: newSeals);
                    _vaultFile = file;
                }
                catch { }
            }

            TxtPassword.Text = "";
            _biometricPromptSuppressed = false;
            SwitchToUnlocked();
        }
        catch (Exception ex)
        {
            TxtLockError.Text = "Unlock failed: " + (ex is InvalidDataException ? "Incorrect master password." : ex.Message);
            TxtLockError.IsVisible = true;
        }
        finally
        {
            PnlUnlockProgress.IsVisible = false;
            BtnUnlock.IsEnabled = true;
        }
    }

    private void ShowRecoveryUnlock()
    {
        ShowPanel(PanelRecoveryUnlock);
        TxtRecoveryInput.Text = "";
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
        string rawCode = TxtRecoveryInput.Text ?? "";
        string code = RecoveryCode.Normalize(rawCode);
        string newPass = TxtRecoveryNewPass.Text ?? "";
        string confirmPass = TxtRecoveryConfirmPass.Text ?? "";

        if (code.Length != 52)
        {
            TxtRecoveryUnlockError.Text = $"Emergency recovery code must be exactly 52 characters (entered {code.Length}/52).";
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
                TxtRecoveryUnlockError.Text = "Invalid recovery code. Please check and re-type.";
                TxtRecoveryUnlockError.IsVisible = true;
                return;
            }

            var (masterKey, payload) = result.Value;
            _masterKey = masterKey;
            _payload = payload;

            string freshCode = "";
            VaultCrypto.Save(_vaultFile, masterKey, payload, VaultPath, newPassword: newPass, regenerateRecovery: true, recoveryCodeOut: c => freshCode = c);

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

    private void SwitchToUnlocked()
    {
        ShowPanel(PanelUnlocked);

        if (MainActivity.Instance != null && _payload?.Settings != null)
        {
            MainActivity.Instance.ConfiguredLockMinutes = _payload.Settings.LockMinutes;
        }

        string machineId = VaultCrypto.CurrentMachineId();
        bool hasBio = _vaultFile?.Seals?.Any(s => s.MachineId == machineId && !string.IsNullOrEmpty(s.KeyId) && !string.IsNullOrEmpty(s.TpmBlob)) == true;
        TxtUnlockedStatus.Text = hasBio
            ? "Vault Unlocked (Fingerprint / Face Protected)"
            : "Vault Unlocked (Password Mode)";

        RefreshProfilesList();
    }

    private void LockVault()
    {
        EndActiveSession();
        _payload = null;
        if (_masterKey != null)
        {
            CryptographicOperations.ZeroMemory(_masterKey);
            _masterKey = null;
        }
        _vaultFile = null;
        _biometricPromptSuppressed = false;

        ShowLockScreen();
        TxtLockNotice.Text = "Vault locked";
        TxtLockNotice.IsVisible = true;
    }

    // ==================== PROFILES MANAGEMENT ====================

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

        var rootStack = new StackPanel
        {
            Spacing = 10
        };

        // Row 1: Info (full-width) + Connect Button
        var topGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto")
        };

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
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        string wolBadge = profile.EnableWol ? "  •  WOL" : "";
        string resBadge = string.IsNullOrWhiteSpace(profile.ResolutionPreset) || profile.ResolutionPreset.Equals("InheritGlobal", StringComparison.OrdinalIgnoreCase)
            ? ""
            : $"  •  {profile.ResolutionPreset}";

        info.Children.Add(new TextBlock
        {
            Text = $"{profile.Host}:{profile.Port}  •  {profile.Username}{wolBadge}{resBadge}",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse("#8A8A93")),
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        var btnConnect = new Button
        {
            Content = "Connect",
            Background = new SolidColorBrush(Color.Parse("#005FB8")),
            Foreground = Brushes.White,
            CornerRadius = new CornerRadius(6),
            Height = 40,
            Padding = new Thickness(18, 0),
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

        // Row 2: Actions Bar (Copy Password, Edit, Delete)
        var actionsRow = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 8
        };

        if (profile.HasPassword)
        {
            var btnCopyPass = new Button
            {
                Content = "📋 Copy Password",
                Background = new SolidColorBrush(Color.Parse("#1C1C21")),
                Foreground = new SolidColorBrush(Color.Parse("#EDEDED")),
                BorderBrush = new SolidColorBrush(Color.Parse("#2E2E35")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Height = 34,
                Padding = new Thickness(10, 0),
                VerticalAlignment = VerticalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                FontSize = 12
            };
            btnCopyPass.Click += async (_, e) =>
            {
                e.Handled = true;
                await CopyPasswordToClipboardAsync(profile.Password, profile.Name);
                btnCopyPass.Content = "✓ Password Copied";
                btnCopyPass.Foreground = new SolidColorBrush(Color.Parse("#2FBF71"));
                _ = Task.Delay(2000).ContinueWith(_ =>
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        btnCopyPass.Content = "📋 Copy Password";
                        btnCopyPass.Foreground = new SolidColorBrush(Color.Parse("#EDEDED"));
                    });
                });
            };
            actionsRow.Children.Add(btnCopyPass);
        }

        var btnEdit = new Button
        {
            Content = "✏ Edit",
            Background = new SolidColorBrush(Color.Parse("#1C1C21")),
            Foreground = new SolidColorBrush(Color.Parse("#EDEDED")),
            BorderBrush = new SolidColorBrush(Color.Parse("#2E2E35")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Height = 34,
            Padding = new Thickness(12, 0),
            VerticalAlignment = VerticalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            FontSize = 12
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
            Height = 34,
            Padding = new Thickness(10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            FontSize = 12
        };
        btnDelete.Click += (_, e) =>
        {
            e.Handled = true;
            _editingProfile = profile;
            TxtConfirmDeleteMessage.Text = $"Are you sure you want to delete '{profile.Name}' ({profile.Host})? This action cannot be undone.";
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
            SmartSizing = ChkProfileSmartSizing.IsChecked == true,
            EnableWol = ChkProfileEnableWol.IsChecked == true,
            WolMac = TxtProfileWolMac.Text ?? "",
            WolPort = TxtProfileWolPort.Text ?? "9",
            WolWait = TxtProfileWolWait.Text ?? "25",
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
            || (ChkProfileSmartSizing.IsChecked == true) != _editorInitialState.SmartSizing
            || (ChkProfileEnableWol.IsChecked == true) != _editorInitialState.EnableWol
            || (TxtProfileWolMac.Text ?? "") != _editorInitialState.WolMac
            || (TxtProfileWolPort.Text ?? "") != _editorInitialState.WolPort
            || (TxtProfileWolWait.Text ?? "") != _editorInitialState.WolWait
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
        }
    }

    private void ShowProfileEditor(RdpProfile? profile)
    {
        _editingProfile = profile;
        TxtProfileError.IsVisible = false;

        if (profile == null)
        {
            TxtEditorTitle.Text = "New Connection";
            TxtProfileName.Text = "";
            TxtProfileHost.Text = "";
            TxtProfilePort.Text = "3389";
            TxtProfileUser.Text = "";
            TxtProfilePass.Text = "";
            TxtProfilePass.PasswordChar = '●';
            BtnToggleProfilePass.Content = "👁";
            TxtProfileGateway.Text = "";

            // Resolution defaults
            CmbProfileResolution.SelectedIndex = 0;
            PnlProfileCustomRes.IsVisible = false;
            TxtProfileCustomWidth.Text = "1920";
            TxtProfileCustomHeight.Text = "1080";
            CmbProfileMultiMon.SelectedIndex = 0;
            ChkProfileSmartSizing.IsChecked = true;

            ChkProfileEnableWol.IsChecked = false;
            PnlWolDetails.IsVisible = false;
            TxtProfileWolMac.Text = "";
            TxtProfileWolPort.Text = "9";
            TxtProfileWolWait.Text = "25";
            TxtProfileNotes.Text = "";
            ChkProfileSuppressCert.IsChecked = _payload?.Settings?.SuppressCertWarnings ?? true;
            BtnDeleteProfile.IsVisible = false;
        }
        else
        {
            TxtEditorTitle.Text = "Edit Connection";
            TxtProfileName.Text = profile.Name;
            TxtProfileHost.Text = profile.Host;
            TxtProfilePort.Text = profile.Port.ToString();
            TxtProfileUser.Text = profile.Username;
            TxtProfilePass.Text = profile.Password;
            TxtProfilePass.PasswordChar = '●';
            BtnToggleProfilePass.Content = "👁";
            TxtProfileGateway.Text = profile.GatewayHost;

            // Map resolution preset
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

            // Multi-Mon override
            CmbProfileMultiMon.SelectedIndex = profile.MultiMonOverride switch
            {
                TriStateOverride.Disabled => 1, // Single Monitor Only
                TriStateOverride.Enabled => 2,  // Span All Monitors
                _ => 0                          // Default (Follow Global)
            };

            // Smart Sizing override
            ChkProfileSmartSizing.IsChecked = profile.SmartSizingOverride switch
            {
                TriStateOverride.Disabled => false,
                _ => true
            };

            ChkProfileEnableWol.IsChecked = profile.EnableWol;
            PnlWolDetails.IsVisible = profile.EnableWol;
            TxtProfileWolMac.Text = profile.WolMacAddress;
            TxtProfileWolPort.Text = profile.WolPort.ToString();
            TxtProfileWolWait.Text = profile.WolWaitSeconds.ToString();
            TxtProfileNotes.Text = profile.Notes;
            ChkProfileSuppressCert.IsChecked = profile.SuppressCertWarningsOverride switch
            {
                TriStateOverride.Enabled => true,
                TriStateOverride.Disabled => false,
                _ => _payload?.Settings?.SuppressCertWarnings ?? true
            };
            BtnDeleteProfile.IsVisible = true;
        }

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
            TxtProfileError.Text = "Connection name is required.";
            TxtProfileError.IsVisible = true;
            return;
        }

        if (string.IsNullOrWhiteSpace(host))
        {
            TxtProfileError.Text = "Host IP or hostname is required.";
            TxtProfileError.IsVisible = true;
            return;
        }

        if (!int.TryParse(TxtProfilePort.Text, out int port) || port < 1 || port > 65535)
        {
            TxtProfileError.Text = "Port must be a valid number between 1 and 65535.";
            TxtProfileError.IsVisible = true;
            return;
        }

        int wolPort = 9;
        int wolWait = 25;
        string wolMac = (TxtProfileWolMac.Text ?? "").Trim();
        if (ChkProfileEnableWol.IsChecked == true)
        {
            if (!MacAddressHelper.TryNormalizeMac(wolMac, out string formattedMac, out string macError))
            {
                TxtProfileError.Text = macError;
                TxtProfileError.IsVisible = true;
                return;
            }
            wolMac = formattedMac;
            TxtProfileWolMac.Text = formattedMac;

            if (!int.TryParse(TxtProfileWolPort.Text, out wolPort) || wolPort < 1 || wolPort > 65535)
            {
                TxtProfileError.Text = "WOL Port must be between 1 and 65535 (standard is 9).";
                TxtProfileError.IsVisible = true;
                return;
            }

            if (!int.TryParse(TxtProfileWolWait.Text, out wolWait) || wolWait < 0 || wolWait > 300)
            {
                TxtProfileError.Text = "WOL Wait seconds must be between 0 and 300.";
                TxtProfileError.IsVisible = true;
                return;
            }
        }

        // Resolution preset resolution
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

        int customWidth = 1920;
        int.TryParse(TxtProfileCustomWidth.Text, out customWidth);
        if (customWidth <= 0) customWidth = 1920;

        int customHeight = 1080;
        int.TryParse(TxtProfileCustomHeight.Text, out customHeight);
        if (customHeight <= 0) customHeight = 1080;

        TriStateOverride multiMonOverride = CmbProfileMultiMon.SelectedIndex switch
        {
            1 => TriStateOverride.Disabled, // Single Monitor Only
            2 => TriStateOverride.Enabled,  // Span All Monitors
            _ => TriStateOverride.InheritGlobal
        };

        TriStateOverride smartSizingOverride = ChkProfileSmartSizing.IsChecked == true
            ? TriStateOverride.Enabled
            : TriStateOverride.Disabled;

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
            _editingProfile.Notes = TxtProfileNotes.Text?.Trim() ?? "";
            _editingProfile.SuppressCertWarningsOverride = ChkProfileSuppressCert.IsChecked == true ? TriStateOverride.Enabled : TriStateOverride.Disabled;
        }

        VaultCrypto.Save(_vaultFile, _masterKey, _payload, VaultPath);

        PanelProfileEditor.IsVisible = false;
        PanelUnlocked.IsVisible = true;
        RefreshProfilesList();
    }

    private void ConfirmDeleteProfile()
    {
        if (_editingProfile != null && _payload != null && _vaultFile != null && _masterKey != null)
        {
            _payload.Profiles.Remove(_editingProfile);
            VaultCrypto.Save(_vaultFile, _masterKey, _payload, VaultPath);
        }

        PanelProfileEditor.IsVisible = false;
        PanelUnlocked.IsVisible = true;
        RefreshProfilesList();
    }

    // ==================== SETTINGS ====================

    private void ShowSettings()
    {
        ShowPanel(PanelSettings);
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
        ChkSettingsSuppressCert.IsChecked = _payload?.Settings?.SuppressCertWarnings ?? true;

        // Auto-lock setting
        int lockMinutes = _payload?.Settings?.LockMinutes ?? 60;
        CmbSettingsAutoLock.SelectedIndex = lockMinutes switch
        {
            1 => 0,
            5 => 1,
            15 => 2,
            30 => 3,
            60 => 4,
            <= 0 => 5,
            _ => 4
        };

        // Resolution preset in settings
        string defRes = _payload?.Settings?.DefaultResolution ?? "1920x1080";
        CmbSettingsResolution.SelectedIndex = defRes.ToLowerInvariant() switch
        {
            "1280x720" => 1,
            "1600x900" => 2,
            "1366x768" => 3,
            "2560x1440" => 4,
            "3840x2160" => 5,
            "device" => 6,
            _ => 0 // 1920x1080
        };

        CmbSettingsMultiMon.SelectedIndex = (_payload?.Settings?.DefaultUseMultiMon == true) ? 1 : 0;
        ChkSettingsSmartSizing.IsChecked = _payload?.Settings?.DefaultSmartSizing ?? true;

        string machineId = VaultCrypto.CurrentMachineId();
        bool enrolled = _vaultFile?.Seals?.Any(s => s.MachineId == machineId && !string.IsNullOrEmpty(s.KeyId) && !string.IsNullOrEmpty(s.TpmBlob)) == true;
        TxtBiometricStatus.Text = enrolled
            ? "Fingerprint / Face Unlock: Active on this device"
            : "Fingerprint / Face Unlock: Not enrolled on this device";
        BtnToggleBiometrics.Content = enrolled
            ? "Remove Biometric Seal"
            : "Enroll Biometrics";
    }

    private void SaveSettingsDefaults()
    {
        if (_payload?.Settings != null && _vaultFile != null && _masterKey != null)
        {
            int lockMins = CmbSettingsAutoLock.SelectedIndex switch
            {
                0 => 1,
                1 => 5,
                2 => 15,
                3 => 30,
                4 => 60,
                5 => 0,
                _ => 60
            };
            _payload.Settings.LockMinutes = lockMins;
            if (MainActivity.Instance != null)
            {
                MainActivity.Instance.ConfiguredLockMinutes = lockMins;
            }

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
            _payload.Settings.DefaultSmartSizing = ChkSettingsSmartSizing.IsChecked == true;
            _payload.Settings.SuppressCertWarnings = ChkSettingsSuppressCert.IsChecked == true;

            VaultCrypto.Save(_vaultFile, _masterKey, _payload, VaultPath);
        }
    }

    private async Task ToggleBiometricsAsync()
    {
        if (MainActivity.Instance == null || _vaultFile == null || _masterKey == null || _payload == null) return;

        string machineId = VaultCrypto.CurrentMachineId();
        var existing = _vaultFile.Seals.FirstOrDefault(s => s.MachineId == machineId);

        if (existing != null)
        {
            // Remove seal
            AndroidHardwareKeyStore.DeleteHardwareKey(existing.KeyId);
            var updated = _vaultFile.Seals.Where(s => s.MachineId != machineId).ToList();
            VaultCrypto.Save(_vaultFile, _masterKey, _payload, VaultPath, newSeals: updated);
            _vaultFile.Seals = updated;

            TxtBiometricStatus.Text = "Fingerprint / Face Unlock: Not enrolled on this device";
            BtnToggleBiometrics.Content = "Enroll Biometrics";
        }
        else
        {
            // Enroll seal
            try
            {
                var (success, err) = await AndroidHardwareKeyStore.AuthenticateBiometricAsync(
                    MainActivity.Instance, "Enroll Biometrics", "Confirm fingerprint or face to protect your master key", "Cancel");

                if (success)
                {
                    string keyId = Guid.NewGuid().ToString("N");
                    AndroidHardwareKeyStore.GenerateHardwareKey(MainActivity.Instance, keyId, requireBiometrics: true);
                    var cipher = AndroidHardwareKeyStore.GetInitializedCipher(keyId, (int)Javax.Crypto.CipherMode.EncryptMode);

                    string tpmBlob = AndroidHardwareKeyStore.SealMasterKey(keyId, _masterKey, cipher);
                    var seal = new SealEntry
                    {
                        MachineId = machineId,
                        KeyId = keyId,
                        TpmBlob = tpmBlob
                    };
                    var seals = _vaultFile.Seals.Where(s => s.MachineId != machineId).ToList();
                    seals.Add(seal);
                    VaultCrypto.Save(_vaultFile, _masterKey, _payload, VaultPath, newSeals: seals);
                    _vaultFile.Seals = seals;

                    TxtBiometricStatus.Text = "Fingerprint / Face Unlock: Active on this device";
                    BtnToggleBiometrics.Content = "Remove Biometric Seal";
                }
                else if (!string.IsNullOrEmpty(err) && !err.Contains("cancel", StringComparison.OrdinalIgnoreCase))
                {
                    TxtBiometricStatus.Text = "Enrollment: " + err;
                }
            }
            catch (Exception ex)
            {
                TxtBiometricStatus.Text = "Enrollment error: " + ex.Message;
            }
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

            // Verify master password
            await Task.Run(() => VaultCrypto.Open(_vaultFile, pass));

            string freshCode = "";
            VaultCrypto.Save(_vaultFile, _masterKey, _payload, VaultPath, regenerateRecovery: true, recoveryCodeOut: c => freshCode = c);

            OverlayPromptPasswordForRecovery.IsVisible = false;
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

            // Verify current password
            await Task.Run(() => VaultCrypto.Open(_vaultFile, current));

            string freshCode = "";
            VaultCrypto.Save(_vaultFile, _masterKey, _payload, VaultPath, newPassword: newPass, regenerateRecovery: true, recoveryCodeOut: c => freshCode = c);

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

    // ==================== RDP SESSION & WAKE-ON-LAN ====================

    private void BtnCancelLaunch_Click(object? sender, RoutedEventArgs e)
    {
        _connectCts?.Cancel();
        OverlayLaunch.IsVisible = false;
    }

    private void CopySensitiveTextToClipboard(string text, string label)
    {
        if (string.IsNullOrEmpty(text)) return;
        _lastSensitiveCopyUtc = DateTime.UtcNow;
        _lastSensitiveCopiedText = text;

        try
        {
            var context = (global::Android.Content.Context?)MainActivity.Instance ?? global::Android.App.Application.Context;
            var clipboardManager = (global::Android.Content.ClipboardManager?)context.GetSystemService(global::Android.Content.Context.ClipboardService);
            if (clipboardManager != null)
            {
                var clip = global::Android.Content.ClipData.NewPlainText(label, text);
                if (OperatingSystem.IsAndroidVersionAtLeast(33) && clip.Description != null)
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

        try
        {
            var top = TopLevel.GetTopLevel(this);
            top?.Clipboard?.SetTextAsync(text);
        }
        catch { }

        // Auto-wipe clipboard after 60 seconds
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
            var context = (global::Android.Content.Context?)MainActivity.Instance ?? global::Android.App.Application.Context;
            var clipboardManager = (global::Android.Content.ClipboardManager?)context.GetSystemService(global::Android.Content.Context.ClipboardService);
            if (clipboardManager != null)
            {
                if (OperatingSystem.IsAndroidVersionAtLeast(28))
                {
                    clipboardManager.ClearPrimaryClip();
                }
                else
                {
                    clipboardManager.PrimaryClip = global::Android.Content.ClipData.NewPlainText("", "");
                }
            }
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

    private Task CopyPasswordToClipboardAsync(string password, string profileName)
    {
        if (!string.IsNullOrEmpty(password))
        {
            CopySensitiveTextToClipboard(password, $"Password for {profileName}");
        }
        return Task.CompletedTask;
    }

    private async Task StartSessionAsync(RdpProfile profile)
    {
        _connectCts = new System.Threading.CancellationTokenSource();
        var ct = _connectCts.Token;
        _skipWolWait = false;

        TxtLaunchTitle.Text = profile.EnableWol ? "WAKING COMPUTER & CONNECTING" : "CONNECTING TO REMOTE COMPUTER";
        TxtLaunchTarget.Text = $"{profile.Name}  •  {profile.Host}:{profile.Port}";
        ProgLaunch.IsIndeterminate = true;
        ProgLaunch.Value = 0;
        TxtLaunchCountdown.IsVisible = false;
        BtnSkipWolWait.IsVisible = false;
        TxtLaunchStep.Text = "Preparing connection...";
        TxtLaunchSubStatus.Text = "";
        BtnCancelLaunch.IsEnabled = true;
        OverlayLaunch.IsVisible = true;

        try
        {
            // Auto-copy password to clipboard with 60s auto-wipe if saved
            if (profile.HasPassword)
            {
                await CopyPasswordToClipboardAsync(profile.Password, profile.Name);
                TxtLaunchSubStatus.Text = "Password Ready";
                TxtLaunchStep.Text = "Password copied to clipboard (clears in 60s). Paste when prompted by Remote Desktop!";
                await Task.Delay(800, ct);
            }

            // 1. Wake-on-LAN dispatch if enabled
            if (profile.EnableWol && !string.IsNullOrWhiteSpace(profile.WolMacAddress))
            {
                TxtLaunchSubStatus.Text = "Sending wake signal";
                TxtLaunchStep.Text = $"Transmitting wake signal to {profile.WolMacAddress}...";
                await DispatchWolAsync(profile);

                if (profile.WolWaitSeconds > 0)
                {
                    BtnSkipWolWait.IsVisible = true;
                    int total = profile.WolWaitSeconds;
                    for (int s = total; s > 0; s--)
                    {
                        if (ct.IsCancellationRequested || _skipWolWait) break;

                        double percent = 100.0 * (total - s) / total;
                        ProgLaunch.IsIndeterminate = false;
                        ProgLaunch.Value = percent;
                        TxtLaunchCountdown.Text = $"{s}s remaining";
                        TxtLaunchCountdown.IsVisible = true;
                        TxtLaunchSubStatus.Text = "Waking Computer";
                        TxtLaunchStep.Text = $"Wake signal broadcasted. Waiting for computer to start ({s}s remaining)...";

                        await Task.Delay(1000, ct);
                    }
                    BtnSkipWolWait.IsVisible = false;
                }
            }

            if (ct.IsCancellationRequested) return;

            ProgLaunch.IsIndeterminate = true;
            TxtLaunchCountdown.IsVisible = false;
            TxtLaunchSubStatus.Text = "Opening Remote Desktop";
            TxtLaunchStep.Text = $"Launching Remote Desktop for {profile.Host}:{profile.Port}...";

            // 2. Launch RDP via Mobile Intent handoff with monitor & resolution protection
            var context = (global::Android.Content.Context?)MainActivity.Instance ?? global::Android.App.Application.Context;
            var result = RdpLauncher.LaunchRdp(context, profile, _payload?.Settings, out string message);

            ProgLaunch.IsIndeterminate = false;
            ProgLaunch.Value = 100;
            TxtLaunchSubStatus.Text = result == RdpLaunchStatus.Failed ? "Handoff Failed" : "Connected";
            TxtLaunchStep.Text = message;

            if (result == RdpLaunchStatus.Success)
            {
                MainActivity.Instance?.StartForegroundSession(profile);
                BannerActiveSession.IsVisible = true;
                TxtActiveSessionDetails.Text = $"{profile.Name}  •  {profile.Host}:{profile.Port}";
                // Immediately dismiss launch overlay so return from remote desktop is seamless and never shows a black screen
                OverlayLaunch.IsVisible = false;
            }
            else if (result == RdpLaunchStatus.RedirectedToStore)
            {
                await Task.Delay(2500, ct);
            }
            else
            {
                await Task.Delay(3500, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Canceled by user
        }
        catch (Exception ex)
        {
            TxtLaunchSubStatus.Text = "Error";
            TxtLaunchStep.Text = $"Connection failed: {ex.Message}";
            await Task.Delay(3000);
        }
        finally
        {
            OverlayLaunch.IsVisible = false;
            BtnSkipWolWait.IsVisible = false;
            _connectCts?.Dispose();
            _connectCts = null;
        }
    }

    private static async Task DispatchWolAsync(RdpProfile profile)
    {
        try
        {
            string macClean = Regex.Replace(profile.WolMacAddress, "[: -]", "");
            if (macClean.Length != 12) return;
            byte[] macBytes = Convert.FromHexString(macClean);

            byte[] packet = new byte[102];
            for (int i = 0; i < 6; i++) packet[i] = 0xFF;
            for (int i = 1; i <= 16; i++)
            {
                Buffer.BlockCopy(macBytes, 0, packet, i * 6, 6);
            }

            using var client = new UdpClient();
            client.EnableBroadcast = true;

            int wolPort = profile.WolPort > 0 ? profile.WolPort : 9;
            int rdpPort = profile.Port > 0 ? profile.Port : 3389;

            var endpoints = new List<IPEndPoint>
            {
                new IPEndPoint(IPAddress.Broadcast, wolPort),
                new IPEndPoint(IPAddress.Broadcast, rdpPort)
            };

            string host = profile.Host.Trim();
            if (host.Contains(':') && !host.Contains('['))
            {
                var parts = host.Split(':');
                host = parts[0];
            }

            if (IPAddress.TryParse(host, out var hostIp))
            {
                endpoints.Add(new IPEndPoint(hostIp, wolPort));
                endpoints.Add(new IPEndPoint(hostIp, rdpPort));
            }
            else if (!string.IsNullOrWhiteSpace(host))
            {
                try
                {
                    var addresses = await Dns.GetHostAddressesAsync(host);
                    foreach (var addr in addresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork))
                    {
                        endpoints.Add(new IPEndPoint(addr, wolPort));
                        endpoints.Add(new IPEndPoint(addr, rdpPort));
                    }
                }
                catch { }
            }

            foreach (var ep in endpoints)
            {
                await client.SendAsync(packet, packet.Length, ep);
            }
        }
        catch
        {
            // Non-fatal WOL dispatch
        }
    }

    private void SkipWolWait()
    {
        _skipWolWait = true;
        BtnSkipWolWait.IsVisible = false;
        TxtLaunchStep.Text = "Skipping countdown, connecting now...";
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

        // Update active session banner state
        if (MainActivity.Instance != null && MainActivity.Instance.IsSessionActive)
        {
            var p = MainActivity.Instance.ActiveSessionProfile;
            if (p != null)
            {
                BannerActiveSession.IsVisible = true;
                TxtActiveSessionDetails.Text = $"{p.Name}  •  {p.Host}:{p.Port}";
            }
        }
        else
        {
            BannerActiveSession.IsVisible = false;
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
                }
            }
        }
        catch { }
    }

    private void OnRecoveryInputChanged()
    {
        if (_isFormattingRecovery) return;

        string raw = TxtRecoveryInput.Text ?? "";
        string clean = RecoveryCode.Normalize(raw);
        if (clean.Length > 52) clean = clean[..52];

        TxtRecoveryCharCount.Text = $"{clean.Length} / 52 characters";
        TxtRecoveryCharCount.Foreground = clean.Length == 52
            ? new SolidColorBrush(Color.Parse("#2FBF71"))
            : new SolidColorBrush(Color.Parse("#8A8A93"));

        string formattedStr = RecoveryCode.Format(clean);
        if (formattedStr != TxtRecoveryInput.Text)
        {
            _isFormattingRecovery = true;
            try
            {
                TxtRecoveryInput.Text = formattedStr;
                TxtRecoveryInput.CaretIndex = formattedStr.Length;
            }
            finally
            {
                _isFormattingRecovery = false;
            }
        }
    }

    private async Task ImportVaultFileAsync()
    {
        try
        {
            var top = TopLevel.GetTopLevel(this);
            if (top == null) return;

            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select Vault File (*.rdpv, *.rdpvault, *.enc, *.dat)",
                AllowMultiple = false
            });

            if (files.Count > 0)
            {
                var file = files[0];
                await using var stream = await file.OpenReadAsync();
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms);
                byte[] importedData = ms.ToArray();

                if (importedData.Length < 16)
                {
                    TxtFirstRunError.Text = "Selected file is too small or invalid.";
                    TxtFirstRunError.IsVisible = true;
                    return;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(VaultPath)!);
                await File.WriteAllBytesAsync(VaultPath, importedData);

                ShowLockScreen();
                TxtLockNotice.Text = "Vault file imported successfully! Enter master password to unlock.";
                TxtLockNotice.IsVisible = true;
                TxtLockError.IsVisible = false;
            }
        }
        catch (Exception ex)
        {
            TxtFirstRunError.Text = $"Import failed: {ex.Message}";
            TxtFirstRunError.IsVisible = true;
        }
    }

    private async Task ExportVaultFileAsync()
    {
        try
        {
            if (!File.Exists(VaultPath))
            {
                TxtVaultBackupStatus.Text = "No vault file exists to export.";
                TxtVaultBackupStatus.Foreground = new SolidColorBrush(Color.Parse("#EF4444"));
                TxtVaultBackupStatus.IsVisible = true;
                return;
            }

            var top = TopLevel.GetTopLevel(this);
            if (top == null) return;

            string defaultName = $"rdp_vault_backup_{DateTime.Now:yyyyMMdd_HHmm}.rdpv";
            var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export Encrypted Vault Backup",
                DefaultExtension = "rdpv",
                SuggestedFileName = defaultName
            });

            if (file != null)
            {
                byte[] vaultBytes = await File.ReadAllBytesAsync(VaultPath);
                await using var stream = await file.OpenWriteAsync();
                await stream.WriteAsync(vaultBytes);

                TxtVaultBackupStatus.Text = $"Exported successfully ({vaultBytes.Length:N0} bytes).";
                TxtVaultBackupStatus.Foreground = new SolidColorBrush(Color.Parse("#2FBF71"));
                TxtVaultBackupStatus.IsVisible = true;
            }
        }
        catch (Exception ex)
        {
            TxtVaultBackupStatus.Text = $"Export error: {ex.Message}";
            TxtVaultBackupStatus.Foreground = new SolidColorBrush(Color.Parse("#EF4444"));
            TxtVaultBackupStatus.IsVisible = true;
        }
    }

    private async Task ImportVaultFromSettingsAsync()
    {
        try
        {
            var top = TopLevel.GetTopLevel(this);
            if (top == null) return;

            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select Vault Backup to Restore (*.rdpv, *.rdpvault, *.enc, *.dat)",
                AllowMultiple = false
            });

            if (files.Count > 0)
            {
                var file = files[0];
                await using var stream = await file.OpenReadAsync();
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms);
                byte[] importedData = ms.ToArray();

                if (importedData.Length < 16)
                {
                    TxtVaultBackupStatus.Text = "Selected file is invalid or corrupted.";
                    TxtVaultBackupStatus.Foreground = new SolidColorBrush(Color.Parse("#EF4444"));
                    TxtVaultBackupStatus.IsVisible = true;
                    return;
                }

                _stagedRestoreBytes = importedData;
                OverlayConfirmRestoreVault.IsVisible = true;
            }
        }
        catch (Exception ex)
        {
            TxtVaultBackupStatus.Text = $"Restore error: {ex.Message}";
            TxtVaultBackupStatus.Foreground = new SolidColorBrush(Color.Parse("#EF4444"));
            TxtVaultBackupStatus.IsVisible = true;
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
                string backupPath = VaultPath + AppPaths.BackupSuffix;
                File.Copy(VaultPath, backupPath, overwrite: true);
            }

            await File.WriteAllBytesAsync(VaultPath, _stagedRestoreBytes);
            _stagedRestoreBytes = null;

            LockVault();
            TxtLockNotice.Text = "Vault restored from backup successfully! Enter master password to unlock.";
            TxtLockNotice.IsVisible = true;
            TxtLockError.IsVisible = false;
        }
        catch (Exception ex)
        {
            TxtVaultBackupStatus.Text = $"Restore error: {ex.Message}";
            TxtVaultBackupStatus.Foreground = new SolidColorBrush(Color.Parse("#EF4444"));
            TxtVaultBackupStatus.IsVisible = true;
        }
    }
}
