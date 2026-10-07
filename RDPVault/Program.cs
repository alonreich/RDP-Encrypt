using Avalonia;
using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;

namespace RDPVault;

internal static class Program
{

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    private static Mutex? _singleInstanceMutex;
    private static bool _ownsMutex;

    [STAThread]
    public static void Main(string[] args)
    {
        try
        {
            bool isSetupOrMaintenance =
                args.Any(a => string.Equals(a, "--install", StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(a, "--setup", StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(a, "--upgrade", StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(a, "--uninstall", StringComparison.OrdinalIgnoreCase));

            if (isSetupOrMaintenance)
            {
                if (!InstallerService.IsAdministrator() &&
                    !args.Any(a => string.Equals(a, "--no-elevate", StringComparison.OrdinalIgnoreCase)))
                {
                    if (InstallerService.TryElevate(args))
                        return;
                }

                InstallerService.KillRunningInstances(excludeCurrent: true);
            }

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
                    return;
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
                string value = args[i + 1].Trim().Trim('"', '\'');
                string? id = null;
                if (File.Exists(value))
                {
                    try
                    {
                        string content = File.ReadAllText(value).Trim();
                        const string prefix = "TargetProfileId=";
                        if (content.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                            id = content.Substring(prefix.Length).Trim().Trim('"', '\'');
                        else
                            id = content;
                    }
                    catch { }
                }
                else
                {
                    id = value;
                }

                if (!string.IsNullOrWhiteSpace(id))
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
