using System;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.OS;
using AndroidX.Core.App;
using RDPVault;

namespace RDPVault.Android.Services;

/// <summary>
/// Foreground Service maintaining active RDP socket connections and in-memory session credentials.
/// By anchoring connections in a Foreground Service rather than an Activity, screen rotations
/// and temporary app backgrounding NEVER destroy the session or trigger re-authentication.
/// </summary>
[Service(Name = "com.rdpvault.app.services.RdpSessionService", Enabled = true, Exported = false, ForegroundServiceType = global::Android.Content.PM.ForegroundService.TypeRemoteMessaging)]
public class RdpSessionService : Service
{
    public const string ChannelId = "rdpvault_active_session";
    public const int NotificationId = 1001;

    private readonly IBinder _binder;
    private RdpProfile? _activeProfile;
    private bool _isConnected;
    private CancellationTokenSource? _sessionCts;

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
        return StartCommandResult.Sticky;
    }

    /// <summary>
    /// Starts an RDP session in the foreground service.
    /// </summary>
    public void StartSession(RdpProfile profile)
    {
        _activeProfile = profile;
        _isConnected = true;
        _sessionCts = new CancellationTokenSource();

        var notification = BuildNotification($"Connected to {profile.Name} ({profile.Host})");
        StartForeground(NotificationId, notification);
    }

    /// <summary>
    /// Gracefully ends the RDP session and releases foreground service priority.
    /// </summary>
    public void EndSession()
    {
        _isConnected = false;
        _sessionCts?.Cancel();
        _sessionCts?.Dispose();
        _sessionCts = null;
        _activeProfile = null;

        StopForeground(StopForegroundFlags.Remove);
        StopSelf();
    }

    public bool IsConnected => _isConnected;
    public RdpProfile? ActiveProfile => _activeProfile;

    private Notification BuildNotification(string statusText)
    {
        var launchIntent = PackageManager?.GetLaunchIntentForPackage(PackageName ?? "") ?? new Intent(this, typeof(MainActivity));
        var pendingIntent = PendingIntent.GetActivity(
            this,
            0,
            launchIntent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

        var builder = new NotificationCompat.Builder(this, ChannelId);
        builder.SetContentTitle("RDP Vault Active Session");
        builder.SetContentText(statusText);
        builder.SetSmallIcon(global::Android.Resource.Drawable.IcMenuShare);
        builder.SetOngoing(true);
        builder.SetPriority(NotificationCompat.PriorityHigh);

        if (pendingIntent != null)
        {
            builder.SetContentIntent(pendingIntent);
        }

        var notification = builder.Build();
        return notification ?? new Notification();
    }

    private void CreateNotificationChannel()
    {
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
        {
            var channel = new NotificationChannel(
                ChannelId,
                "Active RDP Sessions",
                NotificationImportance.High)
            {
                Description = "Maintains connection state across orientation changes"
            };

            var manager = (NotificationManager?)GetSystemService(NotificationService);
            manager?.CreateNotificationChannel(channel);
        }
    }

    public override void OnDestroy()
    {
        EndSession();
        base.OnDestroy();
    }
}
