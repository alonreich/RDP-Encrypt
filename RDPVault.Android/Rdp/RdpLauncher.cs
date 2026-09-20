using System;
using System.Collections.Generic;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using RDPVault;

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

        // 1. Resolve host and port
        string host = profile.Host.Trim();
        int port = profile.Port > 0 ? profile.Port : 3389;

        if (host.Contains(':') && !host.Contains('['))
        {
            var parts = host.Split(':');
            if (parts.Length == 2 && int.TryParse(parts[1], out int parsedPort))
            {
                host = parts[0];
                port = parsedPort;
            }
        }

        string fullAddress = port == 3389 ? host : $"{host}:{port}";

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

        // If launching a desktop widescreen resolution (e.g. 1920x1080), request sensor landscape orientation
        // so that the incoming Remote Desktop client immediately opens in native 16:9 widescreen, preventing
        // the remote desktop from squashing/smooshing into portrait (1080x1920).
        var activity = (context as Activity) ?? MainActivity.Instance;
        if (!isDeviceNative && width > height && activity != null)
        {
            try
            {
                activity.RequestedOrientation = ScreenOrientation.SensorLandscape;
            }
            catch { }
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
            $"authentication%20level=i:{authLevel}",
            "promptcredentialonce=i:1",
            $"screen%20mode%20id=i:{screenModeId}",
            $"use%20multimon=i:{(useMultiMon ? 1 : 0)}",
            $"span%20monitors=i:{(useMultiMon ? 1 : 0)}",
            "desktopscale=i:100",
            "desktopscalefactor=i:100",
            "session%20bpp=i:32",
            "autoreconnection%20enabled=i:1",
            $"redirectclipboard=i:{(allowClipboard ? 1 : 0)}"
        };

        if (!isDeviceNative && width > 0 && height > 0)
        {
            queryList.Add($"desktopwidth=i:{width}");
            queryList.Add($"desktopheight=i:{height}");
            // CRITICAL: Disable dynamic resolution updates to prevent Microsoft Remote Desktop from sending
            // a display resize PDU (MS-RDPEDISP) that alters the Windows OS physical monitor resolution to 1080x1920!
            queryList.Add("dynamic%20resolution=i:0");
            queryList.Add($"smart%20sizing=i:{(effectiveSmartSizing ? 1 : 0)}");
        }
        else
        {
            queryList.Add($"smart%20sizing=i:{(effectiveSmartSizing ? 1 : 0)}");
            queryList.Add("dynamic%20resolution=i:1");
        }

        if (!string.IsNullOrEmpty(encodedUser))
        {
            queryList.Add($"username=s:{encodedUser}");
        }

        if (!string.IsNullOrWhiteSpace(profile.GatewayHost))
        {
            string encGateway = global::Android.Net.Uri.Encode(profile.GatewayHost.Trim()) ?? profile.GatewayHost.Trim();
            queryList.Add($"gatewayhostname=s:{encGateway}");
            queryList.Add("gatewayusagemethod=i:1");
            queryList.Add("gatewayprofileusagemethod=i:1");
        }

        string uriString = $"rdp://{string.Join("&", queryList)}";

        var rdpUri = global::Android.Net.Uri.Parse(uriString);

        var intent = new Intent(Intent.ActionView, rdpUri);
        intent.AddFlags(ActivityFlags.NewTask);

        var pm = context.PackageManager;

        // Try direct intent resolution
        try
        {
            var activities = pm?.QueryIntentActivities(intent, (PackageInfoFlags)0);
            if (activities != null && activities.Count > 0)
            {
                context.StartActivity(intent);
                message = "Remote Desktop client launched.";
                return RdpLaunchStatus.Success;
            }
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("RDPVault", "QueryIntentActivities failed: " + ex.Message);
        }

        // Fallback: check for known RDP apps by package ID
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
                var launchIntent = pm?.GetLaunchIntentForPackage(pkg);
                if (launchIntent != null)
                {
                    launchIntent.SetData(rdpUri);
                    launchIntent.AddFlags(ActivityFlags.NewTask);
                    context.StartActivity(launchIntent);
                    message = "Remote Desktop client launched.";
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
            message = "Remote Desktop client launched.";
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

    /// <summary>
    /// Resumes or brings the external RDP application to the foreground.
    /// </summary>
    public static bool ResumeRemoteDesktop(Context context)
    {
        var pm = context.PackageManager;
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
                var launchIntent = pm?.GetLaunchIntentForPackage(pkg);
                if (launchIntent != null)
                {
                    launchIntent.AddFlags(ActivityFlags.NewTask | ActivityFlags.ReorderToFront);
                    context.StartActivity(launchIntent);
                    return true;
                }
            }
            catch { }
        }
        return false;
    }
}
