using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace RDPVault;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (desktop.Args != null && System.Linq.Enumerable.Contains(desktop.Args, "--uninstall"))
            {
                desktop.MainWindow = new UninstallWindow();
            }
            else
            {
                string vaultPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "vault.dat");
                if (!System.IO.File.Exists(vaultPath) && !InstallerService.IsInstalledLocation())
                {
                    desktop.MainWindow = new SetupWindow();
                }
                else
                {
                    desktop.MainWindow = new MainWindow();
                }
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}