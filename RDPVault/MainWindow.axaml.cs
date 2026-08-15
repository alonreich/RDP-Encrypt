using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Threading;
using System;
using System.IO;

namespace RDPVault;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        
        SessionManager.Current.Locked += OnLocked;
        SessionManager.Current.ShowRequested += OnShowRequested;
        
        UpdateUIState();
        
        this.Loaded += async (s, e) => 
        {
            // Yield to ensure Avalonia window renders and foregrounds before spawning OS Hello dialog
            await System.Threading.Tasks.Task.Delay(300);
            CheckHelloAvailability();
        };
    }

    private void UpdateUIState()
    {
        bool isLocked = SessionManager.Current.Payload == null;
        LockedPanel.IsVisible = isLocked;
        MainPanel.IsVisible = !isLocked;

        if (isLocked)
        {
            TxtPassword.Text = "";
            TxtLockError.IsVisible = false;
            TxtLockStatus.IsVisible = false;
            BtnHello.IsVisible = SessionManager.Current.HelloSealAvailable();
            TxtPassword.Focus();
        }
        else
        {
            RefreshProfiles();
        }
    }

    private void CheckHelloAvailability()
    {
        if (SessionManager.Current.HelloSealAvailable())
        {
            BtnHello.IsVisible = true;
            _ = AttemptHelloUnlockAsync();
        }
        else
        {
            BtnHello.IsVisible = false;
        }
    }

    private async System.Threading.Tasks.Task AttemptHelloUnlockAsync()
    {
        TxtLockError.IsVisible = false;
        TxtLockStatus.Text = "Validating hardware TPM signature. Please wait...";
        TxtLockStatus.IsVisible = true;
        BtnHello.IsEnabled = false;

        bool success = await System.Threading.Tasks.Task.Run(async () => await SessionManager.Current.UnlockWithHelloAsync());
        
        if (success)
        {
            Dispatcher.UIThread.InvokeAsync(UpdateUIState);
        }
        else
        {
            Dispatcher.UIThread.InvokeAsync(() =>
            {
                TxtLockStatus.IsVisible = false;
                TxtLockError.Text = "Hardware signature rejected.";
                TxtLockError.IsVisible = true;
                BtnHello.IsEnabled = true;
            });
        }
    }

    private void RefreshProfiles()
    {
        var profiles = SessionManager.Current.Payload?.Profiles;
        if (profiles != null)
        {
            LstProfiles.ItemsSource = null;
            LstProfiles.ItemsSource = profiles;
            TxtCount.Text = $"{profiles.Count} Profile{(profiles.Count != 1 ? "s" : "")}";
        }
    }

    private void OnLocked() => Dispatcher.UIThread.InvokeAsync(UpdateUIState);
    private void OnShowRequested() => Dispatcher.UIThread.InvokeAsync(() =>
    {
        this.Show();
        if (this.WindowState == WindowState.Minimized) this.WindowState = WindowState.Normal;
        this.Activate();
    });

    private void BtnUnlock_Click(object? sender, RoutedEventArgs e) => SubmitPassword();

    private void TxtPassword_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) SubmitPassword();
    }

    private async void SubmitPassword()
    {
        TxtLockError.IsVisible = false;
        TxtLockStatus.IsVisible = false;
        if (string.IsNullOrWhiteSpace(TxtPassword.Text)) return;

        string pwd = TxtPassword.Text;
        TxtPassword.IsEnabled = false;
        BtnUnlock.IsEnabled = false;
        BtnHello.IsEnabled = false;

        try
        {
            if (!File.Exists(SessionManager.Current.VaultPath))
            {
                TxtLockStatus.Text = "Generating high-entropy cryptographic keys. Please wait...";
                TxtLockStatus.IsVisible = true;
                
                var payload = new VaultPayload();
                await System.Threading.Tasks.Task.Run(() => SessionManager.Current.CreateNew(pwd, payload));
                
                UpdateUIState();
                return;
            }

            TxtLockStatus.Text = "Decrypting vault structure...";
            TxtLockStatus.IsVisible = true;

            await System.Threading.Tasks.Task.Run(() => SessionManager.Current.UnlockWithPassword(pwd));
            UpdateUIState();
        }
        catch (Exception ex)
        {
            TxtLockStatus.IsVisible = false;
            TxtLockError.Text = ex.Message;
            TxtLockError.IsVisible = true;
        }
        finally
        {
            TxtPassword.IsEnabled = true;
            BtnUnlock.IsEnabled = true;
            BtnHello.IsEnabled = true;
            TxtPassword.Text = "";
            TxtPassword.Focus();
        }
    }

    private async void BtnHello_Click(object? sender, RoutedEventArgs e)
    {
        TxtLockError.IsVisible = false;
        _ = AttemptHelloUnlockAsync();
    }

    private void BtnLock_Click(object? sender, RoutedEventArgs e)
    {
        SessionManager.Current.Lock(killSessions: false);
    }

    private void BtnSettings_Click(object? sender, RoutedEventArgs e)
    {
        // Settings Window
        var sw = new SettingsWindow();
        sw.ShowDialog(this);
    }

    private async void BtnAddProfile_Click(object? sender, RoutedEventArgs e)
    {
        var editor = new ProfileEditorWindow();
        var result = await editor.ShowDialog<bool>(this);
        if (result && SessionManager.Current.Payload != null)
        {
            SessionManager.Current.Payload.Profiles.Add(editor.Profile);
            SessionManager.Current.Save();
            RefreshProfiles();
        }
    }

    private void BtnConnect_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is RdpProfile p)
        {
            RdpLauncher.Launch(p);
        }
    }

    private void BtnShortcut_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is RdpProfile p)
        {
            ShortcutGenerator.CreateDesktopShortcut(p);
        }
    }

    private async void BtnEdit_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is RdpProfile p)
        {
            var editor = new ProfileEditorWindow(p);
            var result = await editor.ShowDialog<bool>(this);
            if (result)
            {
                SessionManager.Current.Save();
                RefreshProfiles();
            }
        }
    }

    private void BtnDelete_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is RdpProfile p && SessionManager.Current.Payload != null)
        {
            SessionManager.Current.Payload.Profiles.Remove(p);
            SessionManager.Current.Save();
            RefreshProfiles();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        SessionManager.Current.Dispose();
        base.OnClosed(e);
        Environment.Exit(0);
    }
}