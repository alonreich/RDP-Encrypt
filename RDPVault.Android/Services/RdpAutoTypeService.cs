using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Android.AccessibilityServices;
using Android.App;
using Android.Content;
using Android.OS;
using Android.Provider;
using Android.Text;
using Android.Views.Accessibility;

namespace RDPVault.Android.Services;

/// <summary>
/// Android Accessibility Service that automatically and securely injects remote desktop credentials
/// into Microsoft Remote Desktop without touching the system clipboard or storing credentials in external apps.
/// </summary>
[Service(
    Name = "com.rdpvault.app.services.RdpAutoTypeService",
    Permission = "android.permission.BIND_ACCESSIBILITY_SERVICE",
    Exported = true,
    Label = "@string/accessibility_service_label"
)]
[IntentFilter(new[] { "android.accessibilityservice.AccessibilityService" })]
[MetaData("android.accessibilityservice", Resource = "@xml/accessibility_service_config")]
public class RdpAutoTypeService : AccessibilityService
{
    public static RdpAutoTypeService? Instance { get; private set; }

    private static readonly object _lock = new();
    private static string? _armedHost;
    private static string? _armedUsername;
    private static string? _armedPassword;
    private static DateTime _armExpiry = DateTime.MinValue;
    private static System.Threading.Timer? _expiryTimer;
    private static System.Threading.Timer? _failureTimer;
    private static bool _hasInjected;

    /// <summary>
    /// Separate, low-importance channel for the "auto-type could not fill the password"
    /// notice. Kept off the session channel so silencing one does not silence the other.
    /// </summary>
    public const string FailureChannelId = "rdpvault_autotype_status";
    public const int FailureNotificationId = 1003;

    /// <summary>
    /// How long to wait after arming before concluding that node traversal failed.
    /// Microsoft Remote Desktop (and aRDP) are increasingly built with Jetpack Compose,
    /// whose view hierarchy does not always expose a classic EditText node - so the
    /// injector CAN legitimately find nothing. Telling the user beats silence.
    /// </summary>
    private const int FailureNoticeSeconds = 12;

    public override void OnCreate()
    {
        base.OnCreate();
        Instance = this;
    }

    protected override void OnServiceConnected()
    {
        base.OnServiceConnected();
        Instance = this;

        try
        {
            var info = ServiceInfo ?? new AccessibilityServiceInfo();
            info.EventTypes = EventTypes.WindowStateChanged | EventTypes.WindowContentChanged;
            info.FeedbackType = FeedbackFlags.Generic;
            info.Flags = AccessibilityServiceFlags.Default | AccessibilityServiceFlags.RetrieveInteractiveWindows;
            info.PackageNames = new[]
            {
                "com.microsoft.rdc.androidx",
                "com.microsoft.rdc.android",
                "com.iiordanov.freeaRDP",
                "com.iiordanov.aRDP"
            };
            info.NotificationTimeout = 50;
            SetServiceInfo(info);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("RDPVault", $"OnServiceConnected error: {ex.Message}");
        }
    }

    public override bool OnUnbind(Intent? intent)
    {
        Instance = null;
        Disarm();
        return base.OnUnbind(intent);
    }

    public override void OnInterrupt()
    {
    }

    /// <summary>
    /// Arms the auto-type injector with credentials for an upcoming connection launch.
    /// The password is held ephemerally in volatile memory for at most timeoutSeconds,
    /// and is wiped immediately upon injection or expiry.
    /// </summary>
    public static void Arm(string host, string username, string password, int timeoutSeconds = 30)
    {
        if (string.IsNullOrEmpty(password)) return;

        lock (_lock)
        {
            _armedHost = host;
            _armedUsername = username;
            _armedPassword = password;
            _armExpiry = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            _hasInjected = false;

            _expiryTimer?.Dispose();
            _expiryTimer = new System.Threading.Timer(_ => Disarm(), null, timeoutSeconds * 1000, System.Threading.Timeout.Infinite);

            _failureTimer?.Dispose();
            _failureTimer = new System.Threading.Timer(_ => ReportInjectionFailureIfStillArmed(), null,
                FailureNoticeSeconds * 1000, System.Threading.Timeout.Infinite);
        }
    }

