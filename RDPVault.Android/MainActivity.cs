using System;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Content.Res;
using Android.OS;
using Android.Views;
using AndroidX.Biometric;
using AndroidX.Core.Content;
using Avalonia;
using Avalonia.Android;
using RDPVault;
using RDPVault.Android.Platform;
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
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode | ConfigChanges.SmallestScreenSize | ConfigChanges.ScreenLayout | ConfigChanges.Density | ConfigChanges.FontScale)]
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

    /// <summary>Inactivity timeout in minutes. 0 = never.</summary>
    public int ConfiguredLockMinutes { get; set; } = 5;

    /// <summary>
    /// When true the vault is locked the moment the app leaves the screen, and the
    /// physical Back button on the connections list locks instead of just minimising.
    /// </summary>
    public bool LockImmediatelyOnBackground { get; set; } = true;

    /// <summary>
    /// Set immediately before RDP Vault deliberately launches another app (Remote Desktop,
    /// Play Store, the file picker, the share sheet, Android Settings). Without this the
    /// "lock the instant we leave the screen" rule would slam the vault shut every time the
    /// user opens a file picker and lose their place.
    /// </summary>
    private DateTime _externalActivitySuppressUntilUtc = DateTime.MinValue;

    private DateTime _lastBackgroundedUtc = DateTime.MinValue;

    public void BeginExternalActivity(int suppressSeconds = 120)
    {
        _externalActivitySuppressUntilUtc = DateTime.UtcNow.AddSeconds(suppressSeconds);
    }

    private bool IsExternalActivitySuppressed => DateTime.UtcNow < _externalActivitySuppressUntilUtc;

    public void RequestNotificationPermissionIfNeeded()
    {
        try
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(33))
            {
                if (CheckSelfPermission(global::Android.Manifest.Permission.PostNotifications) != Permission.Granted)
                {
                    RequestPermissions(new[] { global::Android.Manifest.Permission.PostNotifications }, 1002);
                }
            }
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("RDPVault", "Notification permission request: " + ex.Message);
        }
    }

    /// <summary>
    /// FLAG_SECURE. Blanks the app-switcher thumbnail and blocks screenshots and screen
    /// recording of the vault (connection list, master password field, recovery code).
    /// Opt-out lives in Settings for users who genuinely need to screen-record.
    /// </summary>
    public void ApplyScreenSecurity()
    {
        try
        {
            bool allowScreenshots = AppPrefs.GetBool(AppPrefs.KeyAllowScreenshots, false);
            if (allowScreenshots)
            {
                Window?.ClearFlags(WindowManagerFlags.Secure);
            }
            else
            {
                Window?.SetFlags(WindowManagerFlags.Secure, WindowManagerFlags.Secure);
            }
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("RDPVault", "FLAG_SECURE apply failed: " + ex.Message);
        }
    }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        Instance = this;
        base.OnCreate(savedInstanceState);

        ApplyScreenSecurity();
        RequestNotificationPermissionIfNeeded();

        try
        {
            var serviceIntent = new Intent(this, typeof(RdpSessionService));
            _serviceConnection = new ServiceConnection(this);
            BindService(serviceIntent, _serviceConnection, Bind.AutoCreate);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("RDPVault", "Background service bind skipped: " + ex.Message);
        }
    }

    private Views.MainView? ResolveMainView()
    {
        try
        {
            if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.ISingleViewApplicationLifetime singleView
                && singleView.MainView is Views.MainView mainView)
            {
                return mainView;
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Every touch, key press and trackball event in the app funnels through here.
    /// This is what makes the FOREGROUND inactivity auto-lock real: previously the timer
    /// only ever ran while the app was in the background, so a phone left unlocked on a
    /// desk with RDP Vault open never locked at all.
    /// </summary>
    public override void OnUserInteraction()
    {
        base.OnUserInteraction();
        try { ResolveMainView()?.NotifyUserActivity(); } catch { }
    }

    protected override void OnPause()
    {
        base.OnPause();
        _lastBackgroundedUtc = DateTime.UtcNow;

        try
        {
            ResolveMainView()?.SuspendIdleTimer();

            if (LockImmediatelyOnBackground && !IsExternalActivitySuppressed && !IsSessionActive)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    try { ResolveMainView()?.AutoLockIfUnlocked(); } catch { }
                });
            }
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("RDPVault", "OnPause lock evaluation: " + ex.Message);
        }
    }

    protected override void OnResume()
    {
        base.OnResume();
        try
        {
            RequestedOrientation = ScreenOrientation.Unspecified;

            ApplyScreenSecurity();

            Window?.SetBackgroundDrawable(new global::Android.Graphics.Drawables.ColorDrawable(global::Android.Graphics.Color.ParseColor("#0E0E10")));

            bool shouldLock = false;

            if (_lastBackgroundedUtc != DateTime.MinValue)
            {
                TimeSpan elapsed = DateTime.UtcNow - _lastBackgroundedUtc;
                if (ConfiguredLockMinutes > 0 && elapsed.TotalMinutes >= ConfiguredLockMinutes)
                {
                    shouldLock = true;
                }
            }
            _lastBackgroundedUtc = DateTime.MinValue;
            _externalActivitySuppressUntilUtc = DateTime.MinValue;

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    var mainView = ResolveMainView();
                    if (mainView == null) return;

                    if (shouldLock)
                    {
                        mainView.AutoLockIfUnlocked();
                    }

                    mainView.OnAppResumed();
                    mainView.CheckAndWipeExpiredClipboard();
                    mainView.ResumeIdleTimer();
                    mainView.InvalidateVisual();
                }
                catch { }
            });

            Task.Delay(60).ContinueWith(_ =>
            {
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
            });
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("RDPVault", "OnResume lifecycle restore: " + ex.Message);
        }
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        Intent = intent;
        if (intent?.Action == RdpSessionService.ActionEndSession)
        {
            EndForegroundSession();
            OnSessionEndedFromNotification();
        }
    }

    /// <summary>
    /// Re-syncs the in-app session banner (e.g. after the reachability watchdog flips the
    /// remote host to "not responding"). Does NOT clear the banner.
    /// </summary>
    public void NotifySessionStateChanged()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            try { ResolveMainView()?.OnAppResumed(); } catch { }
        });
    }

    public void OnSessionEndedFromNotification()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            try { ResolveMainView()?.OnSessionEnded(); } catch { }
        });
    }

    public override void OnBackPressed()
    {
        try
        {
            var mainView = ResolveMainView();
            if (mainView != null && mainView.HandleBackPressed())
            {
                return;
            }

            if (LockImmediatelyOnBackground && !IsSessionActive)
            {
                mainView?.AutoLockIfUnlocked();
            }
        }
        catch { }

        MoveTaskToBack(true);
    }

    /// <summary>
    /// Orientation changes are handled IN-PLACE. The Activity is never destroyed, so an
    /// external remote session and the unlocked vault both survive a screen flip with no
    /// re-authentication prompt.
    /// </summary>
    public override void OnConfigurationChanged(Configuration newConfig)
    {
        base.OnConfigurationChanged(newConfig);
        try
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                try { ResolveMainView()?.InvalidateVisual(); } catch { }
            });
        }
        catch { }
    }

    public bool IsSessionActive => _sessionService?.IsConnected == true || Rdp.RdpSessionBridge.ActiveSession != null || Rdp.RdpSessionBridge.HasPendingConfig;
    public RdpProfile? ActiveSessionProfile => _sessionService?.ActiveProfile;
    public bool ActiveSessionHostUnreachable => _sessionService?.HostUnreachable == true;

    public void StartForegroundSession(RdpProfile profile)
    {
        try
        {
            RequestNotificationPermissionIfNeeded();
            if (_sessionService != null)
            {
                _sessionService.StartSession(profile);
            }
            else
            {
                var intent = new Intent(this, typeof(RdpSessionService));
                intent.SetAction(RdpSessionService.ActionStartSession);
                intent.PutExtra(RdpSessionService.ExtraProfileName, profile.Name);
                intent.PutExtra(RdpSessionService.ExtraProfileHost, profile.Host);
                intent.PutExtra(RdpSessionService.ExtraProfilePort, profile.Port);
                if (!string.IsNullOrWhiteSpace(profile.GatewayHost) &&
                    ConnectionEndpoint.TryParseGatewayAuthority(profile.GatewayHost, out var gwEp, out _))
                {
                    intent.PutExtra(RdpSessionService.ExtraProfileGatewayHost, gwEp.Host);
                    intent.PutExtra(RdpSessionService.ExtraProfileGatewayPort, gwEp.Port);
                }
                if (OperatingSystem.IsAndroidVersionAtLeast(26))
                {
                    StartForegroundService(intent);
                }
                else
                {
                    StartService(intent);
                }
            }
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

    public void StopForegroundSession() => EndForegroundSession();

    protected override void OnDestroy()
    {
        if (Instance == this) Instance = null;
        if (_serviceBound && _serviceConnection != null)
        {
            try { UnbindService(_serviceConnection); } catch { }
            _serviceBound = false;
        }
        base.OnDestroy();
    }
}
