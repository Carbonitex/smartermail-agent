using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SmarterMailAgent.Web;

namespace SmarterMailAgent.Tests;

/// <summary>
/// The per-IP limiters must not be steerable by a client-supplied header unless the request came
/// through a proxy we were told to trust.
/// </summary>
public sealed class ProxyTrustTests
{
    private const string Untrusted = "198.51.100.7";
    private const string Trusted = "10.1.2.3";

    private static DefaultHttpContext Request(string peer, string? xff = null, string? cf = null)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
        if (xff is not null) context.Request.Headers["X-Forwarded-For"] = xff;
        if (cf is not null) context.Request.Headers["CF-Connecting-IP"] = cf;
        return context;
    }

    /// <summary>The pipeline in Program.cs: remember the peer, apply forwarded headers, then key.</summary>
    private static async Task<string> KeyAfterPipeline(ProxyTrust trust, DefaultHttpContext context)
    {
        context.Items[ProxyTrust.PeerItem] = context.Connection.RemoteIpAddress;
        var options = new ForwardedHeadersOptions();
        trust.Configure(options);
        var middleware = new ForwardedHeadersMiddleware(_ => Task.CompletedTask, NullLoggerFactory.Instance, Options.Create(options));
        await middleware.Invoke(context);
        return trust.ClientKey(context);
    }

    [Fact]
    public async Task Defaults_ignore_every_forwarding_header()
    {
        var trust = ProxyTrust.Parse(null, null);
        Assert.Equal(Untrusted, await KeyAfterPipeline(trust, Request(Untrusted, xff: "1.2.3.4", cf: "5.6.7.8")));
        // Loopback is not trusted by default either (ASP.NET's own default would trust it).
        Assert.Equal("127.0.0.1", await KeyAfterPipeline(trust, Request("127.0.0.1", xff: "1.2.3.4")));
    }

    [Fact]
    public async Task Spoofed_headers_from_an_untrusted_peer_do_not_change_the_key()
    {
        var trust = ProxyTrust.Parse("10.0.0.0/8", "true");
        Assert.Equal(Untrusted, await KeyAfterPipeline(trust, Request(Untrusted, xff: "1.2.3.4")));
        Assert.Equal(Untrusted, await KeyAfterPipeline(trust, Request(Untrusted, cf: "5.6.7.8")));
        Assert.Equal(Untrusted, await KeyAfterPipeline(trust, Request(Untrusted, xff: "1.2.3.4", cf: "5.6.7.8")));
    }

    [Fact]
    public async Task A_trusted_peer_forwards_the_client_address()
    {
        var trust = ProxyTrust.Parse("10.0.0.0/8", null);
        Assert.Equal("1.2.3.4", await KeyAfterPipeline(trust, Request(Trusted, xff: "1.2.3.4")));
        // CF-Connecting-IP is off unless asked for.
        Assert.Equal("1.2.3.4", await KeyAfterPipeline(trust, Request(Trusted, xff: "1.2.3.4", cf: "5.6.7.8")));
    }

    [Fact]
    public async Task Cf_connecting_ip_is_used_only_when_enabled_and_the_peer_is_trusted()
    {
        var trust = ProxyTrust.Parse("10.0.0.0/8", "true");
        // The original peer decides, even though forwarded headers have already replaced RemoteIpAddress.
        Assert.Equal("5.6.7.8", await KeyAfterPipeline(trust, Request(Trusted, xff: "1.2.3.4", cf: "5.6.7.8")));
        Assert.Equal("5.6.7.8", await KeyAfterPipeline(trust, Request(Trusted, cf: "5.6.7.8")));
        // Garbage in the header falls back to the forwarded / peer address.
        Assert.Equal(Trusted, await KeyAfterPipeline(trust, Request(Trusted, cf: "not-an-ip")));
    }

    [Fact]
    public async Task A_dual_stack_peer_matches_an_ipv4_network()
    {
        var trust = ProxyTrust.Parse("172.18.0.0/16", "true");
        Assert.Equal("1.2.3.4", await KeyAfterPipeline(trust, Request("::ffff:172.18.0.5", xff: "1.2.3.4")));
        Assert.Equal("5.6.7.8", await KeyAfterPipeline(trust, Request("::ffff:172.18.0.5", cf: "5.6.7.8")));
    }

    [Fact]
    public void Parse_accepts_addresses_and_cidrs_and_rejects_typos()
    {
        var trust = ProxyTrust.Parse(" 10.0.0.0/8 , 192.0.2.1,2001:db8::/32 ", null);
        Assert.Equal(3, trust.Networks.Count);
        Assert.True(trust.IsTrusted(IPAddress.Parse("192.0.2.1")));
        Assert.False(trust.IsTrusted(IPAddress.Parse("192.0.2.2")));
        Assert.True(trust.IsTrusted(IPAddress.Parse("2001:db8::1")));
        Assert.False(trust.TrustCfConnectingIp);

        Assert.Throws<InvalidOperationException>(() => ProxyTrust.Parse("10.0.0/8x", null));
        Assert.Throws<InvalidOperationException>(() => ProxyTrust.Parse("proxy.local", null));
    }
}
