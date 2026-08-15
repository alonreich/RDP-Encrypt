using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Threading;
using System;
using System.IO;

namespace RDPVault;

public partial class MainWindow : Window
{
    private Border LockedPanel => this.FindControl<Border>("LockedPanel")!;
    private Grid MainPanel => this.FindControl<Grid>("MainPanel")!;
    private TextBox TxtPassword => this.FindControl<TextBox>("TxtPassword")!;
    private Button BtnHello => this.FindControl<Button>("BtnHello")!;
    private TextBlock TxtLockError => this.FindControl<TextBlock>("TxtLockError")!;
    private ListBox LstProfiles => this.FindControl<ListBox>("LstProfiles")!;
    private TextBlock TxtCount => this.FindControl<TextBlock>("TxtCount")!;

    public MainWindow()
    {
        InitializeComponent();
        
        SessionManager.Current.Locked += OnLocked;
        SessionManager.Current.ShowRequested += OnShowRequested;
        
        UpdateUIState();
        CheckHelloAvailability();
    }

    private void UpdateUIState()
    {
        if (SessionManager.Current.IsUnlocked)
        {
            LockedPanel.IsVisible = false;
            MainPanel.IsVisible = true;
            RefreshProfiles();
        }
        else
        {
            LockedPanel.IsVisible = true;
            MainPanel.IsVisible = false;
            TxtPassword.Text = "";
            TxtPassword.Focus();
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
        bool success = await SessionManager.Current.UnlockWithHelloAsync();
        if (success)
        {
            Dispatcher.UIThread.InvokeAsync(UpdateUIState);
        }
        else
        {
            Dispatcher.UIThread.InvokeAsync(() =>
            {
                TxtLockError.Text = "Hardware signature rejected.";
                TxtLockError.IsVisible = true;
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

    private void SubmitPassword()
    {
        TxtLockError.IsVisible = false;
        if (string.IsNullOrWhiteSpace(TxtPassword.Text)) return;

        try
        {
            if (!File.Exists(SessionManager.Current.VaultPath))
            {
                // First run
                var payload = new VaultPayload();
                SessionManager.Current.CreateNew(TxtPassword.Text, payload);
                UpdateUIState();
                return;
            }

            SessionManager.Current.UnlockWithPassword(TxtPassword.Text);
            UpdateUIState();
        }
        catch (Exception ex)
        {
            TxtLockError.Text = ex.Message;
            TxtLockError.IsVisible = true;
        }
    }

    private void BtnHello_Click(object? sender, RoutedEventArgs e)
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