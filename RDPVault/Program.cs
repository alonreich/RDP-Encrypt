using Avalonia;
using System;

namespace RDPVault;

internal static class Program
{
    // Don't use Avalonia, third-party APIs or any SynchronizationContext-reliant
    // code before AppMain is called: things aren't initialized yet.

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    [STAThread]
    public static void Main(string[] args)
    {
        try
        {
            // Issue #5: the window to open is decided in one place (App.axaml.cs),
            // from one path constant. Program.cs used to make the same decision from
            // a *different* file name ("vault.dat"), and the two disagreed.
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(
                args, Avalonia.Controls.ShutdownMode.OnLastWindowClose);
        }
        catch (Exception ex)
        {
            MessageBoxW(IntPtr.Zero,
                "A fatal error occurred and RDP Vault must close:\n\n" + ex,
                "RDP Vault - Fatal Error", 0x10);
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
