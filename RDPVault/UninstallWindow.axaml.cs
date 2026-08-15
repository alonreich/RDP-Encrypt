using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using System;
using System.Threading.Tasks;

namespace RDPVault;

public partial class UninstallWindow : Window
{
    private string _logText = "";

    public UninstallWindow()
    {
        InitializeComponent();
        this.Loaded += (s, e) => StartUninstall();
    }

    private void Log(string message)
    {
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            _logText += $"[{DateTime.Now:HH:mm:ss}] {message}\n";
            TxtLog.Text = _logText;
            ScrollLog.ScrollToEnd();
        });
    }

    private async void StartUninstall()
    {
        await Task.Run(() =>
        {
            try
            {
                InstallerService.UninstallWithProgress(Log);
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.InvokeAsync(() =>
                {
                    ProgInstall.IsIndeterminate = false;
                    ProgInstall.Foreground = Avalonia.Media.Brushes.Red;
                    Log("ERROR: " + ex.Message);
                });
            }
        });
    }
}
