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
        // 1. First-Time Setup Wizard
        BtnFirstRunCreate.Click += async (_, _) => await CreateVaultFirstTimeAsync();

        // 2. Recovery Code Display
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

        // 3. Lock Screen
        BtnBiometricUnlock.Click += async (_, _) => await UnlockWithBiometricsAsync();
        BtnUnlock.Click += async (_, _) => await UnlockWithPasswordAsync();
        TxtPassword.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter) await UnlockWithPasswordAsync();
        };
        BtnShowRecovery.Click += (_, _) => ShowRecoveryUnlock();

        // 4. Recovery Unlock
        BtnSubmitRecoveryUnlock.Click += async (_, _) => await SubmitRecoveryUnlockAsync();
        BtnCancelRecoveryUnlock.Click += (_, _) => ShowLockScreen();

        // 5. Unlocked Screen
        BtnAddProfile.Click += (_, _) => ShowProfileEditor(null);
        BtnSettings.Click += (_, _) => ShowSettings();
        BtnLock.Click += (_, _) => LockVault();
        TxtSearch.TextChanged += (_, _) => RefreshProfilesList();
        BtnClearSearch.Click += (_, _) =>
        {
            TxtSearch.Text = "";
            RefreshProfilesList();
        };

        // 6. Profile Editor
        BtnSaveProfile.Click += (_, _) => SaveProfile();
        BtnCancelProfile.Click += (_, _) =>
        {
            PanelProfileEditor.IsVisible = false;
            PanelUnlocked.IsVisible = true;
        };
        BtnDeleteProfile.Click += (_, _) => DeleteProfile();

        // 7. Settings
        BtnToggleBiometrics.Click += async (_, _) => await ToggleBiometricsAsync();
        BtnViewRecoveryCode.Click += (_, _) => ViewCurrentRecoveryCode();
        BtnChangeMasterPassword.Click += async (_, _) => await ChangeMasterPasswordAsync();
        BtnCloseSettings.Click += (_, _) =>
        {
            PanelSettings.IsVisible = false;
            PanelUnlocked.IsVisible = true;
        };

        // 8. RDP Session
        BtnDisconnectSession.Click += (_, _) => DisconnectSession();
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
        TxtFirstRunConfirm.Text = "";
        TxtFirstRunError.IsVisible = false;
        PnlFirstRunProgress.IsVisible = false;
        BtnFirstRunCreate.IsEnabled = true;

        bool bioAvail = MainActivity.Instance != null && AndroidHardwareKeyStore.IsBiometricAvailable(MainActivity.Instance);
        ChkFirstRunBiometrics.IsChecked = bioAvail;
        ChkFirstRunBiometrics.IsEnabled = bioAvail;
        if (!bioAvail)
        {
            ChkFirstRunBiometrics.Content = "Biometrics unavailable (Not enrolled in device settings)";
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
                    string keyId = Guid.NewGuid().ToString("N");
                    AndroidHardwareKeyStore.GenerateHardwareKey(MainActivity.Instance, keyId, requireBiometrics: true);
                    var cipher = AndroidHardwareKeyStore.GetInitializedCipher(keyId, (int)Javax.Crypto.CipherMode.EncryptMode);

                    var (success, authedCipher, _) = await AndroidHardwareKeyStore.AuthenticatePromptAsync(
                        MainActivity.Instance, cipher, "Enroll Biometrics", "Confirm fingerprint or face to protect your master key", "Skip Biometrics");

                    if (success && authedCipher != null)
                    {
                        string tpmBlob = AndroidHardwareKeyStore.SealMasterKey(keyId, masterKey, authedCipher);
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
                    // Fall back cleanly to password-only vault if user cancels or hardware errors
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
        }
    }

    // ==================== LOCK SCREEN & UNLOCK ====================

    private void ShowLockScreen()
    {
        ShowPanel(PanelLocked);
        TxtPassword.Text = "";
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
    }

    private async Task UnlockWithBiometricsAsync()
    {
        if (MainActivity.Instance == null || _vaultFile == null) return;

        string machineId = VaultCrypto.CurrentMachineId();
        var seal = _vaultFile.Seals.FirstOrDefault(s => s.MachineId == machineId && !string.IsNullOrEmpty(s.KeyId) && !string.IsNullOrEmpty(s.TpmBlob));
        if (seal == null)
        {
            TxtLockError.Text = "Biometrics not enrolled for this device. Unlock with Master Password.";
            TxtLockError.IsVisible = true;
            return;
        }

        try
        {
            string[] parts = seal.TpmBlob.Split(':');
            if (parts.Length != 2) throw new FormatException("Invalid seal format.");
            byte[] iv = Convert.FromBase64String(parts[0]);

            var cipher = AndroidHardwareKeyStore.GetInitializedCipher(seal.KeyId, (int)Javax.Crypto.CipherMode.DecryptMode, iv);

            var (success, authedCipher, error) = await AndroidHardwareKeyStore.AuthenticatePromptAsync(
                MainActivity.Instance, cipher, "Unlock RDP Vault", "Confirm fingerprint or face to unseal vault", "Use Master Password");

            if (!success || authedCipher == null)
            {
                if (!string.IsNullOrEmpty(error) && !error.Contains("cancel", StringComparison.OrdinalIgnoreCase))
                {
                    TxtLockError.Text = "Biometrics: " + error;
                    TxtLockError.IsVisible = true;
                }
                return;
            }

            byte[] masterKey = AndroidHardwareKeyStore.UnsealMasterKey(seal.TpmBlob, authedCipher);
            var payload = VaultCrypto.OpenPayload(_vaultFile, masterKey);

            _masterKey = masterKey;
            _payload = payload;

            SwitchToUnlocked();
        }
        catch (Exception ex)
        {
            TxtLockError.Text = "Biometric unlock failed: " + ex.Message + ". Unlock with Master Password.";
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

            TxtPassword.Text = "";
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
        TxtRecoveryConfirmPass.Text = "";
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
            ? "Vault Unlocked (StrongBox Protected)"
            : "Vault Unlocked (Master Password Mode)";

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
            Padding = new Thickness(14)
        };

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,8,Auto")
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
            Foreground = new SolidColorBrush(Color.Parse("#FFFFFF"))
        });

        string wolBadge = profile.EnableWol ? "  •  WOL" : "";
        info.Children.Add(new TextBlock
        {
            Text = $"{profile.Host}:{profile.Port}  •  {profile.Username}{wolBadge}",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse("#8A8A93"))
        });

        var btnEdit = new Button
        {
            Content = "Edit",
            Background = new SolidColorBrush(Color.Parse("#2E2E35")),
            Foreground = new SolidColorBrush(Color.Parse("#EDEDED")),
            CornerRadius = new CornerRadius(4),
            Height = 36,
            Padding = new Thickness(12, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        btnEdit.Click += (_, _) => ShowProfileEditor(profile);

        var btnConnect = new Button
        {
            Content = "Connect",
            Background = new SolidColorBrush(Color.Parse("#005FB8")),
            Foreground = Brushes.White,
            CornerRadius = new CornerRadius(4),
            Height = 36,
            Padding = new Thickness(14, 0),
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeight.SemiBold
        };
        btnConnect.Click += async (_, _) => await StartSessionAsync(profile);

        Grid.SetColumn(info, 0);
        Grid.SetColumn(btnEdit, 1);
        Grid.SetColumn(btnConnect, 3);
        grid.Children.Add(info);
        grid.Children.Add(btnEdit);
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
            TxtProfileDomain.Text = "";
            ChkProfileEnableWol.IsChecked = false;
            TxtProfileWolMac.Text = "";
            TxtProfileWolPort.Text = "9";
            TxtProfileWolWait.Text = "5";
            TxtProfileNotes.Text = "";
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
            TxtProfileDomain.Text = profile.GatewayHost;
            ChkProfileEnableWol.IsChecked = profile.EnableWol;
            TxtProfileWolMac.Text = profile.WolMacAddress;
            TxtProfileWolPort.Text = profile.WolPort.ToString();
            TxtProfileWolWait.Text = profile.WolWaitSeconds.ToString();
            TxtProfileNotes.Text = profile.Notes;
            BtnDeleteProfile.IsVisible = true;
        }

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
                EnableWol = ChkProfileEnableWol.IsChecked == true,
                WolMacAddress = TxtProfileWolMac.Text?.Trim() ?? "",
                WolPort = wolPort > 0 ? wolPort : 9,
                WolWaitSeconds = wolWait > 0 ? wolWait : 5,
                Notes = TxtProfileNotes.Text?.Trim() ?? ""
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
            _editingProfile.EnableWol = ChkProfileEnableWol.IsChecked == true;
            _editingProfile.WolMacAddress = TxtProfileWolMac.Text?.Trim() ?? "";
            _editingProfile.WolPort = wolPort > 0 ? wolPort : 9;
            _editingProfile.WolWaitSeconds = wolWait > 0 ? wolWait : 5;
            _editingProfile.Notes = TxtProfileNotes.Text?.Trim() ?? "";
        }

        VaultCrypto.Save(_vaultFile, _masterKey, _payload, VaultPath);

        PanelProfileEditor.IsVisible = false;
        PanelUnlocked.IsVisible = true;
        RefreshProfilesList();
    }

    private void DeleteProfile()
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
        TxtSettingsNewPass.Text = "";
        TxtSettingsConfirmPass.Text = "";
        TxtChangePasswordError.IsVisible = false;
        PnlChangePassProgress.IsVisible = false;

        string machineId = VaultCrypto.CurrentMachineId();
        bool enrolled = _vaultFile?.Seals?.Any(s => s.MachineId == machineId && !string.IsNullOrEmpty(s.KeyId) && !string.IsNullOrEmpty(s.TpmBlob)) == true;
        TxtBiometricStatus.Text = enrolled
            ? "StrongBox Biometrics: Active on this device"
            : "Biometrics: Not enrolled on this device";
        BtnToggleBiometrics.Content = enrolled
            ? "Remove Biometric Seal"
            : "Enroll Biometrics";
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

            TxtBiometricStatus.Text = "Biometrics: Not enrolled on this device";
            BtnToggleBiometrics.Content = "Enroll Biometrics";
        }
        else
        {
            // Enroll seal
            try
            {
                string keyId = Guid.NewGuid().ToString("N");
                AndroidHardwareKeyStore.GenerateHardwareKey(MainActivity.Instance, keyId, requireBiometrics: true);
                var cipher = AndroidHardwareKeyStore.GetInitializedCipher(keyId, (int)Javax.Crypto.CipherMode.EncryptMode);

                var (success, authedCipher, err) = await AndroidHardwareKeyStore.AuthenticatePromptAsync(
                    MainActivity.Instance, cipher, "Enroll Biometrics", "Confirm fingerprint or face to protect your master key", "Cancel");

                if (success && authedCipher != null)
                {
                    string tpmBlob = AndroidHardwareKeyStore.SealMasterKey(keyId, _masterKey, authedCipher);
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

                    TxtBiometricStatus.Text = "StrongBox Biometrics: Active on this device";
                    BtnToggleBiometrics.Content = "Remove Biometric Seal";
                }
            }
            catch (Exception ex)
            {
                TxtBiometricStatus.Text = "Enrollment error: " + ex.Message;
            }
        }
    }

    private void ViewCurrentRecoveryCode()
    {
        // View recovery code notice
        if (_vaultFile != null && !string.IsNullOrEmpty(_vaultFile.RecoverySalt))
        {
            ShowRecoveryDisplay("Recovery Code is active. To obtain a fresh printed copy, use Change Master Password.");
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

    private async Task StartSessionAsync(RdpProfile profile)
    {
        // 1. Wake-on-LAN dispatch if enabled
        if (profile.EnableWol && !string.IsNullOrWhiteSpace(profile.WolMacAddress))
        {
            await DispatchWolAsync(profile);
            if (profile.WolWaitSeconds > 0)
            {
                await Task.Delay(profile.WolWaitSeconds * 1000);
            }
        }

        // 2. Launch RDP Session
        try
        {
            PanelUnlocked.IsVisible = false;
            PanelSession.IsVisible = true;

            int width = (int)Math.Max(800, Bounds.Width);
            int height = (int)Math.Max(600, Bounds.Height);

            _activeRdpContext = FreeRdpClient.Connect(profile, _payload?.Settings, width, height);
        }
        catch (Exception)
        {
            DisconnectSession();
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

            // Transmit to broadcast and host unicast across WOL port & custom RDP port
            var endpoints = new List<IPEndPoint>
            {
                new IPEndPoint(IPAddress.Broadcast, profile.WolPort > 0 ? profile.WolPort : 9),
                new IPEndPoint(IPAddress.Broadcast, profile.Port)
            };

            if (IPAddress.TryParse(profile.Host, out var hostIp))
            {
                endpoints.Add(new IPEndPoint(hostIp, profile.WolPort > 0 ? profile.WolPort : 9));
                endpoints.Add(new IPEndPoint(hostIp, profile.Port));
            }

            foreach (var ep in endpoints)
            {
                await client.SendAsync(packet, packet.Length, ep);
            }
        }
        catch
        {
            // WOL dispatch non-fatal
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
