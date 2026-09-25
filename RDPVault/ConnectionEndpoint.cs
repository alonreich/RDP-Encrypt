using System.Net;
using System.Net.Sockets;

namespace RDPVault;

/// <summary>The separate Port field is authoritative, including for legacy host:port inputs.</summary>
public readonly record struct ConnectionEndpoint(string Host, int Port)
{
    public string Address => Host.Contains(':') ? $"[{Host}]:{Port}" : $"{Host}:{Port}";

    public static ConnectionEndpoint FromProfile(RdpProfile profile) => Parse(profile.Host, profile.Port);

    public static ConnectionEndpoint Parse(string input, int port = 3389)
    {
        if (!TryParse(input, out var endpoint, out string error, port))
            throw new ArgumentException(error);
        return endpoint;
    }

    public static bool TryParse(string? input, out ConnectionEndpoint endpoint, out string error, int port = 3389)
    {
        endpoint = default;
        error = "";

        if (string.IsNullOrWhiteSpace(input))
        {
            error = "Enter the host name or IP address to connect to.";
            return false;
        }

        string host = input.Trim();
        int embedded = 0;

        if (host.StartsWith('['))
        {
            int end = host.IndexOf(']');
            if (end < 0 || !IPAddress.TryParse(host[1..end], out var ip) || ip.AddressFamily != AddressFamily.InterNetworkV6)
            {
                error = "Enter a valid IPv6 address.";
                return false;
            }
            string suffix = host[(end + 1)..];
            if (suffix.Length > 0)
            {
                if (!suffix.StartsWith(':') || !int.TryParse(suffix[1..], out embedded) || embedded is < 1 or > 65535)
                {
                    error = "Enter the computer address and use the Port field for its port.";
                    return false;
                }
            }
            host = host[1..end];
        }
        else if (host.Count(c => c == ':') == 1)
        {
            int colon = host.LastIndexOf(':');
            if (!int.TryParse(host[(colon + 1)..], out embedded) || embedded is < 1 or > 65535)
            {
                error = "Enter the computer address and use the Port field for its port.";
                return false;
            }
            host = host[..colon];
        }

        int finalPort = port;
        if (finalPort is < 1 or > 65535)
        {
            finalPort = embedded > 0 ? embedded : 3389;
        }
        // If the separate port is valid (1..65535), it takes precedence over embedded port.

        if (string.IsNullOrWhiteSpace(host) || host.Any(char.IsWhiteSpace) || host.IndexOfAny(['/', '\\', '&', '?', '#', '"']) >= 0 || Uri.CheckHostName(host) == UriHostNameType.Unknown)
        {
            error = "Enter a valid computer name or IP address, without a URL or spaces.";
            return false;
        }

        endpoint = new ConnectionEndpoint(host, finalPort);
        return true;
    }

    /// <summary>
    /// Parses an RD Gateway authority string (e.g. "gw.example.com", "gw.example.com:8443", "[2001:db8::1]:8443").
    /// Preserves the embedded port when present, falling back to defaultPort (443) only when no port was specified.
    /// </summary>
    public static bool TryParseGatewayAuthority(string? input, out ConnectionEndpoint endpoint, out string error, int defaultPort = 443)
    {
        endpoint = default;
        error = "";

        if (string.IsNullOrWhiteSpace(input))
        {
            error = "Enter the gateway host name or IP address.";
            return false;
        }

        string host = input.Trim();
        int embedded = 0;

        if (host.StartsWith('['))
        {
            int end = host.IndexOf(']');
            if (end < 0 || !IPAddress.TryParse(host[1..end], out var ip) || ip.AddressFamily != AddressFamily.InterNetworkV6)
            {
                error = "Enter a valid IPv6 address.";
                return false;
            }
            string suffix = host[(end + 1)..];
            if (suffix.Length > 0)
            {
                if (!suffix.StartsWith(':') || !int.TryParse(suffix[1..], out embedded) || embedded is < 1 or > 65535)
                {
                    error = "Enter a valid gateway address and port (1-65535).";
                    return false;
                }
            }
            host = host[1..end];
        }
        else if (host.Count(c => c == ':') == 1)
        {
            int colon = host.LastIndexOf(':');
            if (!int.TryParse(host[(colon + 1)..], out embedded) || embedded is < 1 or > 65535)
            {
                error = "Enter a valid gateway address and port (1-65535).";
                return false;
            }
            host = host[..colon];
        }

        int finalPort = embedded > 0 ? embedded : defaultPort;

        if (string.IsNullOrWhiteSpace(host) || host.Any(char.IsWhiteSpace) || host.IndexOfAny(['/', '\\', '&', '?', '#', '"']) >= 0 || Uri.CheckHostName(host) == UriHostNameType.Unknown)
        {
            error = "Enter a valid computer name or IP address, without a URL or spaces.";
            return false;
        }

        endpoint = new ConnectionEndpoint(host, finalPort);
        return true;
    }

    /// <summary>
    /// Parses an endpoint as displayed in client UI without applying default port overrides.
    /// Preserves exact host and port tokens.
    /// Returns port = null when no port was specified in the text.
    /// Returns port = exact integer when a valid port (1..65535) was specified.
    /// </summary>
    public static bool TryParseDisplayedEndpoint(string? input, out string host, out int? port, out string error)
    {
        host = "";
        port = null;
        error = "";

        if (string.IsNullOrWhiteSpace(input))
        {
            error = "Input is empty.";
            return false;
        }

        string raw = input.Trim();
        while ((raw.StartsWith('\"') && raw.EndsWith('\"')) ||
               (raw.StartsWith('\'') && raw.EndsWith('\'')) ||
               (raw.StartsWith('(') && raw.EndsWith(')')))
        {
            raw = raw[1..^1].Trim();
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "Input is empty.";
            return false;
        }

        int embedded = 0;

        // IPv6 bracketed format: [2001:db8::1] or [2001:db8::1]:3389
        if (raw.StartsWith('['))
        {
            int end = raw.IndexOf(']');
            if (end < 0)
            {
                error = "Unmatched bracket in IPv6 address.";
                return false;
            }

            string ipPart = raw[1..end];
            if (!IPAddress.TryParse(ipPart, out var ip) || ip.AddressFamily != AddressFamily.InterNetworkV6)
            {
                error = "Invalid IPv6 address inside brackets.";
                return false;
            }

            string suffix = raw[(end + 1)..];
            if (suffix.Length > 0)
            {
                if (!suffix.StartsWith(':') || !int.TryParse(suffix[1..], out embedded) || embedded is < 1 or > 65535)
                {
                    error = "Invalid port following IPv6 address.";
                    return false;
                }
                port = embedded;
            }

            host = ipPart;
            return true;
        }

        // Unbracketed IPv6 address (e.g. 2001:db8::1)
        if (raw.Count(c => c == ':') > 1)
        {
            if (IPAddress.TryParse(raw, out var ip6) && ip6.AddressFamily == AddressFamily.InterNetworkV6)
            {
                host = raw;
                port = null;
                return true;
            }
            error = "Invalid IPv6 address.";
            return false;
        }

        // Single colon format: host:port
        if (raw.Count(c => c == ':') == 1)
        {
            int colon = raw.LastIndexOf(':');
            string hostPart = raw[..colon];
            string portPart = raw[(colon + 1)..];

            if (!int.TryParse(portPart, out embedded) || embedded is < 1 or > 65535)
            {
                error = "Invalid port in host:port string.";
                return false;
            }

            host = hostPart;
            port = embedded;
        }
        else
        {
            host = raw;
            port = null;
        }

        // Validate host syntax
        if (string.IsNullOrWhiteSpace(host) || host.Any(char.IsWhiteSpace) ||
            host.IndexOfAny(['/', '\\', '&', '?', '#', '"', '\'']) >= 0)
        {
            error = "Host contains invalid characters.";
            return false;
        }

        if (Uri.CheckHostName(host) == UriHostNameType.Unknown && !IPAddress.TryParse(host, out _))
        {
            error = "Host is not a valid hostname or IP address.";
            return false;
        }

        return true;
    }
}
