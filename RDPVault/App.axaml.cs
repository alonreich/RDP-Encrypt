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
