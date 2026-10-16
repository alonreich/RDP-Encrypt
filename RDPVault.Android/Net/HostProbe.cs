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

public static class HostProbe
{
    public static async Task<bool> IsReachableAsync(string host, int port, int timeoutMs, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeoutMs);

            var addresses = new List<IPAddress>();
            if (IPAddress.TryParse(host, out var directIp))
            {
                addresses.Add(directIp);
            }

            try
            {
                var resolved = await Dns.GetHostAddressesAsync(host, timeoutCts.Token).ConfigureAwait(false);
                foreach (var r in resolved)
                {
                    if (!addresses.Any(a => a.Equals(r)))
                    {
                        addresses.Add(r);
                    }
                }
            }
            catch
            {
            }

            foreach (var ip in addresses)
            {
                try
                {
                    using var socket = new Socket(ip.AddressFamily, System.Net.Sockets.SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    await socket.ConnectAsync(new IPEndPoint(ip, port), timeoutCts.Token).ConfigureAwait(false);
                    if (socket.Connected) return true;
                }
                catch
                {
                }
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsPrivateOrLocalHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;

        if (IPAddress.TryParse(host, out var ip))
        {
            if (IPAddress.IsLoopback(ip)) return true;
            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                byte[] b = ip.GetAddressBytes();
                if (b[0] == 10) return true;
                if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
                if (b[0] == 192 && b[1] == 168) return true;
                if (b[0] == 169 && b[1] == 254) return true;
                return false;
            }
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal) return true;
            return false;
        }

        if (host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".lan", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".home", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
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

    public static bool IsWolCapableNetwork(Context? context)
    {
        var kind = GetActiveNetworkKind(context);
        return kind == NetworkKind.Wifi || kind == NetworkKind.Ethernet;
    }

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
