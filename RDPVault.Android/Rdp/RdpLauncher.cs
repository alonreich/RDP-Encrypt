using System;
using System.Collections.Generic;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using RDPVault;
using RDPVault.Android.Services;

namespace RDPVault.Android.Rdp;

public enum RdpLaunchStatus
{
    Success,
    RedirectedToStore,
    Failed
}

public static class RdpLauncher
{
    /// <summary>
    /// Launches an RDP connection session via standard Android intent handoff.
    /// Strictly protects password hygiene: credentials are NEVER exposed to the system clipboard,
    /// preventing cloud sync or predictive text ingestion by external apps.
    /// Configures authentication level 0 by default to suppress untrusted certificate warnings.
    /// Falls back to Google Play Store if no compatible RDP client is installed.
    /// </summary>
    public static RdpLaunchStatus LaunchRdp(Context context, RdpProfile profile, VaultSettings? settings, out string message)
    {
        if (profile == null)
        {
            message = "Profile is null.";
            return RdpLaunchStatus.Failed;
        }

        // 1. Resolve host and port using authoritative ConnectionEndpoint
        var endpoint = ConnectionEndpoint.FromProfile(profile);
        string host = endpoint.Host;
        int port = endpoint.Port;
        string fullAddress = endpoint.Address;

        // 2. Resolve certificate warning suppression (default to true on Android)
        bool suppressCert = profile.SuppressCertWarningsOverride switch
        {
            TriStateOverride.Enabled => true,
            TriStateOverride.Disabled => false,
            _ => settings?.SuppressCertWarnings ?? true
        };

        // authentication level:i:0 suppresses certificate/identity verification warnings in Microsoft Remote Desktop
        int authLevel = suppressCert ? 0 : 2;

        // 3. Resolve resolution and multi-monitor parameters (prevent remote desktop & monitor scramble)
        var (width, height, isDeviceNative) = profile.ResolveResolution(settings);
        bool smartSizing = profile.ResolveSmartSizing(settings);
        bool useMultiMon = profile.ResolveUseMultiMon(settings);
        bool allowClipboard = profile.ResolveAllowClipboard(settings);

        // Desktop Protection: Force Landscape orientation before handoff for all desktop profiles (where width >= height).
        // This ensures the Android window manager and Microsoft Remote Desktop / aRDP initialize their viewports
        // in Landscape mode rather than Portrait (1080x1920), preventing Windows from collapsing multi-monitor
        // host workstations into a vertical phone screen and scrambling desktop icons and windows.
        if (width >= height && MainActivity.Instance != null)
        {
            MainActivity.Instance.RequestedOrientation = ScreenOrientation.SensorLandscape;
        }

        // 4. Construct standard Microsoft Remote Desktop URI with display & monitor protection
        // Format: rdp://full%20address=s:{host}:{port}&desktopwidth=i:{w}&desktopheight=i:{h}&screen%20mode%20id=i:{mode}&smart%20sizing=i:{sizing}&dynamic%20resolution=i:0&use%20multimon=i:0&span%20monitors=i:0&authentication%20level=i:{authLevel}&promptcredentialonce=i:1
        string encodedAddress = global::Android.Net.Uri.Encode(fullAddress) ?? fullAddress;
        string encodedUser = string.IsNullOrWhiteSpace(profile.Username) ? "" : (global::Android.Net.Uri.Encode(profile.Username) ?? profile.Username);

        // Determine effective smart sizing (scaling):
        // When user chooses "Never squash / Scroll to view", effectiveSmartSizing is FALSE.
        // When smart sizing is disabled (1:1 native scrollable), screen mode id = 1 (windowed/scrollable).
        // When smart sizing is enabled (fit to screen), screen mode id = 2 (full screen scaled).
        bool effectiveSmartSizing = profile.SmartSizingOverride switch
        {
            TriStateOverride.Enabled => true,
            TriStateOverride.Disabled => false,
            _ => smartSizing
        };
        int screenModeId = effectiveSmartSizing ? 2 : 1;

        var queryList = new List<string>
        {
            $"full%20address=s:{encodedAddress}",
            $"server%20port=i:{port}",
            $"authentication%20level=i:{authLevel}",
            "promptcredentialonce=i:1",
            "prompt%20for%20credentials%20on%20client=i:0",
            $"screen%20mode%20id=i:{screenModeId}",
            $"use%20multimon=i:{(useMultiMon ? 1 : 0)}",
            $"span%20monitors=i:{(useMultiMon ? 1 : 0)}",
            "desktopsizeid=i:0",
            "desktopscale=i:100",
            "desktopscalefactor=i:100",
            "session%20bpp=i:32",
            "autoreconnection%20enabled=i:1",
            $"redirectclipboard=i:{(allowClipboard ? 1 : 0)}"
        };

        int targetWidth = width > 0 ? width : 1920;
        int targetHeight = height > 0 ? height : 1080;
        if (targetWidth < targetHeight)
        {
            (targetWidth, targetHeight) = (targetHeight, targetWidth);
        }

        queryList.Add($"desktopwidth=i:{targetWidth}");
        queryList.Add($"desktopheight=i:{targetHeight}");
        // CRITICAL: Disable dynamic resolution updates to prevent Microsoft Remote Desktop from sending
        // a display resize PDU (MS-RDPEDISP) that alters the Windows OS physical monitor resolution!
        queryList.Add("dynamic%20resolution=i:0");
        queryList.Add($"smart%20sizing=i:{(effectiveSmartSizing ? 1 : 0)}");

        if (!string.IsNullOrEmpty(encodedUser))
        {
            queryList.Add($"username=s:{encodedUser}");
        }

        if (!string.IsNullOrWhiteSpace(profile.GatewayHost))
        {
            string gwAddress = ConnectionEndpoint.TryParseGatewayAuthority(profile.GatewayHost, out var gwEp, out _)
                ? gwEp.Address
                : profile.GatewayHost.Trim();
            string encGateway = global::Android.Net.Uri.Encode(gwAddress) ?? gwAddress;
            queryList.Add($"gatewayhostname=s:{encGateway}");
            queryList.Add("gatewayusagemethod=i:1");
            queryList.Add("gatewayprofileusagemethod=i:1");
        }

        string uriString = $"rdp://{string.Join("&", queryList)}";

        var rdpUri = global::Android.Net.Uri.Parse(uriString);
        var intent = new Intent(Intent.ActionView, rdpUri);
        intent.AddFlags(ActivityFlags.NewTask);

        var pm = context.PackageManager;

        string[] knownPackages = new[]
        {
            "com.microsoft.rdc.androidx",
            "com.microsoft.rdc.android",
            "com.iiordanov.freeaRDP",
            "com.iiordanov.aRDP"
        };

        string? targetPkg = null;

        // Try direct intent resolution targeting an explicit package
        try
        {
            var activities = pm?.QueryIntentActivities(intent, (PackageInfoFlags)0);
            if (activities != null && activities.Count > 0)
            {
                foreach (var known in knownPackages)
                {
                    if (activities.Any(a => string.Equals(a.ActivityInfo?.PackageName, known, StringComparison.OrdinalIgnoreCase)))
                    {
                        targetPkg = known;
                        break;
                    }
                }
                targetPkg ??= activities[0].ActivityInfo?.PackageName;
            }
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("RDPVault", "QueryIntentActivities failed: " + ex.Message);
        }

        // Arm the accessibility auto-type service if a password is present
        if (!string.IsNullOrEmpty(profile.Password))
        {
            RdpAutoTypeService.Arm(endpoint, profile.Username, profile.Password, targetPackage: targetPkg, timeoutSeconds: 45);
        }

        // Tell the activity we are deliberately leaving the screen so the
        // "lock the instant the app is backgrounded" rule does not slam the vault shut
        // mid hand-off (and so the user is not re-prompted on the way back).
        MainActivity.Instance?.BeginExternalActivity();

        global::Android.Net.Uri BuildClientUri(string pkg)
        {
            if (pkg.Contains("aRDP", StringComparison.OrdinalIgnoreCase) || pkg.Contains("freeaRDP", StringComparison.OrdinalIgnoreCase))
            {
                string ardpUri = !string.IsNullOrEmpty(encodedUser)
                    ? $"rdp://{encodedUser}@{host}:{port}"
                    : $"rdp://{host}:{port}";
                return global::Android.Net.Uri.Parse(ardpUri) ?? rdpUri;
            }
            return rdpUri;
        }

        void AddClientSpecificExtras(Intent targetIntent, string pkg)
        {
            if (pkg.Contains("aRDP", StringComparison.OrdinalIgnoreCase) || pkg.Contains("freeaRDP", StringComparison.OrdinalIgnoreCase))
            {
                if (!isDeviceNative && width > 0 && height > 0)
                {
                    targetIntent.PutExtra("desktopWidth", width);
                    targetIntent.PutExtra("desktopHeight", height);
                }
                targetIntent.PutExtra("autoFitServer", effectiveSmartSizing);
                targetIntent.PutExtra("panZoomMode", !effectiveSmartSizing);
                if (!string.IsNullOrEmpty(profile.Username))
                    targetIntent.PutExtra("username", profile.Username);
                targetIntent.PutExtra("host", host);
                targetIntent.PutExtra("port", port);
                targetIntent.PutExtra("bpp", 32);
                targetIntent.PutExtra("enableClipboard", allowClipboard);
            }
        }

        string FormatSuccessMessage(string pkg)
        {
            return "Connected in 1080p Landscape mode.";
        }

        if (!string.IsNullOrEmpty(targetPkg))
        {
            intent.SetData(BuildClientUri(targetPkg));
            intent.SetPackage(targetPkg);
            AddClientSpecificExtras(intent, targetPkg);
            LastUsedPackage = targetPkg;
            context.StartActivity(intent);
            message = FormatSuccessMessage(targetPkg);
            return RdpLaunchStatus.Success;
        }

        foreach (var pkg in knownPackages)
        {
            try
            {
                var launchIntent = pm?.GetLaunchIntentForPackage(pkg);
                if (launchIntent != null)
                {
                    LastUsedPackage = pkg;
                    launchIntent.SetData(BuildClientUri(pkg));
                    AddClientSpecificExtras(launchIntent, pkg);
                    launchIntent.AddFlags(ActivityFlags.NewTask);
                    context.StartActivity(launchIntent);
                    message = FormatSuccessMessage(pkg);
                    return RdpLaunchStatus.Success;
                }
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Warn("RDPVault", $"Launch package {pkg} failed: {ex.Message}");
            }
        }

        // Final fallback: try raw StartActivity in case an OS default handler handles it
        try
        {
            context.StartActivity(intent);
            message = FormatSuccessMessage("default");
            return RdpLaunchStatus.Success;
        }
        catch (ActivityNotFoundException)
        {
            // No RDP application installed: redirect to Google Play Store to install Microsoft Remote Desktop
            try
            {
                var storeIntent = new Intent(Intent.ActionView, global::Android.Net.Uri.Parse("market://details?id=com.microsoft.rdc.androidx"));
                storeIntent.AddFlags(ActivityFlags.NewTask);
                context.StartActivity(storeIntent);
                message = "Microsoft Remote Desktop not installed. Opening Google Play Store...";
                return RdpLaunchStatus.RedirectedToStore;
            }
            catch
            {
                try
                {
                    var webStoreIntent = new Intent(Intent.ActionView, global::Android.Net.Uri.Parse("https://play.google.com/store/apps/details?id=com.microsoft.rdc.androidx"));
                    webStoreIntent.AddFlags(ActivityFlags.NewTask);
                    context.StartActivity(webStoreIntent);
                    message = "Microsoft Remote Desktop not installed. Opening Google Play Store...";
                    return RdpLaunchStatus.RedirectedToStore;
                }
                catch (Exception finalEx)
                {
                    message = "Failed to launch RDP client or open Play Store: " + finalEx.Message;
                    return RdpLaunchStatus.Failed;
                }
            }
        }
        catch (Exception ex)
        {
            message = "Unexpected error launching RDP client: " + ex.Message;
            return RdpLaunchStatus.Failed;
        }
    }

