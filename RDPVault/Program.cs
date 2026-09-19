using Avalonia;
using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;

namespace RDPVault;

internal static class Program
{
    // Don't use Avalonia, third-party APIs or any SynchronizationContext-reliant
    // code before AppMain is called: things aren't initialized yet.

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    private static Mutex? _singleInstanceMutex;
    private static bool _ownsMutex;

    [STAThread]
    public static void Main(string[] args)
    {
        try
        {
            // If running in installer, setup, upgrade, or maintenance mode, kill any running
            // instances first to release file locks on the binaries and release the named mutex.
            bool isSetupOrMaintenance =
                args.Any(a => string.Equals(a, "--install", StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(a, "--setup", StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(a, "--upgrade", StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(a, "--uninstall", StringComparison.OrdinalIgnoreCase));

            if (isSetupOrMaintenance)
            {
                // Auto-elevate via UAC if running as standard user in setup/maintenance mode
                if (!InstallerService.IsAdministrator() &&
                    !args.Any(a => string.Equals(a, "--no-elevate", StringComparison.OrdinalIgnoreCase)))
                {
                    if (InstallerService.TryElevate(args))
                        return; // Successfully spawned elevated instance; current un-elevated caller exits
                }

                InstallerService.KillRunningInstances(excludeCurrent: true);
            }

            // Verify single-instance at the primary entry point before Avalonia initialization.
            _singleInstanceMutex = new Mutex(false, @"Local\RDPVault_SingleInstance");
            try
            {
                _ownsMutex = _singleInstanceMutex.WaitOne(50, false);
            }
            catch (AbandonedMutexException)
            {
                _ownsMutex = true;
            }

            if (!_ownsMutex)
            {
                if (isSetupOrMaintenance)
                {
                    InstallerService.KillRunningInstances(excludeCurrent: true);
                    try
                    {
                        _ownsMutex = _singleInstanceMutex.WaitOne(300, false);
                    }
                    catch (AbandonedMutexException)
                    {
                        _ownsMutex = true;
                    }
                }

                if (!_ownsMutex)
                {
                    string pipeMsg = FormatPipeMessage(args);
                    try
                    {
                        using var client = new NamedPipeClientStream(".", "RDPVault_Show", PipeDirection.Out);
                        client.Connect(500);
                        using var w = new StreamWriter(client) { AutoFlush = true };
                        w.Write(pipeMsg);
                    }
                    catch { }
                    return; // Clean exit without initializing UI framework
                }
            }

            BuildAvaloniaApp().StartWithClassicDesktopLifetime(
                args, Avalonia.Controls.ShutdownMode.OnLastWindowClose);
        }
        catch (Exception ex)
        {
            MessageBoxW(IntPtr.Zero,
                "A fatal error occurred and RDP Vault must close:\n\n" + ex,
                "RDP Vault - Fatal Error", 0x10);
        }
        finally
        {
            if (_singleInstanceMutex != null)
            {
                if (_ownsMutex)
                {
                    try { _singleInstanceMutex.ReleaseMutex(); } catch { }
                }
                try { _singleInstanceMutex.Dispose(); } catch { }
                _singleInstanceMutex = null;
            }
        }
    }

    private static string FormatPipeMessage(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--launch", StringComparison.OrdinalIgnoreCase))
            {
                string value = args[i + 1].Trim();
                string? id = null;
                if (File.Exists(value))
                {
                    try
                    {
                        string content = File.ReadAllText(value).Trim();
                        const string prefix = "TargetProfileId=";
                        if (content.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                            id = content.Substring(prefix.Length).Trim();
                    }
                    catch { }
                }
                else if (Guid.TryParse(value, out Guid g))
                {
                    id = g.ToString("N");
                }

                if (!string.IsNullOrEmpty(id))
                    return "LAUNCH:" + id;
            }
        }
        return "SHOW";
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
