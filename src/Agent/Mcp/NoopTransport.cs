using System.Threading.Channels;
using ModelContextProtocol.Protocol;

namespace SmarterMailAgent.Mcp;

/// <summary>
/// A transport that goes nowhere. Used to spin up a throwaway <c>McpServer</c> so that
/// /api/tools/call can invoke <c>McpServerTool</c>s directly with the request's service
/// provider, without a JSON-RPC peer on the other end.
/// </summary>
internal sealed class NoopTransport : ITransport
{
    private readonly Channel<JsonRpcMessage> _channel = Channel.CreateUnbounded<JsonRpcMessage>();

    public string? SessionId => null;

    public ChannelReader<JsonRpcMessage> MessageReader => _channel.Reader;

    public Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
