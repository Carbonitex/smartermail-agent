using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using IPNetwork = System.Net.IPNetwork;

namespace SmarterMailAgent.Web;

/// <summary>
/// Which reverse proxies may tell us the client's address. The per-IP rate limiters key off that
/// address, so trusting a header from anyone would let any client pick its own bucket.
/// <list type="bullet">
/// <item><c>TRUSTED_PROXIES</c>: comma-separated IPs or CIDRs (default none). Only a request whose
/// immediate peer is in the list has its <c>X-Forwarded-For</c> / <c>X-Forwarded-Proto</c> applied.</item>
/// <item><c>TRUST_CF_CONNECTING_IP</c>: <c>true</c> to key the limiters off Cloudflare's
/// <c>CF-Connecting-IP</c> (default false), and still only when the peer is a trusted proxy.</item>
/// </list>
/// With neither set the limiters use the TCP peer address, which is right when nothing sits in front.
/// </summary>
public sealed class ProxyTrust
{
    /// <summary><c>HttpContext.Items</c> key for the TCP peer, captured before forwarded headers apply.</summary>
    public const string PeerItem = "sma.peer";

    private readonly List<IPNetwork> _networks;

    public ProxyTrust(IEnumerable<IPNetwork> networks, bool trustCfConnectingIp)
    {
        _networks = [.. networks];
        TrustCfConnectingIp = trustCfConnectingIp;
    }

    public IReadOnlyList<IPNetwork> Networks => _networks;
    public bool TrustCfConnectingIp { get; }

    /// <summary>Unparseable entries throw: a typo here would silently disable the proxy.</summary>
    public static ProxyTrust Parse(string? trustedProxies, string? trustCf)
    {
        var networks = new List<IPNetwork>();
        foreach (var raw in (trustedProxies ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (IPNetwork.TryParse(raw, out var network))
            {
                networks.Add(network);
            }
            else if (IPAddress.TryParse(raw, out var address))
            {
                address = Normalize(address);
                networks.Add(new IPNetwork(address, address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128));
            }
            else
            {
                throw new InvalidOperationException($"TRUSTED_PROXIES: '{raw}' is not an IP address or CIDR.");
            }
        }
        return new ProxyTrust(networks, trustCf?.Equals("true", StringComparison.OrdinalIgnoreCase) == true);
    }

    public bool IsTrusted(IPAddress? address)
    {
        if (address is null) return false;
        address = Normalize(address);
        foreach (var network in _networks)
        {
            if (network.Contains(address)) return true;
        }
        return false;
    }

    /// <summary>
    /// Replaces ASP.NET's defaults (loopback) with exactly the configured networks. IPv4 networks are
    /// also added in their IPv4-mapped IPv6 form, which is how a dual-stack socket reports the peer.
    /// With no networks, forwarded headers are switched off entirely: the middleware treats empty
    /// <c>KnownProxies</c> and <c>KnownIPNetworks</c> as "trust every peer", not "trust none".
    /// </summary>
    public void Configure(ForwardedHeadersOptions options)
    {
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
        options.ForwardedHeaders = _networks.Count == 0
            ? ForwardedHeaders.None
            : ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        foreach (var network in _networks)
        {
            options.KnownIPNetworks.Add(network);
            if (network.BaseAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                options.KnownIPNetworks.Add(new IPNetwork(network.BaseAddress.MapToIPv6(), network.PrefixLength + 96));
        }
    }

    /// <summary>
    /// The rate-limiter key. <c>RemoteIpAddress</c> already reflects <c>X-Forwarded-For</c> when (and
    /// only when) the peer is trusted; <c>CF-Connecting-IP</c> is used only when it is switched on and
    /// the original TCP peer is a trusted proxy.
    /// </summary>
    public string ClientKey(HttpContext context)
    {
        if (TrustCfConnectingIp &&
            IsTrusted(context.Items[PeerItem] as IPAddress ?? context.Connection.RemoteIpAddress) &&
            IPAddress.TryParse(context.Request.Headers["CF-Connecting-IP"].FirstOrDefault(), out var cf))
        {
            return Normalize(cf).ToString();
        }
        return context.Connection.RemoteIpAddress is { } remote ? Normalize(remote).ToString() : "unknown";
    }

    private static IPAddress Normalize(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}
