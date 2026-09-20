using System;
using System.Collections.Generic;
using System.Threading.Tasks;
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
    /// Securely copies the password to clipboard with Android 13+ IS_SENSITIVE flag and 30-second auto-wipe.
    /// Falls back to Google Play Store if no compatible RDP client is installed.
    /// </summary>
    public static RdpLaunchStatus LaunchRdp(Context context, RdpProfile profile, out string message)
    {
        if (profile == null)
        {
            message = "Profile is null.";
            return RdpLaunchStatus.Failed;
        }

        // 1. Copy password to clipboard with 30-second auto-wipe
        CopyPasswordWithAutoWipe(context, profile.Password);

        // 2. Resolve host and port
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

        // 3. Construct standard Microsoft Remote Desktop URI
        // Format: rdp://full%20address=s:{host}:{port}&username=s:{username}
        string encodedAddress = global::Android.Net.Uri.Encode(fullAddress) ?? fullAddress;
        string encodedUser = string.IsNullOrWhiteSpace(profile.Username) ? "" : (global::Android.Net.Uri.Encode(profile.Username) ?? profile.Username);

        string uriString = string.IsNullOrEmpty(encodedUser)
            ? $"rdp://full%20address=s:{encodedAddress}"
            : $"rdp://full%20address=s:{encodedAddress}&username=s:{encodedUser}";

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
                message = "Remote Desktop launched! Password copied to clipboard (clears in 30s).";
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
                    message = "Remote Desktop launched! Password copied to clipboard (clears in 30s).";
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
            message = "Remote Desktop launched! Password copied to clipboard (clears in 30s).";
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

    private static void CopyPasswordWithAutoWipe(Context context, string password)
    {
        if (string.IsNullOrEmpty(password)) return;

        try
        {
            var clipboard = (ClipboardManager?)context.GetSystemService(Context.ClipboardService);
            if (clipboard == null) return;

            var clip = ClipData.NewPlainText("RDP Password", password);
            if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
            {
                clip.Description?.Extras?.PutBoolean(ClipDescription.ExtraIsSensitive, true);
            }
            clipboard.PrimaryClip = clip;

            // 30-second self-destruct background wipe
            var mainHandler = new Handler(Looper.MainLooper!);
            _ = Task.Run(async () =>
            {
                await Task.Delay(30000);
                mainHandler.Post(() =>
                {
                    try
                    {
                        if (clipboard.HasPrimaryClip && clipboard.PrimaryClip?.ItemCount > 0)
                        {
                            var item = clipboard.PrimaryClip.GetItemAt(0);
                            if (item?.Text == password)
                            {
                                if (Build.VERSION.SdkInt >= BuildVersionCodes.P)
                                {
                                    clipboard.ClearPrimaryClip();
                                }
                                else
                                {
                                    clipboard.PrimaryClip = ClipData.NewPlainText("", "");
                                }
                            }
                        }
                    }
                    catch { }
                });
            });
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("RDPVault", "Failed to copy password to clipboard: " + ex.Message);
        }
    }
}
