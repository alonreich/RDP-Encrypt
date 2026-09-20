using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
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
    private IntPtr _activeRdpContext = IntPtr.Zero;
    private System.Threading.CancellationTokenSource? _connectCts;
    private bool _biometricPromptSuppressed;

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

        // 4. Recovery Code Display
        BtnCopyRecoveryCode.Click += async (_, _) => await CopyRecoveryCodeToClipboardAsync();
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
        BtnSubmitRecoveryUnlock.Click += async (_, _) => await SubmitRecoveryUnlockAsync();
        BtnCancelRecoveryUnlock.Click += (_, _) => ShowLockScreen();

        // 7. Unlocked Screen
        BtnAddProfile.Click += (_, _) => ShowProfileEditor(null);
        BtnSettings.Click += (_, _) => ShowSettings();
        BtnLock.Click += (_, _) => LockVault();
        TxtSearch.TextChanged += (_, _) => RefreshProfilesList();
        BtnClearSearch.Click += (_, _) =>
        {
            TxtSearch.Text = "";
            RefreshProfilesList();
        };

        // 8. Profile Editor (Sticky Top Header and Bottom Buttons)
        BtnSaveProfile.Click += (_, _) => SaveProfile();
        BtnTopSaveProfile.Click += (_, _) => SaveProfile();
        BtnCancelProfile.Click += (_, _) =>
        {
            PanelProfileEditor.IsVisible = false;
            PanelUnlocked.IsVisible = true;
        };
        BtnTopCancelProfile.Click += (_, _) =>
        {
            PanelProfileEditor.IsVisible = false;
            PanelUnlocked.IsVisible = true;
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
        BtnToggleBiometrics.Click += async (_, _) => await ToggleBiometricsAsync();
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

        // Resolution & Security Defaults in Settings
        CmbSettingsResolution.SelectionChanged += (_, _) => SaveSettingsDefaults();
        CmbSettingsMultiMon.SelectionChanged += (_, _) => SaveSettingsDefaults();
        ChkSettingsSmartSizing.IsCheckedChanged += (_, _) => SaveSettingsDefaults();
        ChkSettingsSuppressCert.IsCheckedChanged += (_, _) => SaveSettingsDefaults();

        BtnCloseSettings.Click += (_, _) =>
        {
            PanelSettings.IsVisible = false;
            PanelUnlocked.IsVisible = true;
        };

        // 10. RDP Session
        BtnDisconnectSession.Click += (_, _) => DisconnectSession();
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

        // 2. Delete confirmation modal
        if (OverlayConfirmDelete.IsVisible)
        {
            OverlayConfirmDelete.IsVisible = false;
            return true;
        }

        // 3. Launch overlay cancel
        if (OverlayLaunch.IsVisible)
        {
            _connectCts?.Cancel();
            OverlayLaunch.IsVisible = false;
            return true;
        }

        // 4. Profile editor -> return to connections list
        if (PanelProfileEditor.IsVisible)
        {
            PanelProfileEditor.IsVisible = false;
            PanelUnlocked.IsVisible = true;
            return true;
        }

        // 5. Settings -> return to connections list
        if (PanelSettings.IsVisible)
        {
            PanelSettings.IsVisible = false;
            PanelUnlocked.IsVisible = true;
            return true;
        }

        // 6. Recovery unlock -> return to lock screen
        if (PanelRecoveryUnlock.IsVisible)
        {
            ShowLockScreen();
            return true;
        }

        // 7. Active session -> disconnect
        if (PanelSession.IsVisible)
        {
            DisconnectSession();
            return true;
        }

        // 8. Recovery display -> enter vault if already initialized
        if (PanelRecoveryDisplay.IsVisible && _payload != null)
        {
            PanelRecoveryDisplay.IsVisible = false;
            SwitchToUnlocked();
            return true;
        }

        // 9. Active search query -> clear search filter
        if (PanelUnlocked.IsVisible && !string.IsNullOrEmpty(TxtSearch.Text))
        {
            TxtSearch.Text = "";
            RefreshProfilesList();
            return true;
        }

        // 10. Root level (Unlocked list, Lock screen, or Setup wizard)
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
        PanelSession.IsVisible = panel == PanelSession;
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
        string clean = raw.Replace("-", "").Trim();
        if (clean.Length < 24) return raw;
        return $"{clean[..4]}-{clean[4..8]}-{clean[8..12]}-{clean[12..16]}-{clean[16..20]}-{clean[20..24]}";
    }

    private async Task CopyRecoveryCodeToClipboardAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top?.Clipboard != null && !string.IsNullOrEmpty(_activeRecoveryCode))
        {
            await top.Clipboard.SetTextAsync(_activeRecoveryCode);
            TxtCopyNotice.IsVisible = true;

            // Auto-wipe recovery code from clipboard after 60 seconds
            string codeToWipe = _activeRecoveryCode;
            _ = Task.Run(async () =>
            {
                await Task.Delay(60000);
                await Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    try
                    {
                        string? current = await top.Clipboard.GetTextAsync();
                        if (current == codeToWipe)
                        {
                            await top.Clipboard.ClearAsync();
                        }
                    }
                    catch { }
                });
            });
        }
    }

    // ==================== LOCK SCREEN & UNLOCK ====================

    private void ShowLockScreen()
    {
        ShowPanel(PanelLocked);
        TxtPassword.Text = "";
        TxtPassword.PasswordChar = '●';
        BtnToggleUnlockPass.Content = "👁";
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
        string code = TxtRecoveryInput.Text ?? "";
        string newPass = TxtRecoveryNewPass.Text ?? "";
        string confirmPass = TxtRecoveryConfirmPass.Text ?? "";

        if (string.IsNullOrWhiteSpace(code))
        {
            TxtRecoveryUnlockError.Text = "Please enter your 24-character recovery code.";
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

        string machineId = VaultCrypto.CurrentMachineId();
        bool hasBio = _vaultFile?.Seals?.Any(s => s.MachineId == machineId && !string.IsNullOrEmpty(s.KeyId) && !string.IsNullOrEmpty(s.TpmBlob)) == true;
        TxtUnlockedStatus.Text = hasBio
            ? "Vault Unlocked (Fingerprint / Face Protected)"
            : "Vault Unlocked (Password Mode)";

        RefreshProfilesList();
    }

    private void LockVault()
    {
        DisconnectSession();
        _payload = null;
        if (_masterKey != null)
        {
            CryptographicOperations.ZeroMemory(_masterKey);
            _masterKey = null;
        }
        _vaultFile = null;
        _biometricPromptSuppressed = false;

        ShowLockScreen();
    }

    // ==================== PROFILES MANAGEMENT ====================

    private void RefreshProfilesList()
    {
        PnlProfilesList.Children.Clear();
        if (_payload?.Profiles == null)
        {
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
                p.Notes.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        var list = profiles.ToList();
        TxtEmptyProfiles.IsVisible = list.Count == 0;

        foreach (var profile in list)
        {
            PnlProfilesList.Children.Add(CreateProfileCard(profile));
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
            Padding = new Thickness(14),
            Cursor = new Cursor(StandardCursorType.Hand)
        };

        // Tapping the card itself directly starts the connection
        border.PointerPressed += async (_, _) => await StartSessionAsync(profile);

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,8,Auto,8,Auto")
        };

        var info = new StackPanel
        {
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center
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

        // Copy Password Button (if password is saved)
        Button? btnCopyPass = null;
        if (profile.HasPassword)
        {
            btnCopyPass = new Button
            {
                Content = "📋 Pass",
                Background = new SolidColorBrush(Color.Parse("#1C1C21")),
                Foreground = new SolidColorBrush(Color.Parse("#EDEDED")),
                BorderBrush = new SolidColorBrush(Color.Parse("#2E2E35")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Height = 38,
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
                btnCopyPass.Content = "✓ Copied";
                btnCopyPass.Foreground = new SolidColorBrush(Color.Parse("#2FBF71"));
                _ = Task.Delay(2000).ContinueWith(_ =>
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        btnCopyPass.Content = "📋 Pass";
                        btnCopyPass.Foreground = new SolidColorBrush(Color.Parse("#EDEDED"));
                    });
                });
            };
        }

        var btnEdit = new Button
        {
            Content = "Edit",
            Background = new SolidColorBrush(Color.Parse("#2E2E35")),
            Foreground = new SolidColorBrush(Color.Parse("#EDEDED")),
            CornerRadius = new CornerRadius(4),
            Height = 38,
            Padding = new Thickness(14, 0),
            VerticalAlignment = VerticalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        btnEdit.Click += (_, e) =>
        {
            e.Handled = true;
            ShowProfileEditor(profile);
        };

        var btnConnect = new Button
        {
            Content = "Connect",
            Background = new SolidColorBrush(Color.Parse("#005FB8")),
            Foreground = Brushes.White,
            CornerRadius = new CornerRadius(4),
            Height = 38,
            Padding = new Thickness(16, 0),
            VerticalAlignment = VerticalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            FontWeight = FontWeight.SemiBold
        };
        btnConnect.Click += async (_, e) =>
        {
            e.Handled = true;
            await StartSessionAsync(profile);
        };

        Grid.SetColumn(info, 0);
        grid.Children.Add(info);

        if (btnCopyPass != null)
        {
            Grid.SetColumn(btnCopyPass, 1);
            grid.Children.Add(btnCopyPass);
        }

        Grid.SetColumn(btnEdit, 3);
        grid.Children.Add(btnEdit);

        Grid.SetColumn(btnConnect, 5);
        grid.Children.Add(btnConnect);

        border.Child = grid;
        return border;
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
            TxtProfileDomain.Text = "";

            // Resolution defaults
            CmbProfileResolution.SelectedIndex = 0;
            PnlProfileCustomRes.IsVisible = false;
            TxtProfileCustomWidth.Text = "1920";
            TxtProfileCustomHeight.Text = "1080";
            CmbProfileMultiMon.SelectedIndex = 0;
            ChkProfileSmartSizing.IsChecked = true;

            ChkProfileEnableWol.IsChecked = false;
            TxtProfileWolMac.Text = "";
            TxtProfileWolPort.Text = "9";
            TxtProfileWolWait.Text = "5";
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
            TxtProfileDomain.Text = profile.GatewayHost;

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
        int.TryParse(TxtProfileWolPort.Text, out wolPort);

        int wolWait = 5;
        int.TryParse(TxtProfileWolWait.Text, out wolWait);

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

        if (_editingProfile == null)
        {
            var p = new RdpProfile
            {
                Name = name,
                Host = host,
                Port = port,
                Username = TxtProfileUser.Text?.Trim() ?? "",
                Password = TxtProfilePass.Text ?? "",
                GatewayHost = TxtProfileDomain.Text?.Trim() ?? "",
                ResolutionPreset = resPreset,
                Width = customWidth,
                Height = customHeight,
                MultiMonOverride = multiMonOverride,
                SmartSizingOverride = smartSizingOverride,
                EnableWol = ChkProfileEnableWol.IsChecked == true,
                WolMacAddress = TxtProfileWolMac.Text?.Trim() ?? "",
                WolPort = wolPort > 0 ? wolPort : 9,
                WolWaitSeconds = wolWait > 0 ? wolWait : 5,
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
            _editingProfile.GatewayHost = TxtProfileDomain.Text?.Trim() ?? "";
            _editingProfile.ResolutionPreset = resPreset;
            _editingProfile.Width = customWidth;
            _editingProfile.Height = customHeight;
            _editingProfile.MultiMonOverride = multiMonOverride;
            _editingProfile.SmartSizingOverride = smartSizingOverride;
            _editingProfile.EnableWol = ChkProfileEnableWol.IsChecked == true;
            _editingProfile.WolMacAddress = TxtProfileWolMac.Text?.Trim() ?? "";
            _editingProfile.WolPort = wolPort > 0 ? wolPort : 9;
            _editingProfile.WolWaitSeconds = wolWait > 0 ? wolWait : 5;
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

    private async Task CopyPasswordToClipboardAsync(string password, string profileName)
    {
        if (string.IsNullOrEmpty(password)) return;
        var top = TopLevel.GetTopLevel(this);
        if (top?.Clipboard != null)
        {
            await top.Clipboard.SetTextAsync(password);

            // Auto-wipe password from clipboard after 30 seconds
            _ = Task.Run(async () =>
            {
                await Task.Delay(30000);
                await Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    try
                    {
                        string? current = await top.Clipboard.GetTextAsync();
                        if (current == password)
                        {
                            await top.Clipboard.ClearAsync();
                        }
                    }
                    catch { }
                });
            });
        }
    }

    private async Task StartSessionAsync(RdpProfile profile)
    {
        _connectCts = new System.Threading.CancellationTokenSource();
        var ct = _connectCts.Token;

        TxtLaunchTitle.Text = profile.EnableWol ? "WAKING COMPUTER & CONNECTING" : "CONNECTING TO REMOTE COMPUTER";
        TxtLaunchTarget.Text = $"{profile.Name}  •  {profile.Host}:{profile.Port}";
        ProgLaunch.IsIndeterminate = true;
        ProgLaunch.Value = 0;
        TxtLaunchCountdown.IsVisible = false;
        TxtLaunchStep.Text = "Preparing connection...";
        TxtLaunchSubStatus.Text = "";
        BtnCancelLaunch.IsEnabled = true;
        OverlayLaunch.IsVisible = true;

        try
        {
            // Auto-copy password to clipboard with 30s auto-wipe if saved
            if (profile.HasPassword)
            {
                await CopyPasswordToClipboardAsync(profile.Password, profile.Name);
                TxtLaunchSubStatus.Text = "Password Ready";
                TxtLaunchStep.Text = "Password copied to clipboard (clears in 30s). Paste when prompted by Remote Desktop!";
                await Task.Delay(1000, ct);
            }

            // 1. Wake-on-LAN dispatch if enabled
            if (profile.EnableWol && !string.IsNullOrWhiteSpace(profile.WolMacAddress))
            {
                TxtLaunchSubStatus.Text = "Sending wake signal";
                TxtLaunchStep.Text = $"Transmitting wake signal to {profile.WolMacAddress}...";
                await DispatchWolAsync(profile);

                if (profile.WolWaitSeconds > 0)
                {
                    int total = profile.WolWaitSeconds;
                    for (int s = total; s > 0; s--)
                    {
                        if (ct.IsCancellationRequested) return;

                        double percent = 100.0 * (total - s) / total;
                        ProgLaunch.IsIndeterminate = false;
                        ProgLaunch.Value = percent;
                        TxtLaunchCountdown.Text = $"{s}s remaining";
                        TxtLaunchCountdown.IsVisible = true;
                        TxtLaunchSubStatus.Text = "Waking Computer";
                        TxtLaunchStep.Text = $"Wake signal broadcasted. Waiting for computer to start ({s}s remaining)...";

                        await Task.Delay(1000, ct);
                    }
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
                await Task.Delay(2000, ct);
            }
            else if (result == RdpLaunchStatus.RedirectedToStore)
            {
                await Task.Delay(3000, ct);
            }
            else
            {
                await Task.Delay(4000, ct);
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

    private void DisconnectSession()
    {
        if (_activeRdpContext != IntPtr.Zero)
        {
            try
            {
                FreeRdpClient.freerdp_client_stop(_activeRdpContext);
                FreeRdpClient.freerdp_client_context_free(_activeRdpContext);
            }
            catch { }
            _activeRdpContext = IntPtr.Zero;
        }

        PanelSession.IsVisible = false;
        if (_payload != null)
        {
            PanelUnlocked.IsVisible = true;
        }
        else
        {
            PanelLocked.IsVisible = true;
        }
    }
}
