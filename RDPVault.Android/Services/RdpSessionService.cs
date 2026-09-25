using System;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.OS;
using AndroidX.Core.App;
using RDPVault;
using RDPVault.Android.Net;

namespace RDPVault.Android.Services;

/// <summary>
/// Foreground Service that tracks a hand-off to an external RDP client.
///
/// HONESTY CONTRACT (audited 2026-09-21, Finding 2):
/// RDP Vault does NOT own the remote session. It hands the connection to Microsoft Remote
/// Desktop (or aRDP) through an Android Intent and has no protocol-level visibility into
/// that session. Therefore this service must never claim "connected" as a fact.
/// What it CAN establish is whether the target host:port still answers a TCP connect, and
/// that is exactly what it reports - nothing more. Every user-facing string here is
/// phrased as "handed off" / "responding" / "not responding", never "connected".
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

    private const int ProbeIntervalMs = 20000;
    private const int ProbeTimeoutMs = 2500;
    private const int FailuresBeforeUnreachable = 2;

    private readonly IBinder _binder;
    private RdpProfile? _activeProfile;
    private string _sessionName = "";
    private string _sessionHost = "";
    private int _sessionPort = 3389;
    private string _gatewayHost = "";
    private int _gatewayPort = 443;
    private bool _isHandedOff;
    private bool _hostUnreachable;
    private bool _isStopping;
    private CancellationTokenSource? _probeCts;

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
            Rdp.RdpLauncher.ResumeRemoteDesktop(this);
            return StartCommandResult.Sticky;
        }

        if (action == ActionStartSession)
        {
            // Cold-start path: the Activity called StartForegroundService before the bind
            // completed. Android requires StartForeground within ~5s of that call.
            _sessionName = intent?.GetStringExtra(ExtraProfileName) ?? "Remote PC";
            _sessionHost = intent?.GetStringExtra(ExtraProfileHost) ?? "";
            _sessionPort = intent?.GetIntExtra(ExtraProfilePort, 3389) ?? 3389;
            _gatewayHost = intent?.GetStringExtra(ExtraProfileGatewayHost) ?? "";
            _gatewayPort = intent?.GetIntExtra(ExtraProfileGatewayPort, 443) ?? 443;
            BeginTracking();
            return StartCommandResult.Sticky;
        }

        if (_isHandedOff)
        {
            // Restarted by the OS while a hand-off was being tracked: re-post the
            // notification immediately so we never sit in the foreground without one.
            StartForeground(NotificationId, BuildNotification());
        }

        return StartCommandResult.Sticky;
    }

    /// <summary>
    /// Records that the session was handed off to the external RDP client and starts the
    /// reachability watchdog. Called by MainActivity once the Intent has been dispatched.
    /// </summary>
    public void StartSession(RdpProfile profile)
    {
        _activeProfile = profile;
        _sessionName = profile.Name;
        _sessionHost = profile.Host;
        _sessionPort = profile.Port > 0 ? profile.Port : 3389;
        if (!string.IsNullOrWhiteSpace(profile.GatewayHost) &&
            ConnectionEndpoint.TryParseGatewayAuthority(profile.GatewayHost, out var gwEp, out _))
        {
            _gatewayHost = gwEp.Host;
            _gatewayPort = gwEp.Port;
        }
        else
        {
            _gatewayHost = "";
            _gatewayPort = 443;
        }
        BeginTracking();
    }

    private void BeginTracking()
    {
        _isStopping = false;
        _isHandedOff = true;
        _hostUnreachable = false;

        StartForeground(NotificationId, BuildNotification());
        StartReachabilityWatchdog();
    }

    /// <summary>
    /// Stops tracking the hand-off and removes the notification.
    ///
    /// This does NOT and CANNOT disconnect Microsoft Remote Desktop: Android app sandboxing
    /// forbids one app from terminating another app's session. The UI must therefore never
    /// present this as "the remote session was terminated".
    /// </summary>
    public void EndSession()
    {
        if (_isStopping) return;
        _isStopping = true;

        _isHandedOff = false;
        _hostUnreachable = false;
        _activeProfile = null;
        _sessionName = "";
        _sessionHost = "";

        try
        {
            _probeCts?.Cancel();
            _probeCts?.Dispose();
        }
        catch { }
        _probeCts = null;

        try
        {
            StopForeground(StopForegroundFlags.Remove);
            StopSelf();
        }
        catch { }
    }

    public bool IsConnected => _isHandedOff;
    public bool HostUnreachable => _hostUnreachable;
    public RdpProfile? ActiveProfile => _activeProfile;
    public string SessionName => _sessionName;
    public string SessionHost => _sessionHost;
    public int SessionPort => _sessionPort;

    /// <summary>
    /// Periodically TCP-probes the remote host. The only honest liveness signal available
    /// to an app that does not own the RDP socket. Two consecutive failures flip the
    /// notification and the in-app banner to "not responding" so the user finds out from
    /// RDP Vault instead of from a frozen Remote Desktop window.
    /// </summary>
    private void StartReachabilityWatchdog()
    {
        try { _probeCts?.Cancel(); } catch { }
        _probeCts?.Dispose();
        _probeCts = new CancellationTokenSource();
        var token = _probeCts.Token;

        string probeTarget;
        int probePort;

        if (!string.IsNullOrWhiteSpace(_gatewayHost))
        {
            probeTarget = _gatewayHost;
            probePort = _gatewayPort;
        }
        else if (_activeProfile != null && !string.IsNullOrWhiteSpace(_activeProfile.GatewayHost) &&
                 ConnectionEndpoint.TryParseGatewayAuthority(_activeProfile.GatewayHost, out var parsedGw, out _))
        {
            probeTarget = parsedGw.Host;
            probePort = parsedGw.Port;
        }
        else
        {
            if (ConnectionEndpoint.TryParse(_sessionHost, out var hostEp, out _, port: _sessionPort))
            {
                probeTarget = hostEp.Host;
                probePort = hostEp.Port;
            }
            else
            {
                probeTarget = _sessionHost;
                probePort = _sessionPort;
            }
        }

        if (string.IsNullOrWhiteSpace(probeTarget)) return;

        _ = Task.Run(async () =>
        {
            int consecutiveFailures = 0;

            while (!token.IsCancellationRequested && _isHandedOff)
            {
                try
                {
                    await Task.Delay(ProbeIntervalMs, token).ConfigureAwait(false);
                }
                catch (System.OperationCanceledException)
                {
                    // Fully qualified: `using Android.OS;` also brings an
                    // Android.OS.OperationCanceledException into scope.
                    return;
                }

                if (token.IsCancellationRequested || !_isHandedOff) return;

                bool reachable = await HostProbe.IsReachableAsync(probeTarget, probePort, ProbeTimeoutMs, token).ConfigureAwait(false);

                if (reachable)
                {
                    consecutiveFailures = 0;
                    if (_hostUnreachable)
                    {
                        _hostUnreachable = false;
                        SafeUpdateNotification();
                    }
                }
                else
                {
                    consecutiveFailures++;
                    if (consecutiveFailures >= FailuresBeforeUnreachable && !_hostUnreachable)
                    {
                        _hostUnreachable = true;
                        SafeUpdateNotification();
                    }
                }
            }
        }, token);
    }

    private void SafeUpdateNotification()
    {
        try
        {
            if (!_isHandedOff) return;
            var manager = NotificationManagerCompat.From(this);
            manager?.Notify(NotificationId, BuildNotification());
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("RDPVault", "Notification update failed: " + ex.Message);
        }

        try
        {
            // Refresh the in-app banner text (responding / not responding). This must NOT
            // clear the banner - the hand-off is still being tracked.
            MainActivity.Instance?.NotifySessionStateChanged();
        }
        catch { }
    }

    private Notification BuildNotification()
    {
        var launchIntent = new Intent(this, typeof(MainActivity));
        launchIntent.AddFlags(ActivityFlags.SingleTop);
        var pendingIntent = PendingIntent.GetActivity(
            this,
            0,
            launchIntent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

        PendingIntent? resumePendingIntent = null;
        var directResumeIntent = Rdp.RdpLauncher.CreateResumeIntent(this);
        if (directResumeIntent != null)
        {
            directResumeIntent.AddFlags(ActivityFlags.NewTask | ActivityFlags.ReorderToFront);
            resumePendingIntent = PendingIntent.GetActivity(
                this,
                1,
                directResumeIntent,
                PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
        }
        else
        {
            var fallbackIntent = new Intent(this, typeof(MainActivity));
            fallbackIntent.AddFlags(ActivityFlags.SingleTop);
            resumePendingIntent = PendingIntent.GetActivity(
                this,
                1,
                fallbackIntent,
                PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
        }

        var endIntent = new Intent(this, typeof(RdpSessionService));
        endIntent.SetAction(ActionEndSession);
        var endPendingIntent = PendingIntent.GetService(
            this,
            2,
            endIntent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

        string target = string.IsNullOrWhiteSpace(_sessionHost)
            ? _sessionName
            : $"{_sessionName} ({_sessionHost})";

        string statusText = _hostUnreachable
            ? $"{target} is not responding - it may be asleep or off the network."
            : $"Handed off to Remote Desktop - {target}";

        var builder = new NotificationCompat.Builder(this, ChannelId);
        builder.SetContentTitle(_hostUnreachable ? "Remote PC not responding" : "Remote Desktop session handed off");
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

        builder.AddAction(Resource.Drawable.ic_stat_vault, "Open Remote Desktop", resumePendingIntent);
        // "Stop tracking" - deliberately NOT called "End Session": tapping it cannot and
        // does not disconnect the remote session, it only clears RDP Vault's own banner.
        builder.AddAction(Resource.Drawable.ic_stat_vault, "Stop tracking", endPendingIntent);

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
                    "Remote Desktop hand-off",
                    NotificationImportance.Low)
                {
                    Description = "Quick return to a remote session and a warning if the remote PC stops responding."
                };
                channel.SetShowBadge(false);
                channel.LockscreenVisibility = NotificationVisibility.Secret;

                var manager = (NotificationManager?)GetSystemService(NotificationService);
                manager?.CreateNotificationChannel(channel);
            }
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("RDPVault", "Notification channel creation failed: " + ex.Message);
        }
    }

    public override void OnDestroy()
    {
        _isHandedOff = false;
        try
        {
            _probeCts?.Cancel();
            _probeCts?.Dispose();
        }
        catch { }
        _probeCts = null;
        base.OnDestroy();
    }
}
