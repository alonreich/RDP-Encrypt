using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using System;
using System.Threading.Tasks;

namespace RDPVault;

public partial class SetupWindow : Window
{
    private string _logText = "";

    public SetupWindow()
    {
        InitializeComponent();

        if (System.IO.File.Exists(InstallerService.InstalledExe))
        {
            this.Title = "Upgrade RDP Vault";
            TxtInstallTitle.Text = "Upgrade Existing App";
            TxtInstallSubtitle.Text = "Preserves your vault and settings";
            this.FindControl<Button>("BtnCleanInstall").IsVisible = true;
        }
    }

    private void BtnPortable_Click(object? sender, RoutedEventArgs e)
    {
        var main = new MainWindow();
        main.Show();
        this.Close();
    }

    private void BtnCleanInstall_Click(object? sender, RoutedEventArgs e)
    {
        try 
        {
            string vaultPath = System.IO.Path.Combine(InstallerService.InstallDir, "vault.dat");
            if (System.IO.File.Exists(vaultPath)) System.IO.File.Delete(vaultPath);
        }
        catch { }
        BtnInstall_Click(null, null);
    }

    private async void BtnInstall_Click(object? sender, RoutedEventArgs e)
    {
        PnlSelection.IsVisible = false;
        PnlProgress.IsVisible = true;
        
        await Task.Run(async () =>
        {
            try
            {
                // Hook into the InstallerService using a custom logging delegate
                InstallerService.InstallWithProgress(Log);
                
                Dispatcher.UIThread.InvokeAsync(() =>
                {
                    ProgInstall.IsIndeterminate = false;
                    ProgInstall.Value = 100;
                    BtnFinish.IsVisible = true;
                    Log("SUCCESS: Installation completed successfully.");
                });
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.InvokeAsync(() =>
                {
                    ProgInstall.IsIndeterminate = false;
                    ProgInstall.Foreground = Avalonia.Media.Brushes.Red;
                    Log("ERROR: " + ex.Message);
                });
            }
        });
    }

    private void BtnFinish_Click(object? sender, RoutedEventArgs e)
    {
        InstallerService.LaunchInstalledAndExit();
    }

    private void Log(string message)
    {
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            _logText += $"[{DateTime.Now:HH:mm:ss}] {message}\n";
            TxtLog.Text = _logText;
            ScrollLog.ScrollToEnd();
        });
    }
}
