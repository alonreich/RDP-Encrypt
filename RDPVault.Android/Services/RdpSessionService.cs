using System;
using Android.App;
using Android.Content;
using Android.OS;
using AndroidX.Core.App;
using RDPVault;
using RDPVault.Android.Activities;
using RDPVault.Android.Rdp;

namespace RDPVault.Android.Services;

/// <summary>
/// Foreground Service tracking the embedded FreeRDP session.
/// Listens to native FreeRDP session callbacks (CONNECTED, DISCONNECTED, CONNECTION_FAILED)
/// and maintains the active connection notification with an explicit Disconnect action.
/// </summary>
[Service(Name = "com.rdpvault.app.services.RdpSessionService", Enabled = true, Exported = false, ForegroundServiceType = global::Android.Content.PM.ForegroundService.TypeConnectedDevice)]
public class RdpSessionService : Service
{
    public const string ActionStartSession = "com.rdpvault.action.START_SESSION";
    public const string ActionEndSession = "com.rdpvault.action.END_SESSION";
    public const string ActionResumeRemoteDesktop = "com.rdpvault.action.RESUME_RDP";

    public const string ExtraProfileName = "com.rdpvault.extra.PROFILE_NAME";
    public const string ExtraProfileHost = "com.rdpvault.extra.PROFILE_HOST";
    public const string ExtraProfilePort = "com.rdpvault.extra.PROFILE_PORT";
    public const string ExtraProfileGatewayHost = "com.rdpvault.extra.PROFILE_GATEWAY_HOST";
    public const string ExtraProfileGatewayPort = "com.rdpvault.extra.PROFILE_GATEWAY_PORT";

    public const string ChannelId = "rdpvault_active_session";
    public const int NotificationId = 1001;

    private readonly IBinder _binder;
    private RdpProfile? _activeProfile;
    private string _sessionName = "";
    private string _sessionHost = "";
    private int _sessionPort = 3389;
    private bool _isConnected;
    private bool _isStopping;
    private FreeRdpSession? _activeSession;

    public RdpSessionService()
    {
        _binder = new LocalBinder(this);
    }

    public class LocalBinder : Binder
    {
        public RdpSessionService Service { get; }
        public LocalBinder(RdpSessionService service) => Service = service;
    }

    public override IBinder OnBind(Intent? intent) => _binder;

    public override void OnCreate()
    {
        base.OnCreate();
        CreateNotificationChannel();
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        string? action = intent?.Action;

        if (action == ActionEndSession)
        {
            EndSession();
            MainActivity.Instance?.OnSessionEndedFromNotification();
            return StartCommandResult.NotSticky;
        }

        if (action == ActionResumeRemoteDesktop)
        {
            RdpLauncher.ResumeRemoteDesktop(this);
            return StartCommandResult.Sticky;
        }

        if (action == ActionStartSession)
        {
            _sessionName = intent?.GetStringExtra(ExtraProfileName) ?? "Remote PC";
            _sessionHost = intent?.GetStringExtra(ExtraProfileHost) ?? "";
            _sessionPort = intent?.GetIntExtra(ExtraProfilePort, 3389) ?? 3389;
            BeginTracking();
            return StartCommandResult.Sticky;
        }

        if (_isConnected)
        {
            StartForeground(NotificationId, BuildNotification());
        }

        return StartCommandResult.Sticky;
    }

    public void StartSession(RdpProfile profile)
    {
        _activeProfile = profile;
        _sessionName = profile.Name;
        _sessionHost = profile.Host;
        _sessionPort = profile.Port > 0 ? profile.Port : 3389;
        BeginTracking();
    }

    private void BeginTracking()
    {
        _isStopping = false;
        _isConnected = true;

        HookNativeSession();
        StartForeground(NotificationId, BuildNotification());
    }

    private void HookNativeSession()
    {
        _activeSession = RdpSessionBridge.ActiveSession;
        if (_activeSession != null)
        {
            _activeSession.Connected += OnNativeConnected;
            _activeSession.Disconnected += OnNativeDisconnected;
            _activeSession.ConnectionFailed += OnNativeConnectionFailed;
        }
    }

