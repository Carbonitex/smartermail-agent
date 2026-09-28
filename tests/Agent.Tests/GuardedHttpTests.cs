using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using SmarterMailAgent.Auth;
using SmarterMailMcp.Core.Models;

namespace SmarterMailAgent.Tests;

/// <summary>
/// HostGuard vets the hostname at sign-in; GuardedHttp re-vets the address at every connect, so a
/// name that later resolves somewhere private (DNS rebinding) is still refused, and a redirect cannot
/// point the request somewhere else.
/// </summary>
public sealed class GuardedHttpTests
{
    private static Func<string, CancellationToken, Task<IPAddress[]>> Resolves(params string[] addresses) =>
        (_, _) => Task.FromResult(addresses.Select(IPAddress.Parse).ToArray());

    /// <summary>A one-shot HTTP server on loopback that answers every request with <paramref name="response"/>.</summary>
    private static (TcpListener Listener, Task Serve) Serve(string response)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var serve = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    using var client = await listener.AcceptTcpClientAsync();
                    var stream = client.GetStream();
                    var buffer = new byte[4096];
                    _ = await stream.ReadAsync(buffer);
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(response));
                }
            }
            catch (Exception) { /* listener stopped */ }
        });
        return (listener, serve);
    }

    private static int PortOf(TcpListener listener) => ((IPEndPoint)listener.LocalEndpoint).Port;

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.5")]
    [InlineData("169.254.169.254")]
    [InlineData("::1")]
    public async Task Private_addresses_are_refused_at_connect(string address)
    {
        await Assert.ThrowsAsync<GuardedHttp.BlockedAddressException>(async () =>
            await GuardedHttp.ConnectAsync(new DnsEndPoint("mail.example.com", 443), allowPrivateHosts: false,
                Resolves(address), CancellationToken.None));
    }

    [Fact]
    public async Task One_private_answer_among_public_ones_is_enough_to_refuse()
    {
        await Assert.ThrowsAsync<GuardedHttp.BlockedAddressException>(async () =>
            await GuardedHttp.ConnectAsync(new DnsEndPoint("mail.example.com", 443), allowPrivateHosts: false,
                Resolves("93.184.216.34", "127.0.0.1"), CancellationToken.None));
    }

    [Fact]
    public async Task A_rebound_name_is_refused_even_though_sign_in_would_have_passed_it()
    {
        // HostGuard saw a public address at sign-in; by the time the request connects the name
        // resolves to loopback, where something is listening.
        var (listener, _) = Serve("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        try
        {
            using var http = new HttpClient(GuardedHttp.CreateHandler(() => false, Resolves("127.0.0.1")));
            var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
                http.GetAsync($"http://rebind.example:{PortOf(listener)}/"));
            Assert.IsType<GuardedHttp.BlockedAddressException>(error.InnerException);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Allow_private_hosts_connects_and_redirects_are_not_followed()
    {
        var (listener, _) = Serve(
            "HTTP/1.1 302 Found\r\nLocation: http://169.254.169.254/latest/meta-data/\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        try
        {
            using var http = new HttpClient(GuardedHttp.CreateHandler(() => true));
            using var response = await http.GetAsync($"http://127.0.0.1:{PortOf(listener)}/api/v1/settings");
            Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task The_per_account_tool_client_is_guarded()
    {
        if (HostGuard.AllowPrivateHosts)
            return;   // the guard is deliberately off in this environment

        var (listener, _) = Serve("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        try
        {
            var tokenData = new TokenData
            {
                AccessToken = "a",
                RefreshToken = "r",
                BaseUrl = $"http://127.0.0.1:{PortOf(listener)}",
                Username = "alice@example.com",
            };
            var globalContext = new GlobalContext(
                Path.Combine(Path.GetTempPath(), $"sma-never-{Guid.NewGuid():N}.json"), readOnlyMode: true);
            var userContext = UserContextFactory.Create(globalContext, tokenData);
            var http = (HttpClient)typeof(UserContext)
                .GetField("_httpClient", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(userContext)!;

            var error = await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync("/api/v1/settings"));
            Assert.IsType<GuardedHttp.BlockedAddressException>(error.InnerException);
        }
        finally
        {
            listener.Stop();
        }
    }
}
