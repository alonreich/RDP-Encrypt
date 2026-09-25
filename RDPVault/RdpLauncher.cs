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
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia.Threading;
using Microsoft.Win32;

namespace RDPVault;

public sealed class ActiveSessionInfo
{
    public Process Process { get; }
    public RdpProfile Profile { get; }
    public string ProfileId { get; }
    public string ProfileName { get; }
    public string Host { get; }
    public int Port { get; }
    public string? TempRdpPath { get; }
    public DateTime StartedAt { get; }

    public ActiveSessionInfo(Process process, RdpProfile profile, string? tempRdpPath = null)
    {
        Process = process;
        Profile = profile;
        ProfileId = profile.Id;
        ProfileName = profile.Name;
        Host = profile.Host;
        Port = profile.Port;
        TempRdpPath = tempRdpPath;
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

    public static HashSet<string> GetLiveHosts()
    {
        lock (Gate)
        {
            LiveSessions.RemoveAll(s => s.HasExited);
            var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in LiveSessions)
            {
                if (!string.IsNullOrWhiteSpace(s.Host))
                {
                    hosts.Add(s.Host.Trim());
                }
            }
            return hosts;
        }
    }

    public static HashSet<string> GetLiveTempFiles()
    {
        lock (Gate)
        {
            LiveSessions.RemoveAll(s => s.HasExited);
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in LiveSessions)
            {
                if (!string.IsNullOrEmpty(s.TempRdpPath))
                {
                    files.Add(s.TempRdpPath);
                }
            }
            return files;
        }
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
                if (cleanHost.Contains(':') && !cleanHost.Contains('['))
                {
                    var parts = cleanHost.Split(':');
                    cleanHost = parts[0];
                }
                else if (cleanHost.StartsWith('[') && cleanHost.Contains(']'))
                {
                    int end = cleanHost.IndexOf(']');
                    cleanHost = cleanHost[1..end];
                }

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
                    try { boundClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, 1); } catch { }

                    var destsForNic = new List<IPAddress>();
                    if (subnetBcast != null) destsForNic.Add(subnetBcast);
                    destsForNic.Add(IPAddress.Broadcast);

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
            try
            {
                using var generalClient = new UdpClient();
                generalClient.EnableBroadcast = true;
                try { generalClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, 1); } catch { }
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
            catch { }

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

        // 1. Port Knocking handling (Stealth knock opens firewall address list before WOL and RDP)
        if (p.EnableIcmpKnock)
        {
            bool isTcp = string.Equals(p.KnockProtocol, "TCP", StringComparison.OrdinalIgnoreCase);
            int delaySec = p.KnockDelaySeconds >= 0 ? p.KnockDelaySeconds : 2;
            progress?.Invoke(new LaunchProgressUpdate
            {
                Step = "Port Knocking",
                Details = isTcp
                    ? $"Sending TCP knock to {p.Host}:{p.KnockTcpPort}; waiting {delaySec}s before connecting."
                    : $"Sending ICMP magic packet to {p.Host}; waiting {delaySec}s before connecting.",
                IsIndeterminate = true
            });
            try { await IcmpKnock.SendBeforeConnectAsync(p, IcmpKnock.SendWindowsAsync, ct); }
            catch (OperationCanceledException) { LaunchFailed?.Invoke("Connection cancelled."); return false; }
            catch (Exception ex) { LaunchFailed?.Invoke("Port knock could not be sent: " + ex.Message); return false; }
        }

        if (ct.IsCancellationRequested)
        {
            LaunchFailed?.Invoke("Connection cancelled by user.");
            return false;
        }

        // 2. Wake-on-LAN handling (Magic packet can now pass through the opened firewall)
        if (p.EnableWol && !string.IsNullOrWhiteSpace(p.WolMacAddress))
        {
            SessionStarted?.Invoke($"{p.Name} (Sending Wake-on-LAN magic packet...)");
            progress?.Invoke(new LaunchProgressUpdate
            {
                Step = "Wake-on-LAN Dispatch",
                Details = $"Preparing magic packet for MAC {p.WolMacAddress} (Target Host: {p.Host})...",
                IsIndeterminate = true
            });

            string wolTargetHost = ConnectionEndpoint.FromProfile(p).Host;
            var (wolOk, report) = await SendWakeOnLanAsync(
                p.WolMacAddress,
                host: wolTargetHost,
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

        // Reference-counted session credentials with isolated leases to avoid cross-session collisions
        var credLeases = new List<(string Target, string LeaseId)>();
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
                var acquireResult = SessionCredentialCoordinator.Acquire(target, p.Username, p.Password, out string leaseId);
                if (acquireResult == SessionCredentialCoordinator.CredentialAcquireResult.Success)
                {
                    credLeases.Add((target, leaseId));
                }
                else
                {
                    // Full rollback and immediate connection abort
                    foreach (var (t, l) in credLeases) SessionCredentialCoordinator.Release(t, l);
                    TryDelete(tempRdp);

                    if (acquireResult == SessionCredentialCoordinator.CredentialAcquireResult.Conflict)
                    {
                        LaunchFailed?.Invoke($"Another active session to target '{target}' is using different credentials. Connection aborted to prevent hijacking.");
                    }
                    else
                    {
                        LaunchFailed?.Invoke($"Failed to write session credentials for target '{target}' to Windows Credential Manager.");
                    }
                    return false;
                }
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
            foreach (var (t, l) in credLeases) SessionCredentialCoordinator.Release(t, l);
            RemoveCertPin(p);
            LaunchFailed?.Invoke($"Windows could not start Remote Desktop: {ex.Message}");
            return false;
        }
        if (proc == null)
        {
            TryDelete(tempRdp);
            foreach (var (t, l) in credLeases) SessionCredentialCoordinator.Release(t, l);
            RemoveCertPin(p);
            LaunchFailed?.Invoke("Windows could not start Remote Desktop.");
            return false;
        }

        proc.EnableRaisingEvents = true;
        var sessionInfo = new ActiveSessionInfo(proc, p, tempRdp);
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
            foreach (var (t, l) in credLeases) SessionCredentialCoordinator.Release(t, l);

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

    /// <summary>
    /// Probes whether the RDP destination endpoint on its specified destination port (custom or standard)
    /// is accepting connections and verifies that the mstsc process remains active.
    /// </summary>
    public static async Task<bool> ProbeRdpConnectionAsync(string host, int port, Process proc, System.Threading.CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(12);
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            if (proc.HasExited)
                return false;

            try
            {
                using var tcp = new TcpClient();
                using var connectCts = new System.Threading.CancellationTokenSource(TimeSpan.FromMilliseconds(800));
                using var linkedCts = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(ct, connectCts.Token);
                await tcp.ConnectAsync(host, port, linkedCts.Token).ConfigureAwait(false);
                if (tcp.Connected)
                {
                    // Destination port is accepting connections.
                    // Wait a short moment to ensure mstsc doesn't exit immediately on auth/cert failure
                    await Task.Delay(1200, ct).ConfigureAwait(false);
                    return !proc.HasExited;
                }
            }
            catch
            {
                // Port probe attempt did not connect yet
            }

            try { await Task.Delay(350, ct).ConfigureAwait(false); } catch { break; }
        }

        return !proc.HasExited;
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
        if (!string.IsNullOrWhiteSpace(p.GatewayHost) &&
            ConnectionEndpoint.TryParseGatewayAuthority(p.GatewayHost, out var gwEp, out _))
        {
            sb.AppendLine("gatewayhostname:s:" + gwEp.Address);
            sb.AppendLine("gatewayusagemethod:i:1");
            sb.AppendLine("gatewaycredentialssource:i:4");
            sb.AppendLine("gatewayprofileusagemethod:i:1");
        }
        else
        {
            sb.AppendLine("gatewayhostname:s:" + p.GatewayHost);
            sb.AppendLine("gatewayusagemethod:i:" + (string.IsNullOrEmpty(p.GatewayHost) ? 0 : 1));
            sb.AppendLine("gatewaycredentialssource:i:4");
            sb.AppendLine("gatewayprofileusagemethod:i:" + (string.IsNullOrEmpty(p.GatewayHost) ? 0 : 1));
        }
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

    public record PendingCertPinUpdate(string ProfileId, string ExpectedEndpoint, string CertThumbprint, DateTime TimestampUtc);
    private static readonly List<PendingCertPinUpdate> PendingCertUpdates = new();
    private static readonly object CertUpdatesLock = new();

    private static void SavePendingCertPinsToDisk()
    {
        lock (CertUpdatesLock)
        {
            try
            {
                if (PendingCertUpdates.Count == 0)
                {
                    if (File.Exists(AppPaths.PendingCertsPath))
                        File.Delete(AppPaths.PendingCertsPath);
                    return;
                }

                string? dir = Path.GetDirectoryName(AppPaths.PendingCertsPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                string json = JsonSerializer.Serialize(PendingCertUpdates);
                byte[] enc = VaultCrypto.ProtectLocalData(Encoding.UTF8.GetBytes(json));
                File.WriteAllBytes(AppPaths.PendingCertsPath, enc);
            }
            catch { }
        }
    }

    private static void LoadPendingCertPinsFromDisk()
    {
        lock (CertUpdatesLock)
        {
            try
            {
                if (!File.Exists(AppPaths.PendingCertsPath)) return;
                byte[] enc = File.ReadAllBytes(AppPaths.PendingCertsPath);
                byte[]? plain = VaultCrypto.UnprotectLocalData(enc);
                if (plain == null) return;
                string json = Encoding.UTF8.GetString(plain);
                var loaded = JsonSerializer.Deserialize<List<PendingCertPinUpdate>>(json);
                if (loaded != null)
                {
                    foreach (var item in loaded)
                    {
                        if (!PendingCertUpdates.Any(x => x.ProfileId == item.ProfileId && string.Equals(x.ExpectedEndpoint, item.ExpectedEndpoint, StringComparison.OrdinalIgnoreCase)))
                        {
                            PendingCertUpdates.Add(item);
                        }
                    }
                }
            }
            catch { }
        }
    }

    public static void SnapshotActiveCertPins()
    {
        List<RdpProfile> activeProfiles;
        lock (Gate)
        {
            LiveSessions.RemoveAll(s => s.HasExited);
            activeProfiles = LiveSessions.Select(s => s.Profile).ToList();
        }

        if (activeProfiles.Count == 0) return;

        bool hasNew = false;
        lock (CertUpdatesLock)
        {
            LoadPendingCertPinsFromDisk();
            foreach (var p in activeProfiles)
            {
                if (p.AllowUnverifiedServer) continue;
                try
                {
                    using var key = Registry.CurrentUser.OpenSubKey($@"{TscServersKey}\{FullAddress(p)}");
                    if (key?.GetValue("CertHash") is byte[] b && b.Length > 0)
                    {
                        string seen = Convert.ToHexString(b);
                        if (!string.IsNullOrEmpty(seen) && !string.Equals(seen, p.CertThumbprint, StringComparison.OrdinalIgnoreCase))
                        {
                            string launchEp = ConnectionEndpoint.FromProfile(p).ToString();
                            PendingCertUpdates.RemoveAll(u => u.ProfileId == p.Id);
                            PendingCertUpdates.Add(new PendingCertPinUpdate(p.Id, launchEp, seen, DateTime.UtcNow));
                            hasNew = true;
                        }
                    }
                }
                catch { }
            }

            if (hasNew)
            {
                SavePendingCertPinsToDisk();
            }
        }
    }

    public static void ApplyPendingCertUpdates(VaultPayload? payload)
    {
        if (payload?.Profiles == null) return;
        LoadPendingCertPinsFromDisk();

        var applied = new List<PendingCertPinUpdate>();
        lock (CertUpdatesLock)
        {
            if (PendingCertUpdates.Count == 0) return;
            foreach (var update in PendingCertUpdates)
            {
                var target = payload.Profiles.FirstOrDefault(x => x.Id == update.ProfileId);
                if (target != null)
                {
                    string targetEndpoint = ConnectionEndpoint.FromProfile(target).ToString();
                    if (string.Equals(targetEndpoint, update.ExpectedEndpoint, StringComparison.OrdinalIgnoreCase))
                    {
                        target.CertThumbprint = update.CertThumbprint;
                        applied.Add(update);
                    }
                }
            }
        }

        if (applied.Count > 0)
        {
            // Atomically save to the vault file first. ONLY if save succeeds do we purge the applied updates!
            bool saved = false;
            try
            {
                saved = SessionManager.Current.TrySave(out _);
            }
            catch { }

            if (saved)
            {
                lock (CertUpdatesLock)
                {
                    foreach (var app in applied)
                    {
                        PendingCertUpdates.Remove(app);
                    }
                    SavePendingCertPinsToDisk();
                }
            }
        }
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
        string launchEndpoint = ConnectionEndpoint.FromProfile(p).ToString();

        // This runs on a background task when mstsc exits. Every other vault save
        // happens on the UI thread from a user action, and VaultCrypto.WriteAtomic
        // shares one .tmp path - so hop to the UI thread instead of racing them.
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                var mgr = SessionManager.Current;
                if (!mgr.IsUnlocked || mgr.Payload == null)
                {
                    // Locked since; preserve pending certificate update safely across vault locking to encrypted store
                    lock (CertUpdatesLock)
                    {
                        PendingCertUpdates.RemoveAll(u => u.ProfileId == p.Id);
                        PendingCertUpdates.Add(new PendingCertPinUpdate(p.Id, launchEndpoint, thumb, DateTime.UtcNow));
                        SavePendingCertPinsToDisk();
                    }
                    return;
                }

                var target = mgr.Payload.Profiles.FirstOrDefault(x => x.Id == p.Id);
                if (target == null) return;  // deleted or edited away

                // Before applying approval, verify that the current profile's normalized endpoint
                // still equals the launch snapshot's endpoint.
                string currentEndpoint = ConnectionEndpoint.FromProfile(target).ToString();
                if (!string.Equals(currentEndpoint, launchEndpoint, StringComparison.OrdinalIgnoreCase))
                    return;

                target.CertThumbprint = thumb;
                p.CertThumbprint = thumb;
                if (!mgr.TrySave(out _))
                {
                    lock (CertUpdatesLock)
                    {
                        PendingCertUpdates.RemoveAll(u => u.ProfileId == p.Id);
                        PendingCertUpdates.Add(new PendingCertPinUpdate(p.Id, launchEndpoint, thumb, DateTime.UtcNow));
                        SavePendingCertPinsToDisk();
                    }
                }
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

    // ---------------- session credential coordinator (lease-based & isolated) ----------------

    private static class SessionCredentialCoordinator
    {
        private sealed class ActiveCred
        {
            public string Username { get; }
            public byte[] CredHash { get; }
            public HashSet<string> LeaseIds { get; } = new(StringComparer.Ordinal);

            public ActiveCred(string username, byte[] credHash, string initialLeaseId)
            {
                Username = username;
                CredHash = credHash;
                LeaseIds.Add(initialLeaseId);
            }
        }

        private static readonly Dictionary<string, ActiveCred> ActiveCredentials = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object CredLock = new();

        public enum CredentialAcquireResult
        {
            Success,
            Conflict,
            WriteFailed
        }

        public static CredentialAcquireResult Acquire(string target, string user, string password, out string leaseId)
        {
            leaseId = "";
            byte[]? inputHash = SHA256.HashData(Encoding.UTF8.GetBytes(user + "\0" + password));
            try
            {
                lock (CredLock)
                {
                    if (ActiveCredentials.TryGetValue(target, out var active))
                    {
                        if (string.Equals(active.Username, user, StringComparison.Ordinal) &&
                            CryptographicOperations.FixedTimeEquals(active.CredHash, inputHash))
                        {
                            string newLease = Guid.NewGuid().ToString("N");
                            active.LeaseIds.Add(newLease);
                            leaseId = newLease;
                            return CredentialAcquireResult.Success;
                        }

                        // Conflict: Another active session holds a lease on this target with different credentials.
                        // Reject injection so running sessions are not hijacked.
                        return CredentialAcquireResult.Conflict;
                    }

                    if (WriteSessionCredential(target, user, password))
                    {
                        string newLease = Guid.NewGuid().ToString("N");
                        ActiveCredentials[target] = new ActiveCred(user, inputHash, newLease);
                        leaseId = newLease;
                        inputHash = null; // ownership transferred to ActiveCred
                        return CredentialAcquireResult.Success;
                    }
                    return CredentialAcquireResult.WriteFailed;
                }
            }
            finally
            {
                if (inputHash != null)
                {
                    CryptographicOperations.ZeroMemory(inputHash);
                }
            }
        }

        public static void Release(string target, string leaseId)
        {
            if (string.IsNullOrEmpty(leaseId)) return;
            lock (CredLock)
            {
                if (ActiveCredentials.TryGetValue(target, out var active))
                {
                    active.LeaseIds.Remove(leaseId);
                    if (active.LeaseIds.Count == 0)
                    {
                        CryptographicOperations.ZeroMemory(active.CredHash);
                        ActiveCredentials.Remove(target);
                        DeleteCredential(target);
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
                foreach (var (target, active) in ActiveCredentials)
                {
                    CryptographicOperations.ZeroMemory(active.CredHash);
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
