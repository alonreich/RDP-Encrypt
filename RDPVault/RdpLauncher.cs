using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Threading;
using Microsoft.Win32;

namespace RDPVault;

public sealed class ActiveSessionInfo
{
    public Process Process { get; }
    public string ProfileId { get; }
    public string ProfileName { get; }
    public string Host { get; }
    public int Port { get; }
    public DateTime StartedAt { get; }

    public ActiveSessionInfo(Process process, RdpProfile profile)
    {
        Process = process;
        ProfileId = profile.Id;
        ProfileName = profile.Name;
        Host = profile.Host;
        Port = profile.Port;
        StartedAt = DateTime.UtcNow;
    }

    public bool HasExited
    {
        get
        {
            try { return Process.HasExited; }
            catch { return true; }
        }
    }
}

/// <summary>
/// Launches mstsc.exe from a generated temp .rdp file and scrubs everything when it closes.
/// If a password is saved, it is placed in Windows Credential Manager as a SESSION
/// credential (vanishes at sign-out / reboot) and deleted the moment the RDP window
/// closes - it is never written into the .rdp file itself.
/// </summary>
public static class RdpLauncher
{
    private static readonly List<ActiveSessionInfo> LiveSessions = new();
    private static readonly object Gate = new();

    public static event Action<string>? SessionEnded;
    public static event Action<string>? SessionStarted;
    public static event Action<string>? LaunchFailed;

    public static ActiveSessionInfo? FindActiveSession(RdpProfile p)
    {
        lock (Gate)
        {
            LiveSessions.RemoveAll(s => s.HasExited);
            return LiveSessions.FirstOrDefault(s =>
                s.ProfileId == p.Id ||
                (string.Equals(s.Host, p.Host, StringComparison.OrdinalIgnoreCase) && s.Port == p.Port));
        }
    }

    public static void DisconnectSession(ActiveSessionInfo session)
    {
        try
        {
            if (!session.Process.HasExited)
                session.Process.Kill(entireProcessTree: true);
        }
        catch { }
    }

    // ---------------- Wake-on-LAN (WOL) ----------------

