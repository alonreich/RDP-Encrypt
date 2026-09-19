using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Button = Avalonia.Controls.Button;
using TextBox = Avalonia.Controls.TextBox;
using ProgressBar = Avalonia.Controls.ProgressBar;
using RDPVault;
using RDPVault.Android.Rdp;

namespace RDPVault.Android.Views;

public partial class MainView : UserControl
{
    private VaultFile? _vaultFile;
    private VaultPayload? _payload;
    private byte[]? _masterKey;
    private IntPtr _activeRdpContext = IntPtr.Zero;

    public MainView()
    {
        InitializeComponent();
        WireEvents();
    }

    private void WireEvents()
    {
        BtnUnlock.Click += async (_, _) => await UnlockWithPasswordAsync();
        BtnLock.Click += (_, _) => LockVault();
        TxtSearch.PropertyChanged += (_, e) =>
        {
            if (e.Property.Name == nameof(TextBox.Text)) RefreshProfilesList();
        };
        BtnClearSearch.Click += (_, _) =>
        {
            TxtSearch.Text = "";
            RefreshProfilesList();
        };
        BtnDisconnectSession.Click += (_, _) => DisconnectSession();
    }

    private async Task UnlockWithPasswordAsync()
    {
        string password = TxtPassword.Text ?? "";
        if (string.IsNullOrEmpty(password)) return;

        PnlUnlockProgress.IsVisible = true;
        BtnUnlock.IsEnabled = false;

        try
        {
            string vaultPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), AppPaths.VaultFileName);

            // If vault doesn't exist yet on device, initialize fresh vault
            if (!File.Exists(vaultPath))
            {
                var payload = new VaultPayload();
                var file = VaultCrypto.CreateVault(password, payload, vaultPath, out string _);
                var (master, _) = VaultCrypto.Open(file, password);
                _vaultFile = file;
                _masterKey = master;
                _payload = payload;
            }
            else
            {
                // Run Argon2id KDF off UI thread to keep mobile UI responsive
                var file = System.Text.Json.JsonSerializer.Deserialize(
                    File.ReadAllText(vaultPath), VaultJsonContext.Default.VaultFile)
                    ?? throw new InvalidDataException("Vault file is empty or corrupted.");
                var (master, payload) = await Task.Run(() => VaultCrypto.Open(file, password));
                _vaultFile = file;
                _masterKey = master;
                _payload = payload;
            }

            // Successfully unlocked
            TxtPassword.Text = "";
            PanelLocked.IsVisible = false;
            PanelUnlocked.IsVisible = true;
            RefreshProfilesList();
        }
        catch (Exception ex)
        {
            TxtUnlockStatus.Text = "Unlock failed: " + ex.Message;
        }
        finally
        {
            PnlUnlockProgress.IsVisible = false;
            BtnUnlock.IsEnabled = true;
        }
    }

    private void LockVault()
    {
        DisconnectSession();
        _payload = null;
        if (_masterKey != null)
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(_masterKey);
            _masterKey = null;
        }

        PanelSession.IsVisible = false;
        PanelUnlocked.IsVisible = false;
        PanelLocked.IsVisible = true;
    }

    private void RefreshProfilesList()
    {
        PnlProfilesList.Children.Clear();
        if (_payload?.Profiles == null) return;

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

        foreach (var profile in profiles)
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
            ColumnDefinitions = new ColumnDefinitions("*,Auto")
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

        info.Children.Add(new TextBlock
        {
            Text = $"{profile.Host}:{profile.Port}  •  {profile.Username}",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse("#8A8A93"))
        });

        var btnConnect = new Button
        {
            Content = "Connect",
            Background = new SolidColorBrush(Color.Parse("#005FB8")),
            Foreground = Brushes.White,
            CornerRadius = new CornerRadius(4),
            Height = 36,
            Padding = new Thickness(16, 0),
            VerticalAlignment = VerticalAlignment.Center
        };

        btnConnect.Click += (_, _) => StartSession(profile);

        Grid.SetColumn(info, 0);
        Grid.SetColumn(btnConnect, 1);
        grid.Children.Add(info);
        grid.Children.Add(btnConnect);

        border.Child = grid;
        return border;
    }

    private void StartSession(RdpProfile profile)
    {
        try
        {
            PanelUnlocked.IsVisible = false;
            PanelSession.IsVisible = true;

            int width = (int)Math.Max(800, Bounds.Width);
            int height = (int)Math.Max(600, Bounds.Height);

            _activeRdpContext = FreeRdpClient.Connect(profile, _payload?.Settings, width, height);
        }
        catch (Exception ex)
        {
            DisconnectSession();
            // Handle connection error cleanly
        }
    }

    private void DisconnectSession()
    {
        if (_activeRdpContext != IntPtr.Zero)
        {
            FreeRdpClient.freerdp_client_stop(_activeRdpContext);
            FreeRdpClient.freerdp_client_context_free(_activeRdpContext);
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
