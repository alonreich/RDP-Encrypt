using Avalonia;
using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;

namespace RDPVault;

internal static class Program
{
    // Don't use Avalonia, third-party APIs or any SynchronizationContext-reliant
    // code before AppMain is called: things aren't initialized yet.

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    private static Mutex? _singleInstanceMutex;

    [STAThread]
    public static void Main(string[] args)
    {
        try
        {
            // Issue #5: Verify single-instance at the primary entry point before Avalonia initialization.
            _singleInstanceMutex = new Mutex(true, @"Local\RDPVault_SingleInstance", out bool createdNew);
            if (!createdNew)
            {
                string pipeMsg = FormatPipeMessage(args);
                try
                {
                    using var client = new NamedPipeClientStream(".", "RDPVault_Show", PipeDirection.Out);
                    client.Connect(1200);
                    using var w = new StreamWriter(client) { AutoFlush = true };
                    w.Write(pipeMsg);
                }
                catch { }
                return; // Clean exit without initializing UI framework
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
                try { _singleInstanceMutex.ReleaseMutex(); } catch { }
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
