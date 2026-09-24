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
using System.Net.NetworkInformation;
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

public sealed class LaunchProgressUpdate
{
    public string Step { get; init; } = "";
    public string Details { get; init; } = "";
    public int? SecondsRemaining { get; init; }
    public double? ProgressPercent { get; init; }
    public bool IsIndeterminate { get; init; } = true;
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

    public struct WolDispatchReport
    {
        public int PacketsSent;
        public List<string> TargetEndpoints;
        public List<int> PortsUsed;
    }

    private static IPAddress? CalculateBroadcastAddress(IPAddress address, IPAddress mask)
    {
        try
        {
            byte[] ipBytes = address.GetAddressBytes();
            byte[] maskBytes = mask.GetAddressBytes();
            if (ipBytes.Length != maskBytes.Length) return null;

            byte[] broadcastBytes = new byte[ipBytes.Length];
            for (int i = 0; i < ipBytes.Length; i++)
            {
                broadcastBytes[i] = (byte)(ipBytes[i] | (~maskBytes[i] & 0xFF));
            }
            return new IPAddress(broadcastBytes);
        }
        catch { return null; }
    }

    /// <summary>
    /// Robust multi-interface, multi-target, multi-port Wake-on-LAN transmission.
    /// Dispatches the 102-byte magic packet to:
    /// 1. Target Host unicast IP/hostname (resolving DNS and penetrating WAN/VPN port forwards)
    /// 2. Subnet-directed broadcasts across all active local network adapters
    /// 3. Global broadcast (255.255.255.255)
    /// 4. Configured broadcast IP (if customized)
    /// Across configured WOL port (default: 9), alternative echo port (7), and custom RDP port (e.g. 11).
    /// </summary>
    public static async Task<(bool success, WolDispatchReport report)> SendWakeOnLanAsync(
        string macAddress,
        string? host = null,
        string broadcastIp = "255.255.255.255",
        int wolPort = 9,
        int rdpPort = 3389)
    {
        var report = new WolDispatchReport
        {
            TargetEndpoints = new List<string>(),
            PortsUsed = new List<int>()
        };

        try
        {
            byte[]? macBytes = ParseMacAddress(macAddress);
            if (macBytes == null || macBytes.Length != 6) return (false, report);

            byte[] packet = new byte[102];
            for (int i = 0; i < 6; i++) packet[i] = 0xFF;
            for (int i = 0; i < 16; i++)
                Buffer.BlockCopy(macBytes, 0, packet, 6 + i * 6, 6);

            // Target ports: configured WOL port (9), echo port (7), and custom RDP port (e.g. 11)
            var ports = new HashSet<int> { wolPort > 0 ? wolPort : 9, 7 };
            if (rdpPort > 0 && rdpPort != 3389 && rdpPort != wolPort && rdpPort != 7)
            {
                ports.Add(rdpPort);
            }
            report.PortsUsed = ports.ToList();

            var targetIps = new HashSet<IPAddress>();

            // 1. Global broadcast
            targetIps.Add(IPAddress.Broadcast);

            // 2. User-configured broadcast IP
            if (!string.IsNullOrWhiteSpace(broadcastIp) &&
                !string.Equals(broadcastIp.Trim(), "255.255.255.255", StringComparison.OrdinalIgnoreCase) &&
                IPAddress.TryParse(broadcastIp.Trim(), out var customBcast))
            {
                targetIps.Add(customBcast);
            }

            // 3. Target Host IP or Hostname (Unicast WOL)
            if (!string.IsNullOrWhiteSpace(host))
            {
                string cleanHost = host.Trim();
                if (IPAddress.TryParse(cleanHost, out var hostIp))
                {
                    targetIps.Add(hostIp);
                }
                else
                {
                    try
                    {
                        var resolved = await Dns.GetHostAddressesAsync(cleanHost);
                        foreach (var ip in resolved.Where(a => a.AddressFamily == AddressFamily.InterNetwork))
                        {
                            targetIps.Add(ip);
                        }
                    }
                    catch { }
                }
            }

            // 4. Local subnet directed broadcasts
            var localInterfaces = new List<(IPAddress LocalIp, IPAddress? SubnetBroadcast)>();
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up ||
                        ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                        continue;

                    var ipProps = ni.GetIPProperties();
                    foreach (var u in ipProps.UnicastAddresses)
                    {
                        if (u.Address.AddressFamily == AddressFamily.InterNetwork && u.IPv4Mask != null)
                        {
                            var subnetBcast = CalculateBroadcastAddress(u.Address, u.IPv4Mask);
                            if (subnetBcast != null)
                            {
                                targetIps.Add(subnetBcast);
                                localInterfaces.Add((u.Address, subnetBcast));
                            }
                        }
                    }
                }
            }
            catch { }

            report.TargetEndpoints = targetIps.Select(ip => ip.ToString()).Distinct().ToList();

            int sentCount = 0;

