using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace SmarterMail.Tests;

/// <summary>
/// Loopback HTTP server answering each request with (status, body) from a callback. The callback gets
/// the path and, for authenticate-user, the 1-based call number (0 otherwise). Every request is
/// recorded as (path, body) in <see cref="Requests"/>.
/// </summary>
internal sealed class FakeSmarterMail : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Func<string, int, (int Status, string Body)> _respond;
    private int _authenticateCalls;

    public FakeSmarterMail(Func<string, int, (int Status, string Body)> respond)
    {
        _respond = respond;
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        BaseUrl = $"http://127.0.0.1:{port}";
        _listener.Prefixes.Add(BaseUrl + "/");
        _listener.Start();
        _ = Task.Run(LoopAsync);
    }

    public string BaseUrl { get; }
    public int AuthenticateCalls => Volatile.Read(ref _authenticateCalls);
    public ConcurrentQueue<(string Path, string Body)> Requests { get; } = new();

    private async Task LoopAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { return; }

            var path = ctx.Request.Url!.AbsolutePath;
            using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                Requests.Enqueue((path, await reader.ReadToEndAsync()));

            var n = path.EndsWith("/authenticate-user") ? Interlocked.Increment(ref _authenticateCalls) : 0;
            var (status, body) = _respond(path, n);
            var bytes = Encoding.UTF8.GetBytes(body);
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = body.StartsWith('{') ? "application/json" : "text/html";
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
    }

    public void Dispose() => _listener.Close();
}
