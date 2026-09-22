using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Android.Content;
using Android.Net;

namespace RDPVault.Android.Net;

public enum NetworkKind
{
    None,
    Wifi,
    Ethernet,
    Cellular,
    Other
}

/// <summary>
/// Pre-flight network checks performed BEFORE handing the session to an external RDP
/// client, so the user is told "your PC is asleep" by RDP Vault instead of staring at
/// an opaque 0x204 spinner inside Microsoft Remote Desktop 30 seconds later.
/// </summary>
public static class HostProbe
{
    /// <summary>
    /// Attempts a TCP connect to host:port. Returns true if the port accepted the
    /// connection within the timeout. Never throws.
    /// </summary>
    public static async Task<bool> IsReachableAsync(string host, int port, int timeoutMs, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;

        try
        {
            using var client = new TcpClient { NoDelay = true };
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeoutMs);

            try
            {
                await client.ConnectAsync(host, port, timeoutCts.Token).ConfigureAwait(false);
                return client.Connected;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }
        catch
        {
            return false;
        }
    }

    public static NetworkKind GetActiveNetworkKind(Context? context)
    {
        try
        {
            var cm = (ConnectivityManager?)context?.GetSystemService(Context.ConnectivityService);
            var active = cm?.ActiveNetwork;
            if (cm == null || active == null) return NetworkKind.None;

            var caps = cm.GetNetworkCapabilities(active);
            if (caps == null) return NetworkKind.None;

            if (caps.HasTransport(TransportType.Wifi)) return NetworkKind.Wifi;
            if (caps.HasTransport(TransportType.Ethernet)) return NetworkKind.Ethernet;
            if (caps.HasTransport(TransportType.Cellular)) return NetworkKind.Cellular;
            return NetworkKind.Other;
        }
        catch
        {
            return NetworkKind.None;
        }
    }

    /// <summary>
    /// True when Wake-on-LAN has any chance of working: WOL magic packets are
    /// link-local, so they only reach the target over Wi-Fi / Ethernet, never over
    /// mobile data.
    /// </summary>
    public static bool IsWolCapableNetwork(Context? context)
    {
        var kind = GetActiveNetworkKind(context);
        return kind == NetworkKind.Wifi || kind == NetworkKind.Ethernet;
    }

    /// <summary>
    /// Computes the subnet-directed broadcast address(es) for the device's current
    /// IPv4 interfaces (e.g. 192.168.1.255 for 192.168.1.37/24).
    ///
    /// Android silently drops or fails to route 255.255.255.255 on many OEM builds,
    /// which is why a plain limited broadcast is not enough on its own.
    /// </summary>
    public static List<IPAddress> GetDirectedBroadcastAddresses()
    {
        var results = new List<IPAddress>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (IPAddress.IsLoopback(ua.Address)) continue;

                    byte[] addr = ua.Address.GetAddressBytes();
                    byte[] mask = ua.IPv4Mask?.GetAddressBytes() ?? new byte[] { 255, 255, 255, 0 };
                    if (mask.Length != 4) mask = new byte[] { 255, 255, 255, 0 };

                    var bcast = new byte[4];
                    for (int i = 0; i < 4; i++)
                    {
                        bcast[i] = (byte)(addr[i] | (byte)~mask[i]);
                    }

                    var ip = new IPAddress(bcast);
                    if (!results.Any(r => r.Equals(ip))) results.Add(ip);
                }
            }
        }
        catch { }

        return results;
    }
}