    public static async Task<bool> SendWakeOnLanAsync(string macAddress, string broadcastIp = "255.255.255.255", int port = 9)
    {
        try
        {
            byte[]? macBytes = ParseMacAddress(macAddress);
            if (macBytes == null || macBytes.Length != 6) return false;

            byte[] packet = new byte[102];
            for (int i = 0; i < 6; i++) packet[i] = 0xFF;
            for (int i = 0; i < 16; i++)
                Buffer.BlockCopy(macBytes, 0, packet, 6 + i * 6, 6);

            using var client = new UdpClient();
            client.EnableBroadcast = true;
            IPAddress ip = IPAddress.TryParse(broadcastIp, out var parsed) ? parsed : IPAddress.Broadcast;
            await client.SendAsync(packet, packet.Length, new IPEndPoint(ip, port));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static byte[]? ParseMacAddress(string mac)
    {
        try
        {
            string cleaned = new string(mac.Where(Uri.IsHexDigit).ToArray());
            if (cleaned.Length != 12) return null;
            return Convert.FromHexString(cleaned);
        }
        catch { return null; }
    }

    // ---------------- Launch ----------------

    /// <summary>Start one RDP session for the profile synchronously.</summary>
    public static bool Launch(RdpProfile p)
    {
        _ = LaunchAsync(p);
        return true;
    }

    /// <summary>Start one RDP session for the profile with WOL and async monitoring.</summary>
    public static async Task<bool> LaunchAsync(RdpProfile p)
    {
        SessionManager.Current.Touch();   // issue #11: connecting is activity

        // Wake-on-LAN handling
        if (p.EnableWol && !string.IsNullOrWhiteSpace(p.WolMacAddress))
        {
            SessionStarted?.Invoke($"{p.Name} (Sending Wake-on-LAN magic packet...)");
            await SendWakeOnLanAsync(p.WolMacAddress, p.WolBroadcastIp, p.WolPort);
            if (p.WolWaitSeconds > 0)
            {
                await Task.Delay(p.WolWaitSeconds * 1000);
            }
        }

        string tempRdp = Path.Combine(Path.GetTempPath(), $"rdpv_{p.Id}.rdp");
        try
        {
            File.WriteAllText(tempRdp, BuildRdpFile(p), new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            LaunchFailed?.Invoke($"Could not prepare the connection file: {ex.Message}");
            return false;
        }

        // Issue #4: Reference-counted session credentials to avoid cross-session collisions
        var credTargets = new List<string>();
        if (p.HasPassword && !string.IsNullOrEmpty(p.Username))
        {
            foreach (string target in CredentialTargets(p))
            {
                if (SessionCredentialCoordinator.Acquire(target, p.Username, p.Password))
                    credTargets.Add(target);
            }
        }

        // Certificate pinning replay if warnings are enabled
        RestoreCertPin(p);

        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "mstsc.exe"),
            Arguments = $"\"{tempRdp}\"",
            UseShellExecute = false
        };

        Process? proc;
        try { proc = Process.Start(psi); }
        catch (Exception ex)
        {
            TryDelete(tempRdp);
            foreach (string t in credTargets) SessionCredentialCoordinator.Release(t);
            RemoveCertPin(p);
            LaunchFailed?.Invoke($"Windows could not start Remote Desktop: {ex.Message}");
            return false;
        }
        if (proc == null)
        {
            TryDelete(tempRdp);
            foreach (string t in credTargets) SessionCredentialCoordinator.Release(t);
            RemoveCertPin(p);
            LaunchFailed?.Invoke("Windows could not start Remote Desktop.");
            return false;
        }

        proc.EnableRaisingEvents = true;
        var sessionInfo = new ActiveSessionInfo(proc, p);
        lock (Gate)
        {
            LiveSessions.RemoveAll(pr => pr.HasExited);
            LiveSessions.Add(sessionInfo);
        }
        SessionStarted?.Invoke(p.Name);

        string profileName = p.Name;
        string targetHost = p.Host;

        // Issue #2: Asynchronous process wait eliminates ThreadPool starvation
        _ = Task.Run(async () =>
        {
            try
            {
                if (proc.HasExited && proc.ExitCode != 0)
                {
                    // Some Windows builds relaunch mstsc; give a short grace period.
                    await Task.Delay(3000);
                }
                else
                {
                    await proc.WaitForExitAsync();
                }
            }
            catch { /* process info no longer available */ }

            CaptureCertPin(p);
            TryDelete(tempRdp);
            foreach (string t in credTargets) SessionCredentialCoordinator.Release(t);

            // Issue #1: deterministic host cleanup even if vault locked during session
            TraceCleaner.SweepHosts(new[] { targetHost });
            TraceCleaner.Sweep();

            lock (Gate)
            {
                LiveSessions.Remove(sessionInfo);
            }

            try { proc.Dispose(); } catch { }

            SessionEnded?.Invoke(profileName);
        });

        return true;
    }

    /// <summary>Kill every mstsc.exe this app launched (USB pulled / user request).</summary>
    public static void KillAll()
    {
        lock (Gate)
        {
            foreach (var session in LiveSessions)
            {
                try { if (!session.Process.HasExited) session.Process.Kill(entireProcessTree: true); }
                catch { }
            }
            LiveSessions.Clear();
            SessionCredentialCoordinator.PurgeAll();
        }
    }

    public static int LiveCount()
    {
        lock (Gate)
        {
            LiveSessions.RemoveAll(pr => pr.HasExited);
            return LiveSessions.Count;
        }
    }

    public static bool AnyLive() => LiveCount() > 0;

    // ---------------- .rdp generation ----------------

    private static string BuildRdpFile(RdpProfile p)
    {
        var settings = SessionManager.Current.Payload?.Settings;
        bool useMulti = p.ResolveUseMultiMon(settings);
        bool fullScreen = p.ResolveFullScreen(settings);

        // Certificate warning suppression: default is to suppress (authLevel 0), can be unchecked globally or overridden per-profile
        bool suppressWarnings = p.ResolveSuppressCertWarnings(settings);
        int authLevel = suppressWarnings ? 0 : 2;

        var sb = new StringBuilder();
        sb.AppendLine("screen mode id:i:" + (fullScreen ? 2 : 1));
        if (!fullScreen)
        {
            sb.AppendLine($"desktopwidth:i:{p.Width}");
            sb.AppendLine($"desktopheight:i:{p.Height}");
        }
        sb.AppendLine("use multimon:i:" + (useMulti ? "1" : "0"));
        sb.AppendLine("session bpp:i:32");
        sb.AppendLine($"winposstr:s:0,1,0,0,{Math.Max(800, p.Width)},{Math.Max(600, p.Height)}");
        sb.AppendLine("compression:i:1");
        sb.AppendLine("keyboardhook:i:2");
        sb.AppendLine("audiocapturemode:i:0");
        sb.AppendLine("videoplaybackmode:i:1");
        sb.AppendLine("connection type:i:7");
        sb.AppendLine("networkautodetect:i:1");
        sb.AppendLine("bandwidthautodetect:i:1");
        sb.AppendLine("displayconnectionbar:i:1");
        sb.AppendLine("disable wallpaper:i:0");
        sb.AppendLine("allow font smoothing:i:1");
        sb.AppendLine("allow desktop composition:i:1");
        sb.AppendLine("disable full window drag:i:0");
        sb.AppendLine("disable menu anims:i:0");
        sb.AppendLine("disable themes:i:0");
        sb.AppendLine("disable cursor setting:i:0");
        sb.AppendLine("bitmapcachepersistenable:i:1");
        sb.AppendLine("full address:s:" + FullAddress(p));
        sb.AppendLine("audiomode:i:0");
        sb.AppendLine("redirectprinters:i:" + (p.AllowPrinters ? 1 : 0));
        sb.AppendLine("redirectcomports:i:0");
        sb.AppendLine("redirectsmartcards:i:" + (p.AllowSmartCards ? 1 : 0));
        sb.AppendLine("redirectclipboard:i:" + (p.AllowClipboard ? 1 : 0));
        sb.AppendLine("redirectposdevices:i:0");
        sb.AppendLine("autoreconnection enabled:i:1");
        sb.AppendLine("authentication level:i:" + authLevel);
        sb.AppendLine("enablecredsspsupport:i:1");
        sb.AppendLine("prompt for credentials:i:0");
        sb.AppendLine("negotiate security layer:i:1");
        sb.AppendLine("remoteapplicationmode:i:0");
        sb.AppendLine("alternate shell:s:");
        sb.AppendLine("shell working directory:s:");
        sb.AppendLine("gatewayhostname:s:" + p.GatewayHost);
        sb.AppendLine("gatewayusagemethod:i:" + (string.IsNullOrEmpty(p.GatewayHost) ? 0 : 1));
        sb.AppendLine("gatewaycredentialssource:i:4");
        sb.AppendLine("gatewayprofileusagemethod:i:" + (string.IsNullOrEmpty(p.GatewayHost) ? 0 : 1));
        sb.AppendLine("promptcredentialonce:i:0");
        sb.AppendLine("use redirection server name:i:0");
        if (!string.IsNullOrEmpty(p.Username))
        {
            sb.AppendLine("username:s:" + p.Username);   // password is NEVER written here
            sb.AppendLine("domain:s:");
        }

        if (p.AllowDrives)
        {
            sb.AppendLine("drivestoredirect:s:*");
            sb.AppendLine("redirectdrives:i:1");
        }
        else
        {
            sb.AppendLine("redirectdrives:i:0");
        }

        sb.AppendLine("pcb:s:");            // no connection bookkeeping id
        sb.AppendLine("disableremoteappcapscheck:i:1");
        return sb.ToString();
    }

    private static string FullAddress(RdpProfile p) => p.Port == 3389 ? p.Host : $"{p.Host}:{p.Port}";

    // ---------------- certificate pinning (issue #2) ----------------
    //
    // Windows records "don't ask me again for connections to this computer" as a
    // REG_BINARY value named CertHash under
    //     HKCU\Software\Microsoft\Terminal Server Client\Servers\<address>
    // holding the thumbprint of the certificate the user accepted. TraceCleaner
    // deletes that key on purpose, so the approval never used to survive a session and
    // the warning returned on every connect - which is why 1.1.1 gave up and shipped
    // authentication level 0 (no verification at all, credentials handed to whatever
    // answered). Keeping the approval inside the encrypted vault and replaying it here
    // gives us both halves: the user is asked once, and nothing is left on the PC.
    //
    // Everything below is best effort. If the registry is not writable, or Windows
    // changes where it stores this, the only consequence is that the user sees the
    // normal certificate warning again - never a failed or silently unverified
    // connection.

    private const string TscServersKey = @"Software\Microsoft\Terminal Server Client\Servers";

    private static void RestoreCertPin(RdpProfile p)
    {
        var settings = SessionManager.Current.Payload?.Settings;
        if (p.ResolveSuppressCertWarnings(settings)) return; // warnings suppressed globally or per-profile
        if (p.AllowUnverifiedServer) return;                 // nothing is being verified
        if (string.IsNullOrWhiteSpace(p.CertThumbprint)) return;

        try
        {
            byte[] hash = Convert.FromHexString(p.CertThumbprint.Trim());
            if (hash.Length == 0) return;
            using var key = Registry.CurrentUser.CreateSubKey($@"{TscServersKey}\{FullAddress(p)}");
            key?.SetValue("CertHash", hash, RegistryValueKind.Binary);
        }
        catch { /* the user simply gets the normal warning */ }
    }

    private static void CaptureCertPin(RdpProfile p)
    {
        if (p.AllowUnverifiedServer) { RemoveCertPin(p); return; }

        string? seen = null;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"{TscServersKey}\{FullAddress(p)}");
            if (key?.GetValue("CertHash") is byte[] b && b.Length > 0)
                seen = Convert.ToHexString(b);
        }
        catch { }

