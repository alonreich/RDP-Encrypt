using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace RDPVault;

public partial class UninstallWindow : Window
{
    private string _logText = "";
    private readonly bool _quiet;

    public UninstallWindow()
    {
        InitializeComponent();

        _quiet = Environment.GetCommandLineArgs()
                            .Any(a => string.Equals(a, "--quiet", StringComparison.OrdinalIgnoreCase));

        bool hasVault = InstallerService.InstalledVaultExists();

        TxtVaultNotice.Text = hasVault
            ? "A vault with your saved connections is stored in the installation folder. Choose what should happen to it."
            : "No vault was found in the installation folder. Nothing of yours will be deleted.";
        TxtKeepHint.Text = hasVault
            ? $"Your vault will be copied to {AppPaths.RescueDir} before the program is removed"
            : "Removes the program, its shortcuts and its registry entries";
        BtnErase.IsVisible = hasVault;

        Loaded += (_, _) =>
        {
            // ISSUE #1: the quiet uninstall path (QuietUninstallString, used by some
            // management tools) previously wiped the vault with no UI at all. Quiet
            // now means quiet AND non-destructive: the vault is always rescued.
            if (_quiet) Start(keepVault: true);
        };
    }

    private void BtnKeep_Click(object? sender, RoutedEventArgs e) => Start(keepVault: true);

    private async void BtnErase_Click(object? sender, RoutedEventArgs e)
    {
        bool go = await Dialogs.ConfirmAsync(this, "Erase the vault?",
            "Every profile and saved password will be overwritten and deleted, along with the automatic backup. " +
            "Nobody can bring it back. Type ERASE to confirm.",
            confirmText: "Erase everything", danger: true, typeToConfirm: "ERASE");
        if (!go) return;
        Start(keepVault: false);
    }

    private void BtnCancel_Click(object? sender, RoutedEventArgs e) => Environment.Exit(0);

    private void BtnClose_Click(object? sender, RoutedEventArgs e) => Environment.Exit(0);

    private void Start(bool keepVault)
    {
        PnlChoice.IsVisible = false;
        PnlProgress.IsVisible = true;

        _ = Task.Run(() =>
        {
            string? rescued = null;
            try
            {
                rescued = InstallerService.UninstallWithProgress(Log, keepVault);
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

            Dispatcher.UIThread.Post(async () =>
            {
                ProgInstall.IsIndeterminate = false;
                ProgInstall.Value = 100;
                BtnClose.IsVisible = true;

                if (_quiet) { Environment.Exit(0); return; }

                if (rescued != null)
                {
                    await Dialogs.MessageAsync(this, "Your vault was kept",
                        $"RDP Vault has been removed. Your vault file was copied to:\n\n{rescued}\n\n" +
                        "It still needs your master password or Recovery Code to open.");
                }
                Environment.Exit(0);
            });
        });
    }

    private void Log(string message) => Dispatcher.UIThread.Post(() =>
    {
        _logText += $"[{DateTime.Now:HH:mm:ss}] {message}\n";
        TxtLog.Text = _logText;
        ScrollLog.ScrollToEnd();
    });
}