            // Strategy A: Transmit from bound sockets on each local interface to ensure packets physical exit
            foreach (var (localIp, subnetBcast) in localInterfaces)
            {
                try
                {
                    using var boundClient = new UdpClient(new IPEndPoint(localIp, 0));
                    boundClient.EnableBroadcast = true;

                    var destsForNic = new HashSet<IPAddress> { IPAddress.Broadcast };
                    if (subnetBcast != null) destsForNic.Add(subnetBcast);

                    foreach (var dest in destsForNic)
                    {
                        foreach (int port in ports)
                        {
                            try
                            {
                                await boundClient.SendAsync(packet, packet.Length, new IPEndPoint(dest, port));
                                sentCount++;
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            }

            // Strategy B: Standard unbound socket sending to all collected targets (including Host unicast)
            using (var generalClient = new UdpClient())
            {
                generalClient.EnableBroadcast = true;
                foreach (var target in targetIps)
                {
                    foreach (int port in ports)
                    {
                        try
                        {
                            await generalClient.SendAsync(packet, packet.Length, new IPEndPoint(target, port));
                            sentCount++;
                        }
                        catch { }
                    }
                }
            }

            report.PacketsSent = sentCount;
            return (sentCount > 0, report);
        }
        catch
        {
            return (false, report);
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
    public static Task<bool> LaunchAsync(RdpProfile p) => LaunchAsync(p, null, System.Threading.CancellationToken.None);

    /// <summary>Start one RDP session for the profile with tactical progress updates and cancellation support.</summary>
    public static async Task<bool> LaunchAsync(
        RdpProfile p,
        Action<LaunchProgressUpdate>? progress,
        System.Threading.CancellationToken ct = default)
    {
        SessionManager.Current.Touch();   // issue #11: connecting is activity
        // Work on a snapshot. The Port field always wins over a stale port pasted in Host.
        p = p.Clone();
        try
        {
            var ep = ConnectionEndpoint.FromProfile(p);
            p.Host = ep.Host;
            p.Port = ep.Port;
        }
        catch (ArgumentException ex) { LaunchFailed?.Invoke(ex.Message); return false; }

        // Wake-on-LAN handling
        if (p.EnableWol && !string.IsNullOrWhiteSpace(p.WolMacAddress))
        {
            SessionStarted?.Invoke($"{p.Name} (Sending Wake-on-LAN magic packet...)");
            progress?.Invoke(new LaunchProgressUpdate
            {
                Step = "Wake-on-LAN Dispatch",
                Details = $"Preparing magic packet for MAC {p.WolMacAddress} (Target Host: {p.Host})...",
                IsIndeterminate = true
            });

            var (wolOk, report) = await SendWakeOnLanAsync(
                p.WolMacAddress,
                host: p.Host,
                broadcastIp: p.WolBroadcastIp,
                wolPort: p.WolPort,
                rdpPort: p.Port);

            if (!wolOk)
            {
                LaunchFailed?.Invoke("Could not send Wake-on-LAN magic packet. Verify the MAC address format.");
                return false;
            }

            string endpointsSummary = string.Join(", ", report.TargetEndpoints.Take(3)) +
                (report.TargetEndpoints.Count > 3 ? $" (+{report.TargetEndpoints.Count - 3} more)" : "");
            string portsSummary = string.Join(", ", report.PortsUsed);

            progress?.Invoke(new LaunchProgressUpdate
            {
                Step = "Wake-on-LAN Broadcasted",
                Details = $"Sent {report.PacketsSent} magic packet(s) to {endpointsSummary} (Ports: {portsSummary}).",
                IsIndeterminate = true
            });

            if (p.WolWaitSeconds > 0)
            {
                int total = p.WolWaitSeconds;
                for (int s = total; s > 0; s--)
                {
                    if (ct.IsCancellationRequested)
                    {
                        LaunchFailed?.Invoke("Connection cancelled by user during Wake-on-LAN wait.");
                        return false;
                    }

                    double percent = 100.0 * (total - s) / total;
                    progress?.Invoke(new LaunchProgressUpdate
                    {
                        Step = "Waking Remote Host",
                        Details = $"Wake packet broadcasted. Waiting for remote host to boot ({s}s remaining)...",
                        SecondsRemaining = s,
                        ProgressPercent = percent,
                        IsIndeterminate = false
                    });

                    try
                    {
                        await Task.Delay(1000, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        LaunchFailed?.Invoke("Connection cancelled by user during Wake-on-LAN wait.");
                        return false;
                    }
                }
            }
        }

        if (ct.IsCancellationRequested)
        {
            LaunchFailed?.Invoke("Connection cancelled by user.");
            return false;
        }

        if (p.EnableIcmpKnock)
        {
            bool isTcp = string.Equals(p.KnockProtocol, "TCP", StringComparison.OrdinalIgnoreCase);
            int delaySec = p.KnockDelaySeconds >= 0 ? p.KnockDelaySeconds : 2;
            progress?.Invoke(new LaunchProgressUpdate
            {
                Step = "Port Knocking",
                Details = isTcp
                    ? $"Sending TCP knock to {p.Host}:{p.KnockTcpPort}; waiting {delaySec}s before RDP on port {p.Port}."
                    : $"Sending ICMP magic packet to {p.Host}; waiting {delaySec}s before RDP on port {p.Port}.",
                IsIndeterminate = true
            });
            try { await IcmpKnock.SendBeforeConnectAsync(p, IcmpKnock.SendWindowsAsync, ct); }
            catch (OperationCanceledException) { LaunchFailed?.Invoke("Connection cancelled."); return false; }
            catch (Exception ex) { LaunchFailed?.Invoke("Port knock could not be sent: " + ex.Message); return false; }
        }

        progress?.Invoke(new LaunchProgressUpdate
        {
            Step = "Preparing Connection",
            Details = $"Configuring temporary connection profile for {p.DisplayHost}...",
            IsIndeterminate = true
        });

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
            progress?.Invoke(new LaunchProgressUpdate
            {
                Step = "Session Credentials",
                Details = "Writing session-scoped credentials to Windows Credential Manager...",
                IsIndeterminate = true
            });
            foreach (string target in CredentialTargets(p))
            {
                if (SessionCredentialCoordinator.Acquire(target, p.Username, p.Password))
                    credTargets.Add(target);
            }
        }

        // Certificate pinning replay if warnings are enabled
        RestoreCertPin(p);

        progress?.Invoke(new LaunchProgressUpdate
        {
            Step = "Starting Remote Desktop",
            Details = "Spawning mstsc.exe process...",
            IsIndeterminate = true
        });

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

        progress?.Invoke(new LaunchProgressUpdate
        {
            Step = "Connected",
            Details = $"Remote Desktop window opened for {p.Name}.",
            IsIndeterminate = false,
            ProgressPercent = 100
        });

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
            var report = TraceCleaner.SweepHosts(new[] { targetHost });
            TraceCleaner.Sweep();

            lock (Gate)
            {
                LiveSessions.Remove(sessionInfo);
            }

            try { proc.Dispose(); } catch { }

            string sweepStatus = report.ItemsLocked > 0
                ? $"{profileName} closed - traces cleaned ({report.ItemsLocked} locked file queued for next sweep)."
                : $"{profileName} closed - local traces cleaned.";
            SessionEnded?.Invoke(sweepStatus);
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

        // Certificate warning suppression: default is to verify (authLevel 2), can be suppressed globally or overridden per-profile
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
        sb.AppendLine($"server port:i:{p.Port}");
        sb.AppendLine("audiomode:i:0");
        sb.AppendLine("redirectprinters:i:" + (p.AllowPrinters ? 1 : 0));
        sb.AppendLine("redirectcomports:i:0");
        sb.AppendLine("redirectsmartcards:i:" + (p.AllowSmartCards ? 1 : 0));
        sb.AppendLine("redirectclipboard:i:" + (p.ResolveAllowClipboard(settings) ? 1 : 0));
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

    private static string FullAddress(RdpProfile p) => ConnectionEndpoint.FromProfile(p).Address;

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
                var target = mgr.Payload?.Profiles.FirstOrDefault(x => x.Id == p.Id);
                if (target == null) return;  // deleted or edited away
                target.CertThumbprint = thumb;
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
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            $"TERMSRV/{p.Host}",
            $"TERMSRV/{FullAddress(p)}"
        };
        if (p.Port != 3389)
        {
            targets.Add($"TERMSRV/{p.Host}:{p.Port}");
        }
        return targets;
    }

    // ---------------- session credential coordinator (issue #4) ----------------

    private static class SessionCredentialCoordinator
    {
        private record struct ActiveCred(int RefCount, string Username, string Password);
        private static readonly Dictionary<string, ActiveCred> ActiveCredentials = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object CredLock = new();

        public static bool Acquire(string target, string user, string password)
        {
            lock (CredLock)
            {
                if (ActiveCredentials.TryGetValue(target, out var active))
                {
                    if (string.Equals(active.Username, user, StringComparison.Ordinal) &&
                        string.Equals(active.Password, password, StringComparison.Ordinal))
                    {
                        ActiveCredentials[target] = active with { RefCount = active.RefCount + 1 };
                        return true;
                    }

                    // Different account requested for the same target: update Windows Credential Manager
                    if (WriteSessionCredential(target, user, password))
                    {
                        ActiveCredentials[target] = new ActiveCred(active.RefCount + 1, user, password);
                        return true;
                    }
                    return false;
                }

                if (WriteSessionCredential(target, user, password))
                {
                    ActiveCredentials[target] = new ActiveCred(1, user, password);
                    return true;
                }
                return false;
            }
        }

        public static void Release(string target)
        {
            lock (CredLock)
            {
                if (ActiveCredentials.TryGetValue(target, out var active))
                {
                    if (active.RefCount <= 1)
                    {
                        ActiveCredentials.Remove(target);
                        DeleteCredential(target);
                    }
                    else
                    {
                        ActiveCredentials[target] = active with { RefCount = active.RefCount - 1 };
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
                foreach (string target in ActiveCredentials.Keys)
                {
                    DeleteCredential(target);
                }
                ActiveCredentials.Clear();
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