    /// <summary>
    /// Fired ~12s after arming. If the credential is still sitting armed and un-injected,
    /// the password box was never found: surface a notification instead of leaving the user
    /// staring at an empty field wondering whether auto-type is broken or just slow.
    /// </summary>
    private static void ReportInjectionFailureIfStillArmed()
    {
        bool stillArmed;
        lock (_lock)
        {
            stillArmed = !_hasInjected && !string.IsNullOrEmpty(_armedPassword);
        }
        if (!stillArmed) return;

        try
        {
            var context = (Context?)MainActivity.Instance ?? global::Android.App.Application.Context;
            if (context == null) return;

            EnsureFailureChannel(context);

            var openIntent = new Intent(context, typeof(MainActivity));
            openIntent.AddFlags(ActivityFlags.SingleTop);
            var pending = PendingIntent.GetActivity(context, 7, openIntent,
                PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

            string body = context.GetString(Resource.String.autotype_failed_text);

            var builder = new AndroidX.Core.App.NotificationCompat.Builder(context, FailureChannelId);
            builder.SetContentTitle(context.GetString(Resource.String.autotype_failed_title));
            builder.SetContentText(body);
            builder.SetStyle(new AndroidX.Core.App.NotificationCompat.BigTextStyle().BigText(body));
            builder.SetSmallIcon(Resource.Drawable.ic_stat_vault);
            builder.SetAutoCancel(true);
            builder.SetPriority(AndroidX.Core.App.NotificationCompat.PriorityDefault);
            builder.SetVisibility(AndroidX.Core.App.NotificationCompat.VisibilitySecret);
            builder.SetContentIntent(pending);

            AndroidX.Core.App.NotificationManagerCompat.From(context)?.Notify(FailureNotificationId, builder.Build());
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("RDPVault", "Auto-type failure notice suppressed: " + ex.Message);
        }
    }

    private static void EnsureFailureChannel(Context context)
    {
        try
        {
            if (Build.VERSION.SdkInt < BuildVersionCodes.O) return;
            var channel = new NotificationChannel(
                FailureChannelId,
                context.GetString(Resource.String.autotype_channel_name),
                NotificationImportance.Default)
            {
                Description = "Tells you when the password could not be filled in automatically."
            };
            channel.LockscreenVisibility = NotificationVisibility.Secret;
            var manager = (NotificationManager?)context.GetSystemService(Context.NotificationService);
            manager?.CreateNotificationChannel(channel);
        }
        catch { }
    }

    /// <summary>
    /// Immediately clears and wipes all armed credentials from memory.
    /// </summary>
    public static void Disarm()
    {
        lock (_lock)
        {
            _armedHost = null;
            _armedUsername = null;
            _armedPassword = null;
            _armExpiry = DateTime.MinValue;
            _hasInjected = false;
            _expiryTimer?.Dispose();
            _expiryTimer = null;
            _failureTimer?.Dispose();
            _failureTimer = null;
        }

        try
        {
            var context = (Context?)MainActivity.Instance ?? global::Android.App.Application.Context;
            if (context != null)
            {
                AndroidX.Core.App.NotificationManagerCompat.From(context)?.Cancel(FailureNotificationId);
            }
        }
        catch { }
    }

    public static bool IsArmed
    {
        get
        {
            lock (_lock)
            {
                return !_hasInjected && !string.IsNullOrEmpty(_armedPassword) && DateTime.UtcNow <= _armExpiry;
            }
        }
    }

    public override void OnAccessibilityEvent(AccessibilityEvent? e)
    {
        if (e == null) return;
        if (!IsArmed) return;

        string pkg = e.PackageName?.ToString() ?? "";
        if (!pkg.Equals("com.microsoft.rdc.androidx", StringComparison.OrdinalIgnoreCase) &&
            !pkg.Equals("com.microsoft.rdc.android", StringComparison.OrdinalIgnoreCase) &&
            !pkg.Equals("com.iiordanov.freeaRDP", StringComparison.OrdinalIgnoreCase) &&
            !pkg.Equals("com.iiordanov.aRDP", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        TryInjectCredentials();
    }

    private void TryInjectCredentials()
    {
        lock (_lock)
        {
            if (!IsArmed) return;
        }

        AccessibilityNodeInfo? root = RootInActiveWindow;
        if (root == null) return;

        try
        {
            var editTexts = new List<AccessibilityNodeInfo>();
            FindEditTextNodes(root, editTexts);

            if (editTexts.Count == 0) return;

            AccessibilityNodeInfo? passwordField = null;
            AccessibilityNodeInfo? usernameField = null;

            foreach (var node in editTexts)
            {
                if (node.Password || IsPasswordField(node))
                {
                    passwordField = node;
                }
                else
                {
                    usernameField = node;
                }
            }

            // Only inject into a verified password field (Fix for Item 1: never guess into plain text boxes)
            if (passwordField == null)
            {
                global::Android.Util.Log.Warn("RDPVault", "RdpAutoTypeService: No verified password input field found. Aborting auto-type to prevent accidental exposure.");
                return;
            }
            // Verify destination host in the dialog before injecting (Item 5: ensure credentials go to verified host only)
            string targetHost = _armedHost ?? "";
            if (targetHost.Contains(':')) targetHost = targetHost.Split(':')[0];
            targetHost = targetHost.Trim('[', ']');

            var allTexts = new List<string>();
            CollectAllText(root, allTexts);

            bool hostVerified = !string.IsNullOrEmpty(targetHost) &&
                                allTexts.Any(t => t.IndexOf(targetHost, StringComparison.OrdinalIgnoreCase) >= 0);

            bool isGatewayPrompt = allTexts.Any(t => t.IndexOf("gateway", StringComparison.OrdinalIgnoreCase) >= 0);

            if (isGatewayPrompt || (!hostVerified && allTexts.Count > 0))
            {
                global::Android.Util.Log.Warn("RDPVault", $"RdpAutoTypeService: Destination host '{targetHost}' not verified on active screen. Aborting injection.");
                Disarm();
                ReportInjectionFailureIfStillArmed();
                return;
            }

            {
                string targetPass;
                string targetUser;
                lock (_lock)
                {
                    if (!IsArmed || string.IsNullOrEmpty(_armedPassword)) return;
                    targetPass = _armedPassword;
                    targetUser = _armedUsername ?? "";
                    _hasInjected = true;
                }

                // 1. Fill username if field is empty and we have a target user
                if (usernameField != null && !string.IsNullOrEmpty(targetUser))
                {
                    string currentText = usernameField.Text?.ToString() ?? "";
                    if (string.IsNullOrWhiteSpace(currentText))
                    {
                        var userBundle = new Bundle();
                        userBundle.PutCharSequence(AccessibilityNodeInfo.ActionArgumentSetTextCharsequence, targetUser);
                        usernameField.PerformAction(global::Android.Views.Accessibility.Action.SetText, userBundle);
                    }
                }

                // 2. Inject password into password field
                var passBundle = new Bundle();
                passBundle.PutCharSequence(AccessibilityNodeInfo.ActionArgumentSetTextCharsequence, targetPass);
                bool setPassSuccess = passwordField.PerformAction(global::Android.Views.Accessibility.Action.SetText, passBundle);

                // 3. Immediately wipe password from memory
                Disarm();

                if (setPassSuccess)
                {
                    global::Android.Util.Log.Info("RDPVault", "RdpAutoTypeService: Password successfully injected into RDP dialog.");

                    // 4. Click Connect/OK button after brief delay to complete zero-touch login
                    Task.Run(async () =>
                    {
                        await Task.Delay(200);
                        try
                        {
                            var freshRoot = RootInActiveWindow;
                            if (freshRoot != null)
                            {
                                var connectBtn = FindConnectButton(freshRoot);
                                connectBtn?.PerformAction(global::Android.Views.Accessibility.Action.Click);
                            }
                        }
                        catch { }
                    });
                }
            }
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("RDPVault", $"AutoType error: {ex.Message}");
        }
    }

    private static void CollectAllText(AccessibilityNodeInfo node, List<string> texts)
    {
        string text = (node.Text?.ToString() ?? node.ContentDescription?.ToString() ?? "").Trim();
        if (!string.IsNullOrEmpty(text))
        {
            texts.Add(text);
        }
        for (int i = 0; i < node.ChildCount; i++)
        {
            var child = node.GetChild(i);
            if (child != null) CollectAllText(child, texts);
        }
    }

    private static void FindEditTextNodes(AccessibilityNodeInfo node, List<AccessibilityNodeInfo> result)
    {
        if (node.ClassName?.ToString()?.Contains("EditText", StringComparison.OrdinalIgnoreCase) == true)
        {
            result.Add(node);
        }

        for (int i = 0; i < node.ChildCount; i++)
        {
            var child = node.GetChild(i);
            if (child != null)
            {
                FindEditTextNodes(child, result);
            }
        }
    }

    private static bool IsPasswordField(AccessibilityNodeInfo node)
    {
        if (node.Password) return true;

        int raw = (int)node.InputType;
        int inputClass = raw & 0x0000000f; // TYPE_MASK_CLASS
        int variation = raw & 0x00000ff0;  // TYPE_MASK_VARIATION

        if (inputClass == 0x00000001) // TYPE_CLASS_TEXT
        {
            if (variation == 0x00000080 || // TYPE_TEXT_VARIATION_PASSWORD
                variation == 0x00000090 || // TYPE_TEXT_VARIATION_VISIBLE_PASSWORD
                variation == 0x000000e0)   // TYPE_TEXT_VARIATION_WEB_PASSWORD
            {
                return true;
            }
        }
        else if (inputClass == 0x00000002) // TYPE_CLASS_NUMBER
        {
            if (variation == 0x00000010) // TYPE_NUMBER_VARIATION_PASSWORD
            {
                return true;
            }
        }

        string id = node.ViewIdResourceName?.ToLowerInvariant() ?? "";
        if (id.EndsWith("password", StringComparison.OrdinalIgnoreCase) || id.EndsWith("password_edit", StringComparison.OrdinalIgnoreCase))
            return true;

        string hint = node.HintText?.ToString()?.Trim().ToLowerInvariant() ?? "";
        if (hint == "password" || hint == "enter password")
            return true;

        return false;
    }

    private static AccessibilityNodeInfo? FindConnectButton(AccessibilityNodeInfo node)
    {
        if (node.Clickable)
        {
            string text = (node.Text?.ToString() ?? node.ContentDescription?.ToString() ?? "").Trim().ToLowerInvariant();
            if (text == "connect" || text == "ok" || text == "sign in" || text == "continue" || text == "log in")
            {
                return node;
            }
        }

        for (int i = 0; i < node.ChildCount; i++)
        {
            var child = node.GetChild(i);
            if (child != null)
            {
                var found = FindConnectButton(child);
                if (found != null) return found;
            }
        }

        return null;
    }

    /// <summary>
    /// Checks whether the RDP Vault Accessibility Service is enabled in Android system settings.
    /// </summary>
    public static bool IsServiceEnabled(Context context)
    {
        if (Instance != null) return true;

        try
        {
            int accessibilityEnabled = Settings.Secure.GetInt(context.ContentResolver, Settings.Secure.AccessibilityEnabled, 0);
            if (accessibilityEnabled != 1) return false;

            string? services = Settings.Secure.GetString(context.ContentResolver, Settings.Secure.EnabledAccessibilityServices);
            if (!string.IsNullOrEmpty(services))
            {
                var colonSplitter = services.Split(':');
                string targetClass = "com.rdpvault.app.services.RdpAutoTypeService";
                foreach (var s in colonSplitter)
                {
                    if (s.Contains(targetClass, StringComparison.OrdinalIgnoreCase) ||
                        s.Contains("RdpAutoTypeService", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("RDPVault", $"IsServiceEnabled check failed: {ex.Message}");
        }

        return false;
    }

    /// <summary>
    /// Opens the Android System Accessibility Settings screen.
    /// </summary>
    public static void OpenAccessibilitySettings(Context context)
    {
        try
        {
            var intent = new Intent(Settings.ActionAccessibilitySettings);
            intent.AddFlags(ActivityFlags.NewTask);
            context.StartActivity(intent);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("RDPVault", $"Failed to open accessibility settings: {ex.Message}");
        }
    }

    /// <summary>
    /// Opens the Android System App Info settings screen for RDP Vault to allow restricted settings.
    /// </summary>
    public static void OpenAppInfo(Context context)
    {
        try
        {
            var intent = new Intent(Settings.ActionApplicationDetailsSettings);
            intent.SetData(global::Android.Net.Uri.Parse("package:" + context.PackageName));
            intent.AddFlags(ActivityFlags.NewTask);
            context.StartActivity(intent);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("RDPVault", $"Failed to open app info: {ex.Message}");
        }
    }
}
