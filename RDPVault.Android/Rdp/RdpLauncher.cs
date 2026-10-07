using System;
using Android.Content;
using RDPVault;
using RDPVault.Android.Activities;

namespace RDPVault.Android.Rdp;

public enum RdpLaunchStatus
{
    Success,
    Failed
}

public static class RdpLauncher
{
    /// <summary>
    /// Launches an embedded in-app FreeRDP connection session via RdpSessionActivity.
    /// Credentials stream directly in RAM through RdpSessionBridge without touching Android
    /// Intent extras, Logcat, clipboard, or persistent storage.
    /// </summary>
    public static RdpLaunchStatus LaunchRdp(Context context, RdpProfile profile, VaultSettings? settings, out string message)
    {
        if (profile == null)
        {
            message = "Profile is null.";
            return RdpLaunchStatus.Failed;
        }

        var endpoint = ConnectionEndpoint.FromProfile(profile);
        var (width, height, _) = profile.ResolveResolution(settings);
        bool allowClipboard = profile.ResolveAllowClipboard(settings);
        bool suppressCert = profile.ResolveSuppressCertWarnings(settings);

        int targetWidth = width > 0 ? width : 1920;
        int targetHeight = height > 0 ? height : 1080;

        char[]? passChars = null;
        if (!string.IsNullOrEmpty(profile.Password))
        {
            passChars = profile.Password.ToCharArray();
        }

        var config = new RdpSessionConfig
        {
            Host = endpoint.Host,
            Port = endpoint.Port,
            Username = profile.Username ?? "",
            Domain = "",
            PasswordChars = passChars,
            Width = targetWidth,
            Height = targetHeight,
            AllowClipboard = allowClipboard,
            SuppressCertWarnings = suppressCert,
            ProfileName = profile.Name,
            GatewayHost = profile.GatewayHost,
            GatewayPort = 443
        };

        RdpSessionBridge.SetPendingConfig(config);

        try
        {
            var intent = new Intent(context, typeof(RdpSessionActivity));
            intent.AddFlags(ActivityFlags.NewTask | ActivityFlags.SingleTop);
            context.StartActivity(intent);

            message = $"Connected via embedded FreeRDP ({targetWidth}x{targetHeight}).";
            return RdpLaunchStatus.Success;
        }
        catch (Exception ex)
        {
            message = "Failed to launch embedded session: " + ex.Message;
            return RdpLaunchStatus.Failed;
        }
    }

    /// <summary>
    /// Resumes or brings the embedded session Activity to the foreground.
    /// </summary>
    public static bool ResumeRemoteDesktop(Context context)
    {
        try
        {
            var intent = new Intent(context, typeof(RdpSessionActivity));
            intent.AddFlags(ActivityFlags.NewTask | ActivityFlags.ReorderToFront);
            context.StartActivity(intent);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
