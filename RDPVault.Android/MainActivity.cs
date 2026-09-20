using System;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Content.Res;
using Android.OS;
using AndroidX.Biometric;
using AndroidX.Core.Content;
using Avalonia;
using Avalonia.Android;
using RDPVault.Android.Services;

namespace RDPVault.Android;

[Activity(
    Name = "com.rdpvault.app.MainActivity",
    Label = "RDP Vault",
    Theme = "@style/MainTheme",
    Icon = "@mipmap/icon",
    RoundIcon = "@mipmap/icon",
    MainLauncher = true,
    Exported = true,
    LaunchMode = LaunchMode.SingleTask,
    WindowSoftInputMode = global::Android.Views.SoftInput.AdjustResize,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode | ConfigChanges.SmallestScreenSize | ConfigChanges.ScreenLayout | ConfigChanges.Density)]
public class MainActivity : AvaloniaMainActivity<App>
{
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        return base.CustomizeAppBuilder(builder)
            .WithInterFont()
            .With(new AndroidPlatformOptions
            {
                RenderingMode = new[] { AndroidRenderingMode.Egl, AndroidRenderingMode.Software }
            });
    }
    private RdpSessionService? _sessionService;
    private bool _serviceBound;
    private ServiceConnection? _serviceConnection;

    private class ServiceConnection : Java.Lang.Object, IServiceConnection
    {
        private readonly MainActivity _activity;
        public ServiceConnection(MainActivity activity) => _activity = activity;

        public void OnServiceConnected(ComponentName? name, IBinder? service)
        {
            if (service is RdpSessionService.LocalBinder binder)
            {
                _activity._sessionService = binder.Service;
                _activity._serviceBound = true;
            }
        }

        public void OnServiceDisconnected(ComponentName? name)
        {
            _activity._sessionService = null;
            _activity._serviceBound = false;
        }
    }

    public static MainActivity? Instance { get; private set; }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        Instance = this;
        base.OnCreate(savedInstanceState);

        try
        {
            // Bind to background session service
            var serviceIntent = new Intent(this, typeof(RdpSessionService));
            _serviceConnection = new ServiceConnection(this);
            BindService(serviceIntent, _serviceConnection, Bind.AutoCreate);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("RDPVault", "Background service bind skipped: " + ex.Message);
        }
    }

    /// <summary>
    /// Critical lifecycle override: Handles phone orientation changes IN-PLACE.
    /// Because the Activity is NOT destroyed, active RDP sessions continue uninterrupted
    /// and NO re-authentication prompt is displayed.
    /// </summary>
    public override void OnConfigurationChanged(Configuration newConfig)
    {
        base.OnConfigurationChanged(newConfig);

        // Orientation flipped (e.g. Portrait <-> Landscape)
        // Notify the viewport renderer to adjust aspect ratio or request dynamic display resize
        if (_sessionService != null && _sessionService.IsConnected)
        {
            // Active session maintained seamlessly without credential re-prompt
        }
    }

    public void StartForegroundSession(RdpProfile profile)
    {
        try
        {
            _sessionService?.StartSession(profile);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("RDPVault", "Failed to start foreground session: " + ex.Message);
        }
    }

    public void EndForegroundSession()
    {
        try
        {
            _sessionService?.EndSession();
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("RDPVault", "Failed to end foreground session: " + ex.Message);
        }
    }

    protected override void OnDestroy()
    {
        if (Instance == this) Instance = null;
        if (_serviceBound && _serviceConnection != null)
        {
            UnbindService(_serviceConnection);
            _serviceBound = false;
        }
        base.OnDestroy();
    }
}
