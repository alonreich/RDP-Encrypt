using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Android.AccessibilityServices;
using Android.App;
using Android.Content;
using Android.OS;
using Android.Provider;
using Android.Text;
using Android.Views.Accessibility;
using RDPVault;

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
    private static ConnectionEndpoint? _armedEndpoint;
    private static string? _armedHost;
    private static string? _armedPackage;
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
    public static void Arm(ConnectionEndpoint endpoint, string username, string password, string? targetPackage = null, int timeoutSeconds = 30)
    {
        if (string.IsNullOrEmpty(password)) return;

        // Dismiss previous notifications when arming a new connection
        Disarm(cancelNotification: true);

        lock (_lock)
        {
            _armedEndpoint = endpoint;
            _armedHost = endpoint.Host;
            _armedPackage = targetPackage;
            _armedUsername = username;
            _armedPassword = password;
            _armExpiry = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            _hasInjected = false;

            _expiryTimer?.Dispose();
            _expiryTimer = new System.Threading.Timer(_ => WipeCredentials(), null, timeoutSeconds * 1000, System.Threading.Timeout.Infinite);

            _failureTimer?.Dispose();
            int failureDelaySeconds = Math.Max(timeoutSeconds - 5, 25);
            _failureTimer = new System.Threading.Timer(_ =>
            {
                WipeCredentials();
            }, null, failureDelaySeconds * 1000, System.Threading.Timeout.Infinite);
        }
    }

    public static void Arm(string host, int port, string username, string password, string? targetPackage = null, int timeoutSeconds = 30)
    {
        var endpoint = ConnectionEndpoint.TryParse(host, out var parsed, out _, port: port) ? parsed : new ConnectionEndpoint(host, port);
        Arm(endpoint, username, password, targetPackage, timeoutSeconds);
    }

    public static void Arm(string host, string username, string password, int timeoutSeconds = 30)
    {
        var endpoint = ConnectionEndpoint.TryParse(host, out var parsed, out _, port: 3389) ? parsed : new ConnectionEndpoint(host, 3389);
        Arm(endpoint, username, password, null, timeoutSeconds);
    }

    /// <summary>
    /// Surfaces an auto-type failure notice in debug logs. Suppressed from posting to the OS notification drawer
    /// to eliminate notification shade pollution and trailing spam when sessions are already active.
    /// </summary>
    public static void ReportInjectionFailure(string? reason = null)
    {
        lock (_lock)
        {
            if (_hasInjected) return;
        }

        global::Android.Util.Log.Info("RDPVault", "Auto-type injection timed out or field not exposed: " + (reason ?? "Remote desktop did not expose an active password field."));
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
    /// Immediately clears and wipes all armed credentials from memory without clearing failure notifications.
    /// </summary>
    public static void WipeCredentials()
    {
        lock (_lock)
        {
            _armedEndpoint = null;
            _armedHost = null;
            _armedPackage = null;
            _armedUsername = null;
            _armedPassword = null;
            _armExpiry = DateTime.MinValue;
            _expiryTimer?.Dispose();
            _expiryTimer = null;
            _failureTimer?.Dispose();
            _failureTimer = null;
        }
    }

    /// <summary>
    /// Dismisses the failure notification if one is currently visible.
    /// </summary>
    public static void CancelFailureNotification()
    {
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

    /// <summary>
    /// Wipes credentials from memory, optionally dismissing the failure notification.
    /// </summary>
    public static void Disarm(bool cancelNotification = false)
    {
        WipeCredentials();
        lock (_lock)
        {
            _hasInjected = false;
        }
        if (cancelNotification)
        {
            CancelFailureNotification();
        }
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
        if (!string.IsNullOrEmpty(_armedPackage))
        {
            if (!pkg.Equals(_armedPackage, StringComparison.OrdinalIgnoreCase)) return;
        }
        else if (!pkg.Equals("com.microsoft.rdc.androidx", StringComparison.OrdinalIgnoreCase) &&
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

            // Verify destination host in the dialog before injecting (ensure credentials go to verified host only)
            if (_armedEndpoint == null)
            {
                WipeCredentials();
                ReportInjectionFailure("Destination host not specified for auto-type.");
                return;
            }

            var endpoint = _armedEndpoint.Value;
            string targetHost = endpoint.Host.Trim();
            string targetAddress = endpoint.Address.Trim();
            string targetHostWithPort = $"{endpoint.Host}:{endpoint.Port}";
            bool isCustomPort = endpoint.Port != 3389;

            // Scope text inspection strictly to the active login dialog container enclosing the password field
            AccessibilityNodeInfo? dialogContainer = FindDialogContainer(passwordField);
            if (dialogContainer == null)
            {
                global::Android.Util.Log.Warn("RDPVault", "RdpAutoTypeService: Could not identify bounded login dialog container. Aborting injection.");
                WipeCredentials();
                ReportInjectionFailure("Could not identify login dialog. Open RDP Vault for manual entry.");
                return;
            }

            var dialogTexts = new List<string>();
            CollectAllText(dialogContainer, dialogTexts);

            bool isGatewayPrompt = dialogTexts.Any(t => t.IndexOf("gateway", StringComparison.OrdinalIgnoreCase) >= 0);
            if (isGatewayPrompt)
            {
                global::Android.Util.Log.Warn("RDPVault", "RdpAutoTypeService: Gateway login prompt detected. Session password withheld.");
                WipeCredentials();
                ReportInjectionFailure("Gateway login prompt detected. Session password withheld.");
                return;
            }

            // Extract candidate destination text nodes specifically (excluding input fields, buttons, and static UI labels)
            var candidateDestinationTexts = new List<string>();
            CollectCandidateDestinationTexts(dialogContainer, candidateDestinationTexts);

            bool hostVerified = false;
            bool ambiguousIdentity = false;

            char[] delimiters = [' ', '\t', '\r', '\n', '\"', '\'', '(', ')', ','];

            foreach (var rawText in candidateDestinationTexts)
            {
                if (string.IsNullOrWhiteSpace(rawText)) continue;

                var tokens = rawText.Split(delimiters, StringSplitOptions.RemoveEmptyEntries);
                var testTokens = new List<string>(tokens);
                string trimmedRaw = rawText.Trim();
                if (!testTokens.Contains(trimmedRaw))
                    testTokens.Insert(0, trimmedRaw);

                foreach (var tok in testTokens)
                {
                    if (ConnectionEndpoint.TryParseDisplayedEndpoint(tok, out string dispHost, out int? dispPort, out _))
                    {
                        if (MatchesEndpoint(dispHost, dispPort, endpoint))
                        {
                            hostVerified = true;
                        }
                        else
                        {
                            // A parsed destination token pointing to a different host/port indicates conflicting/ambiguous identity
                            if (dispHost.Contains('.') || dispHost.Contains(':') || IPAddress.TryParse(dispHost, out _))
                            {
                                ambiguousIdentity = true;
                            }
                        }
                    }
                }
            }

            // Missing or ambiguous identity MUST abort injection immediately
            if (!hostVerified || ambiguousIdentity)
            {
                global::Android.Util.Log.Warn("RDPVault", $"RdpAutoTypeService: Destination endpoint '{targetAddress}' verification failed (verified={hostVerified}, ambiguous={ambiguousIdentity}). Aborting injection.");
                WipeCredentials();
                ReportInjectionFailure(ambiguousIdentity
                    ? "Ambiguous destination in login dialog. Session password withheld."
                    : (isCustomPort
                        ? $"Could not verify destination port {endpoint.Port} for {endpoint.Host} in login dialog. Open RDP Vault for manual entry."
                        : $"Could not verify destination host {endpoint.Host} in login dialog. Open RDP Vault for manual entry."));
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

                // 3. Immediately wipe password from memory and clear failure notifications
                WipeCredentials();
                CancelFailureNotification();

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
        if (node == null) return;

        bool isInput = node.Editable ||
                       node.Password ||
                       IsPasswordField(node) ||
                       (node.ClassName?.ToString()?.Contains("EditText", StringComparison.OrdinalIgnoreCase) == true);

        if (isInput)
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
        if (node == null) return null;

        var allTexts = new List<string>();
        CollectAllText(node, allTexts);
        bool hasConnectText = allTexts.Any(t =>
        {
            string clean = t.Trim().ToLowerInvariant();
            return clean is "connect" or "ok" or "sign in" or "continue" or "log in" or "next" or "done";
        });

        if (node.Clickable && hasConnectText)
        {
            return node;
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

    private static AccessibilityNodeInfo? FindDialogContainer(AccessibilityNodeInfo inputField)
    {
        AccessibilityNodeInfo? current = inputField.Parent;
        AccessibilityNodeInfo? bestCandidate = null;

        while (current != null)
        {
            string className = current.ClassName?.ToString() ?? "";
            if (className.IndexOf("DecorView", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                // Never return DecorView or window root
                break;
            }

            bool hasActions = HasDialogActions(current);
            bool isDialogClass = className.EndsWith("Dialog", StringComparison.OrdinalIgnoreCase) ||
                                 className.EndsWith("AlertDialogLayout", StringComparison.OrdinalIgnoreCase) ||
                                 (current.ViewIdResourceName?.Contains("dialog", StringComparison.OrdinalIgnoreCase) ?? false) ||
                                 (current.ViewIdResourceName?.Contains("parentPanel", StringComparison.OrdinalIgnoreCase) ?? false);

            if (isDialogClass || hasActions)
            {
                bestCandidate = current;
            }

            if (current.Parent == null || (current.Parent.ClassName?.ToString() ?? "").IndexOf("DecorView", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                bestCandidate ??= current;
                break;
            }

            current = current.Parent;
        }

        return bestCandidate;
    }

    private static bool HasDialogActions(AccessibilityNodeInfo node)
    {
        return FindConnectButton(node) != null || FindCancelButton(node) != null;
    }

    private static AccessibilityNodeInfo? FindCancelButton(AccessibilityNodeInfo node)
    {
        if (node == null) return null;

        var allTexts = new List<string>();
        CollectAllText(node, allTexts);
        bool hasCancelText = allTexts.Any(t =>
        {
            string clean = t.Trim().ToLowerInvariant();
            return clean is "cancel" or "dismiss" or "close" or "back" or "exit";
        });

        if (node.Clickable && hasCancelText)
        {
            return node;
        }

        for (int i = 0; i < node.ChildCount; i++)
        {
            var child = node.GetChild(i);
            if (child != null)
            {
                var found = FindCancelButton(child);
                if (found != null) return found;
            }
        }

        return null;
    }

    private static void CollectCandidateDestinationTexts(AccessibilityNodeInfo node, List<string> candidates)
    {
        if (node == null) return;

        if (node.ClassName?.ToString()?.Contains("EditText", StringComparison.OrdinalIgnoreCase) == true ||
            node.Password || IsPasswordField(node))
        {
            return;
        }

        if (node.ClassName?.ToString()?.Contains("Button", StringComparison.OrdinalIgnoreCase) == true ||
            node.ClassName?.ToString()?.Contains("CheckBox", StringComparison.OrdinalIgnoreCase) == true ||
            node.ClassName?.ToString()?.Contains("Switch", StringComparison.OrdinalIgnoreCase) == true ||
            node.Checkable)
        {
            return;
        }

        string text = (node.Text?.ToString() ?? node.ContentDescription?.ToString() ?? "").Trim();
        if (!string.IsNullOrEmpty(text) && !IsStaticUiLabel(text))
        {
            candidates.Add(text);
        }

        for (int i = 0; i < node.ChildCount; i++)
        {
            var child = node.GetChild(i);
            if (child != null)
            {
                CollectCandidateDestinationTexts(child, candidates);
            }
        }
    }

    private static bool IsStaticUiLabel(string text)
    {
        string lower = text.Trim().ToLowerInvariant();
        return lower is "user name" or "username" or "password" or "domain" or "credentials"
            or "enter your credentials" or "enter credentials" or "windows security"
            or "connect" or "cancel" or "ok" or "sign in" or "log in" or "continue"
            or "remember me" or "save credentials" or "show password" or "hide password"
            or "remote desktop" or "advanced options" or "dismiss" or "close" or "back"
            or "yes" or "no" or "trust certificate" or "certificate" or "fingerprint";
    }

    private static bool MatchesEndpoint(string dispHost, int? dispPort, ConnectionEndpoint armed)
    {
        bool hostMatches;
        if (IPAddress.TryParse(dispHost, out var ipDisp) && IPAddress.TryParse(armed.Host, out var ipArmed))
        {
            hostMatches = ipDisp.Equals(ipArmed);
        }
        else
        {
            hostMatches = string.Equals(dispHost, armed.Host, StringComparison.OrdinalIgnoreCase);
        }

        if (!hostMatches) return false;

        // If a specific port was displayed in the UI dialog, it must match the armed port
        if (dispPort.HasValue)
        {
            return dispPort.Value == armed.Port;
        }

        // If the UI dialog omitted the port (standard in MS Remote Desktop), the host match is authoritative
        return true;
    }

    /// <summary>
    /// Returns true only if the AccessibilityService instance is actively bound and receiving system events.
    /// After an in-place APK upgrade, Android unbinds the service until the user toggles it off/on or reboots.
    /// </summary>
    public static bool IsServiceActive => Instance != null;

    /// <summary>
    /// Checks whether the RDP Vault Accessibility Service is enabled in Android system settings.
    /// </summary>
    public static bool IsServiceEnabled(Context context)
    {
        return IsServiceActive || IsServiceConfigured(context);
    }

    /// <summary>
    /// Checks whether the service string is present in Settings.Secure.EnabledAccessibilityServices.
    /// </summary>
    public static bool IsServiceConfigured(Context context)
    {
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
            global::Android.Util.Log.Warn("RDPVault", $"IsServiceConfigured check failed: {ex.Message}");
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
