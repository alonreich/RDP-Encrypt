using Avalonia;
using System;
using System.Linq;

namespace RDPVault;

class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    [STAThread]
    public static void Main(string[] args)
    {
        try
        {
            string vaultPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "vault.dat");
            if (!System.IO.File.Exists(vaultPath) && !InstallerService.IsInstalledLocation())
            {
                BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnMainWindowClose);
            }
            else
            {
                BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            }
        }
        catch (Exception ex)
        {
            MessageBoxW(IntPtr.Zero, "A fatal error occurred and the application must close:\n\n" + ex.ToString(), "RDP Vault - Fatal Error", 0x10);
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
