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
            // Only offer the destructive option when there is actually something to destroy.
            BtnCleanInstall.IsVisible = InstallerService.InstalledVaultExists();
        }
    }

    private void BtnPortable_Click(object? sender, RoutedEventArgs e)
    {
        var main = new MainWindow();
        main.Show();
        Close();
    }

    /// <summary>
    /// ISSUE #5 (and #12).
    /// This button promised to delete "your existing vault", but it deleted
    /// InstallDir\vault.dat - a file this app has never created. The real vault is
    /// vault.rdpv, so nothing was erased: users pressed "Clean install", expected a
    /// fresh start, and were then asked for the old master password by the old vault.
    /// It also fired instantly with no confirmation.
    /// </summary>
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
