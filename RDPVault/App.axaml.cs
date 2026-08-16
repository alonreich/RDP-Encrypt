using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace RDPVault;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            string[] args = desktop.Args ?? Array.Empty<string>();

            if (args.Any(a => string.Equals(a, "--uninstall", StringComparison.OrdinalIgnoreCase)))
            {
                desktop.MainWindow = new UninstallWindow();
            }
            else if (InstallerService.IsInstalledLocation() || System.IO.File.Exists(AppPaths.VaultPath))
            {
                // ISSUE #5.
                // The old test was File.Exists(BaseDirectory + "vault.dat") - a file
                // this app never creates. The real vault is vault.rdpv, so an exe
                // sitting next to a perfectly good vault (the portable / USB case,
                // which is the app's headline scenario) always showed the installer
                // instead of the vault.
                desktop.MainWindow = new MainWindow();
            }
            else
            {
                desktop.MainWindow = new SetupWindow();
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