        // Delete the key ourselves rather than leaving it to TraceCleaner.Sweep().
        // Sweep only removes Servers\<host> entries that match a CONFIGURED vault host,
        // and ForgetHosts() empties that list the moment the vault auto-locks - so a
        // session that outlives an auto-lock would otherwise leave the very registry
        // trace this app exists to erase, planted by us.
        RemoveCertPin(p);

        if (string.IsNullOrEmpty(seen)) return;
        if (string.Equals(seen, p.CertThumbprint, StringComparison.OrdinalIgnoreCase)) return;

        // Copy into a non-nullable local: the compiler discards the null-state of a
        // captured variable inside a lambda (CS8601 otherwise).
        string thumb = seen;

        // This runs on a background task when mstsc exits. Every other vault save
        // happens on the UI thread from a user action, and VaultCrypto.WriteAtomic
        // shares one .tmp path - so hop to the UI thread instead of racing them.
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                var mgr = SessionManager.Current;
                if (!mgr.IsUnlocked) return;                       // locked since; nothing to write into
                if (mgr.Payload?.Profiles.Contains(p) != true) return;  // deleted or edited away
                p.CertThumbprint = thumb;
                mgr.TrySave(out _);   // bookkeeping: never surface as an error to the user
            }
            catch { }
        });
    }

    /// <summary>
    /// Removes the Servers\&lt;address&gt; key this app created to replay a certificate
    /// approval. Always called once the session is over, so the pin lives only in the
    /// encrypted vault and never on this PC.
    /// </summary>
    private static void RemoveCertPin(RdpProfile p)
    {
        try
        {
            using var servers = Registry.CurrentUser.OpenSubKey(TscServersKey, writable: true);
            servers?.DeleteSubKeyTree(FullAddress(p), throwOnMissingSubKey: false);
        }
        catch { }
    }

    private static IEnumerable<string> CredentialTargets(RdpProfile p)
    {
        yield return $"TERMSRV/{p.Host}";
        if (p.Port != 3389) yield return $"TERMSRV/{p.Host}:{p.Port}";
    }

    // ---------------- session credential coordinator (issue #4) ----------------

    private static class SessionCredentialCoordinator
    {
        private static readonly Dictionary<string, int> TargetRefCounts = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object CredLock = new();

        public static bool Acquire(string target, string user, string password)
        {
            lock (CredLock)
            {
                if (TargetRefCounts.TryGetValue(target, out int count))
                {
                    TargetRefCounts[target] = count + 1;
                    return true;
                }

                if (WriteSessionCredential(target, user, password))
                {
                    TargetRefCounts[target] = 1;
                    return true;
                }
                return false;
            }
        }

        public static void Release(string target)
        {
            lock (CredLock)
            {
                if (TargetRefCounts.TryGetValue(target, out int count))
                {
                    if (count <= 1)
                    {
                        TargetRefCounts.Remove(target);
                        DeleteCredential(target);
                    }
                    else
                    {
                        TargetRefCounts[target] = count - 1;
                    }
                }
                else
                {
                    DeleteCredential(target);
                }
            }
        }

        public static void PurgeAll()
        {
            lock (CredLock)
            {
                foreach (string target in TargetRefCounts.Keys)
                {
                    DeleteCredential(target);
                }
                TargetRefCounts.Clear();
            }
        }
    }

    // ---------------- session credential via CredWrite ----------------

    private const int CRED_TYPE_GENERIC = 1;
    private const int CRED_PERSIST_SESSION = 1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public int Flags;
        public int Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWriteW(ref CREDENTIAL cred, int flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDeleteW(string target, int type, int reserved);

    private static bool WriteSessionCredential(string target, string user, string password)
    {
        byte[] blob = Encoding.Unicode.GetBytes(password);
        IntPtr blobPtr = Marshal.AllocHGlobal(blob.Length);
        IntPtr userPtr = Marshal.StringToHGlobalUni(user);
        IntPtr targetPtr = Marshal.StringToHGlobalUni(target);
        try
        {
            Marshal.Copy(blob, 0, blobPtr, blob.Length);
            var c = new CREDENTIAL
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = targetPtr,
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob = blobPtr,
                Persist = CRED_PERSIST_SESSION, // lives only until logoff; we delete sooner
                UserName = userPtr
            };
            return CredWriteW(ref c, 0);
        }
        catch { return false; }
        finally
        {
            // Scrub the unmanaged copy of the password before releasing it.
            try { for (int i = 0; i < blob.Length; i++) Marshal.WriteByte(blobPtr, i, 0); } catch { }
            Marshal.FreeHGlobal(blobPtr);
            Marshal.FreeHGlobal(userPtr);
            Marshal.FreeHGlobal(targetPtr);
            Array.Clear(blob);
        }
    }

    private static void DeleteCredential(string target)
    {
        try { CredDeleteW(target, CRED_TYPE_GENERIC, 0); } catch { }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
