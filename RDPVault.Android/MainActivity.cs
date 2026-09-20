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

    private static readonly System.Reflection.FieldInfo? ViewField =
        typeof(AvaloniaActivity).GetField("_view", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);

    private static readonly System.Reflection.MethodInfo? OnVisibilityChangedMethod =
        ViewField?.FieldType.GetMethod("OnVisibilityChanged", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public, null, new[] { typeof(bool) }, null);

    protected override void OnPause()
    {
        base.OnPause();
        try
        {
            // Explicitly notify Avalonia view that it is no longer visible so the render timer unsubscribes cleanly
            var view = ViewField?.GetValue(this);
            if (view != null && OnVisibilityChangedMethod != null)
            {
                OnVisibilityChangedMethod.Invoke(view, new object[] { false });
            }
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("RDPVault", "OnPause visibility notify skipped: " + ex.Message);
        }
    }

    protected override void OnResume()
    {
        base.OnResume();
        try
        {
            // 1. Force the native window background to dark theme color to prevent any white canvas exposure
            Window?.SetBackgroundDrawable(new global::Android.Graphics.Drawables.ColorDrawable(global::Android.Graphics.Color.ParseColor("#0E0E10")));

            // 2. Explicitly notify Avalonia view of visibility so render timer cleanly resubscribes and StartRendering() is triggered
            var view = ViewField?.GetValue(this);
            if (view != null && OnVisibilityChangedMethod != null)
            {
                OnVisibilityChangedMethod.Invoke(view, new object[] { true });
            }

            // 3. Request immediate layout pass and invalidate hardware surface
            if (view is global::Android.Views.View androidView)
            {
                androidView.RequestLayout();
                androidView.Invalidate();
            }

            // 4. Force visual tree invalidation on Avalonia dispatcher
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.ISingleViewApplicationLifetime singleView)
                    {
                        singleView.MainView?.InvalidateVisual();
                    }
                }
                catch { }
            });
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("RDPVault", "OnResume visibility restore skipped: " + ex.Message);
        }
    }

    public override void OnBackPressed()
    {
        // Preserve activity in background instead of destroying process on physical back press
        MoveTaskToBack(true);
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