    private void OnNativeConnected()
    {
        _isConnected = true;
        SafeUpdateNotification();
    }

    private void OnNativeDisconnected()
    {
        EndSession();
    }

    private void OnNativeConnectionFailed(string reason)
    {
        EndSession();
    }

    public void EndSession()
    {
        if (_isStopping) return;
        _isStopping = true;

        _isConnected = false;
        _activeProfile = null;
        _sessionName = "";
        _sessionHost = "";

        if (_activeSession != null)
        {
            _activeSession.Connected -= OnNativeConnected;
            _activeSession.Disconnected -= OnNativeDisconnected;
            _activeSession.ConnectionFailed -= OnNativeConnectionFailed;
            try { _activeSession.Disconnect(); } catch { }
            _activeSession = null;
        }

        try
        {
            StopForeground(StopForegroundFlags.Remove);
            StopSelf();
        }
        catch { }
    }

    public bool IsConnected => _isConnected;
    public bool HostUnreachable => false;
    public RdpProfile? ActiveProfile => _activeProfile;
    public string SessionName => _sessionName;
    public string SessionHost => _sessionHost;
    public int SessionPort => _sessionPort;

    private void SafeUpdateNotification()
    {
        try
        {
            if (!_isConnected) return;
            var manager = NotificationManagerCompat.From(this);
            manager?.Notify(NotificationId, BuildNotification());
        }
        catch { }

        try
        {
            MainActivity.Instance?.NotifySessionStateChanged();
        }
        catch { }
    }

    private Notification BuildNotification()
    {
        var launchIntent = new Intent(this, typeof(RdpSessionActivity));
        launchIntent.AddFlags(ActivityFlags.SingleTop);
        var pendingIntent = PendingIntent.GetActivity(
            this,
            0,
            launchIntent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

        var endIntent = new Intent(this, typeof(RdpSessionService));
        endIntent.SetAction(ActionEndSession);
        var endPendingIntent = PendingIntent.GetService(
            this,
            2,
            endIntent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

        string target = string.IsNullOrWhiteSpace(_sessionHost)
            ? _sessionName
            : $"{_sessionName} ({_sessionHost}:{_sessionPort})";

        string statusText = $"Connected to {target}";

        var builder = new NotificationCompat.Builder(this, ChannelId);
        builder.SetContentTitle("Remote Desktop Session Active");
        builder.SetContentText(statusText);
        builder.SetStyle(new NotificationCompat.BigTextStyle().BigText(statusText));
        builder.SetSmallIcon(Resource.Drawable.ic_stat_vault);
        builder.SetOngoing(true);
        builder.SetOnlyAlertOnce(true);
        builder.SetPriority(NotificationCompat.PriorityLow);
        builder.SetCategory(NotificationCompat.CategoryService);
        builder.SetVisibility(NotificationCompat.VisibilitySecret);

        if (pendingIntent != null)
        {
            builder.SetContentIntent(pendingIntent);
        }

        builder.AddAction(Resource.Drawable.ic_stat_vault, "Open Session", pendingIntent);
        builder.AddAction(Resource.Drawable.ic_stat_vault, "Disconnect", endPendingIntent);

        var notification = builder.Build();
        return notification ?? new Notification();
    }

    private void CreateNotificationChannel()
    {
        try
        {
            if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
            {
                var channel = new NotificationChannel(
                    ChannelId,
                    "Remote Desktop Session",
                    NotificationImportance.Low)
                {
                    Description = "Active Remote Desktop connection notification and quick return to session."
                };
                channel.SetShowBadge(false);
                channel.LockscreenVisibility = NotificationVisibility.Secret;

                var manager = (NotificationManager?)GetSystemService(NotificationService);
                manager?.CreateNotificationChannel(channel);
            }
        }
        catch { }
    }

    public override void OnDestroy()
    {
        _isConnected = false;
        if (_activeSession != null)
        {
            _activeSession.Connected -= OnNativeConnected;
            _activeSession.Disconnected -= OnNativeDisconnected;
            _activeSession.ConnectionFailed -= OnNativeConnectionFailed;
            _activeSession = null;
        }
        base.OnDestroy();
    }
}
