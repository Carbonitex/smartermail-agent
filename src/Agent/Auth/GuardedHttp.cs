using System.Net;
using System.Net.Sockets;

namespace SmarterMailAgent.Auth;

/// <summary>
/// The only way this service opens a connection to a user-supplied mail server. <see cref="HostGuard"/>
/// checks the hostname when the user signs in, but DNS can answer differently a second later (DNS
/// rebinding), so the <c>ConnectCallback</c> here resolves again at connect time, refuses if **any**
/// address is one <see cref="HostGuard.IsBlocked"/> rejects, and connects to the vetted addresses
/// only. Redirects are never followed (a 30x to an internal URL would sidestep the check), and no
/// proxy is used (the check would then vet the proxy instead of the mail server).
/// </summary>
public static class GuardedHttp
{
    /// <summary>Thrown from the connect callback; <see cref="HttpClient"/> wraps it in an <see cref="HttpRequestException"/>.</summary>
    public sealed class BlockedAddressException(string host)
        : IOException($"Refusing to connect to {host}: it resolves to a private or reserved address.");

    /// <summary>
    /// A handler for requests to user-supplied servers. <paramref name="allowPrivateHosts"/> is read
    /// on every connect (default: <see cref="HostGuard.AllowPrivateHosts"/>); tests pass their own.
    /// </summary>
    public static SocketsHttpHandler CreateHandler(
        Func<bool>? allowPrivateHosts = null,
        Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null)
    {
        var allow = allowPrivateHosts ?? (() => HostGuard.AllowPrivateHosts);
        var dns = resolve ?? ((host, ct) => Dns.GetHostAddressesAsync(host, ct));
        return new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AllowAutoRedirect = false,
            UseProxy = false,
            ConnectCallback = (context, ct) => ConnectAsync(context.DnsEndPoint, allow(), dns, ct),
        };
    }

    internal static async ValueTask<Stream> ConnectAsync(
        DnsEndPoint endpoint, bool allowPrivateHosts,
        Func<string, CancellationToken, Task<IPAddress[]>> resolve, CancellationToken ct)
    {
        var addresses = IPAddress.TryParse(endpoint.Host, out var literal)
            ? [literal]
            : await resolve(endpoint.Host, ct);
        if (addresses.Length == 0)
            throw new SocketException((int)SocketError.HostNotFound);

        if (!allowPrivateHosts && addresses.Any(HostGuard.IsBlocked))
            throw new BlockedAddressException(endpoint.Host);

        Exception? last = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, endpoint.Port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception e) when (e is SocketException or OperationCanceledException)
            {
                socket.Dispose();
                if (e is OperationCanceledException) throw;
                last = e;
            }
        }
        throw last!;
    }
}