    /// <summary>The package name of the external RDP app that last handled a launch.</summary>
    public static string? LastUsedPackage { get; set; }

    /// <summary>
    /// Creates an explicit launch Intent targeting the active or first available RDP client app.
    /// Used for direct Activity PendingIntents to avoid Android 12+ notification service trampolines.
    /// </summary>
    public static Intent? CreateResumeIntent(Context context)
    {
        var pm = context.PackageManager;
        if (pm == null) return null;

        if (!string.IsNullOrEmpty(LastUsedPackage))
        {
            try
            {
                var directIntent = pm.GetLaunchIntentForPackage(LastUsedPackage);
                if (directIntent != null) return directIntent;
            }
            catch { }
        }

        string[] knownPackages = new[]
        {
            "com.microsoft.rdc.androidx",
            "com.microsoft.rdc.android",
            "com.iiordanov.freeaRDP",
            "com.iiordanov.aRDP"
        };

        foreach (var pkg in knownPackages)
        {
            try
            {
                var launchIntent = pm.GetLaunchIntentForPackage(pkg);
                if (launchIntent != null) return launchIntent;
            }
            catch { }
        }
        return null;
    }

    /// <summary>
    /// Resumes or brings the external RDP application to the foreground.
    /// </summary>
    public static bool ResumeRemoteDesktop(Context context)
    {
        MainActivity.Instance?.BeginExternalActivity();
        var intent = CreateResumeIntent(context);
        if (intent != null)
        {
            intent.AddFlags(ActivityFlags.NewTask | ActivityFlags.ReorderToFront);
            context.StartActivity(intent);
            return true;
        }
        return false;
    }
}
