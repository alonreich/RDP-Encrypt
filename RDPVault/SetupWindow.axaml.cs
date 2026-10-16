using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using System;
using System.IO;
using System.Threading.Tasks;

namespace RDPVault;

public partial class SetupWindow : Window
{
    private string _logText = "";

    public SetupWindow()
    {
        InitializeComponent();

        if (File.Exists(InstallerService.InstalledExe))
        {
            Title = "Upgrade RDP Vault";
            TxtInstallTitle.Text = "Upgrade the installed copy";
            TxtInstallSubtitle.Text = "Keeps your vault and settings";
            BtnCleanInstall.IsVisible = InstallerService.InstalledVaultExists();
        }
    }

    private void BtnPortable_Click(object? sender, RoutedEventArgs e)
    {
        var main = new MainWindow();
        main.Show();
        Close();
    }

    private async void BtnCleanInstall_Click(object? sender, RoutedEventArgs e)
    {
        bool go = await Dialogs.ConfirmAsync(this, "Erase the installed vault?",
            $"Every profile and saved password in {AppPaths.InstalledVaultPath} will be permanently deleted, " +
            "including its automatic backup copy. Type ERASE to confirm.",
            confirmText: "Erase and install", danger: true, typeToConfirm: "ERASE");
        if (!go) return;

        try
        {
            foreach (string path in new[]
                     {
                         AppPaths.InstalledVaultPath,
                         AppPaths.InstalledVaultPath + AppPaths.BackupSuffix,
                         AppPaths.InstalledVaultPath + AppPaths.TempSuffix
                     })
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            await Dialogs.MessageAsync(this, "Could not erase the vault", ex.Message, isError: true);
            return;
        }

        StartInstall();
    }

    private void BtnInstall_Click(object? sender, RoutedEventArgs e) => StartInstall();

    private void StartInstall()
    {
        PnlSelection.IsVisible = false;
        PnlProgress.IsVisible = true;

        _ = Task.Run(() =>
        {
            try
            {
                InstallerService.InstallWithProgress(Log);
                Dispatcher.UIThread.Post(() =>
                {
                    ProgInstall.IsIndeterminate = false;
                    ProgInstall.Value = 100;
                    BtnFinish.IsVisible = true;
                    Log("Installation completed successfully.");
                });
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    ProgInstall.IsIndeterminate = false;
                    ProgInstall.Foreground = Avalonia.Media.Brushes.Red;
                    Log("ERROR: " + ex.Message);
                });
            }
        });
    }

    private void BtnFinish_Click(object? sender, RoutedEventArgs e) => InstallerService.LaunchInstalledAndExit();

    private void Log(string message) => Dispatcher.UIThread.Post(() =>
    {
        _logText += $"[{DateTime.Now:HH:mm:ss}] {message}\n";
        TxtLog.Text = _logText;
        ScrollLog.ScrollToEnd();
    });
}
