using System.Net;
using System.Net.Sockets;

namespace SmarterMailAgent.Auth;

/// <summary>
/// SSRF guard for the user-supplied SmarterMail hostname.
/// Resolves the host and rejects loopback / RFC1918 / link-local / CGNAT / IPv6 ULA
/// unless ALLOW_PRIVATE_HOSTS=true. Scheme is forced to https:// unless that flag is set.
/// </summary>
public static class HostGuard
{
    public static bool AllowPrivateHosts =>
        Environment.GetEnvironmentVariable("ALLOW_PRIVATE_HOSTS")?
            .Equals("true", StringComparison.OrdinalIgnoreCase) == true;

    public sealed record Result(bool Ok, string? BaseUrl, string? Error);

    /// <summary>
    /// Normalises whatever the user typed ("mail.example.com", "https://mail.example.com:9998/")
    /// into a base URL with no trailing slash, after validating scheme, port and resolved IPs.
    /// </summary>
    public static async Task<Result> ValidateAsync(string? hostname, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(hostname))
            return new Result(false, null, "Hostname is required.");

        var raw = hostname.Trim();
        if (!raw.Contains("://", StringComparison.Ordinal))
            raw = (AllowPrivateHosts ? "http://" : "https://") + raw;

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
            return new Result(false, null, "Hostname is not a valid URL.");

        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            return new Result(false, null, "Only http and https are supported.");

        if (uri.Scheme == Uri.UriSchemeHttp && !AllowPrivateHosts)
            return new Result(false, null, "Only https:// is allowed.");

        if (!string.IsNullOrEmpty(uri.UserInfo))
            return new Result(false, null, "Credentials in the URL are not allowed.");

        var host = uri.DnsSafeHost;
        if (string.IsNullOrEmpty(host))
            return new Result(false, null, "Hostname is not a valid URL.");

        if (!AllowPrivateHosts)
        {
            // "localhost" and friends never resolve publicly; block by name too.
            if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase))
            {
                return new Result(false, null, "That host is not allowed.");
            }

            IPAddress[] addresses;
            if (IPAddress.TryParse(host, out var literal))
            {
                addresses = [literal];
            }
            else
            {
                try
                {
                    addresses = await Dns.GetHostAddressesAsync(host, ct);
                }
                catch (Exception)
                {
                    return new Result(false, null, "Could not resolve that hostname.");
                }
            }

            if (addresses.Length == 0)
                return new Result(false, null, "Could not resolve that hostname.");

            foreach (var address in addresses)
            {
                if (IsBlocked(address))
                    return new Result(false, null, "That host is not allowed.");
            }
        }

        var builder = new UriBuilder(uri.Scheme, uri.Host, uri.IsDefaultPort ? -1 : uri.Port);
        var baseUrl = builder.Uri.ToString().TrimEnd('/');
        return new Result(true, baseUrl, null);
    }

    public static bool IsBlocked(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv4MappedToIPv6)
                return IsBlocked(address.MapToIPv4());

            if (IPAddress.IsLoopback(address)) return true;                 // ::1
            if (address.Equals(IPAddress.IPv6Any)) return true;             // ::
            if (address.IsIPv6LinkLocal) return true;                       // fe80::/10
            if (address.IsIPv6SiteLocal) return true;                       // fec0::/10 (deprecated)
            if (address.IsIPv6Multicast) return true;

            var b6 = address.GetAddressBytes();
            if ((b6[0] & 0xFE) == 0xFC) return true;                        // fc00::/7 ULA
            if (b6[0] == 0x20 && b6[1] == 0x01 && b6[2] == 0x00 && b6[3] == 0x00) return true; // 2001:0::/32 Teredo
            if (b6[0] == 0x20 && b6[1] == 0x02) return true;                // 2002::/16 6to4
            return false;
        }

        if (address.AddressFamily != AddressFamily.InterNetwork)
            return true;

        var b = address.GetAddressBytes();
        if (b[0] == 0) return true;                                         // 0.0.0.0/8
        if (b[0] == 10) return true;                                        // 10/8
        if (b[0] == 127) return true;                                       // loopback
        if (b[0] == 169 && b[1] == 254) return true;                        // link-local (incl. 169.254.169.254)
        if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;           // 172.16/12
        if (b[0] == 192 && b[1] == 168) return true;                        // 192.168/16
        if (b[0] == 192 && b[1] == 0 && b[2] == 0) return true;             // 192.0.0/24
        if (b[0] == 192 && b[1] == 0 && b[2] == 2) return true;             // TEST-NET-1
        if (b[0] == 198 && (b[1] == 18 || b[1] == 19)) return true;         // benchmarking
        if (b[0] == 198 && b[1] == 51 && b[2] == 100) return true;          // TEST-NET-2
        if (b[0] == 203 && b[1] == 0 && b[2] == 113) return true;           // TEST-NET-3
        if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return true;          // CGNAT 100.64/10
        if (b[0] >= 224) return true;                                       // multicast + reserved + broadcast
        return false;
    }
}
