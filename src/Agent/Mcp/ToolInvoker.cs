using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace SmarterMailAgent.Mcp;

/// <summary>
/// Invokes an <see cref="McpServerTool"/> outside the JSON-RPC pipeline, with an explicit
/// <see cref="IServiceProvider"/> so that the tool's <c>UserContext</c> parameter resolves to the
/// account <c>ToolDispatcher</c> chose. Both /api/tools/call and /mcp <c>tools/call</c> run on this.
/// </summary>
public sealed class ToolInvoker(IOptions<McpServerOptions> serverOptions, ILoggerFactory loggerFactory)
{
    /// <summary>The tool's raw result. A thrown exception comes back as an <c>IsError</c> result.</summary>
    public sealed record Result(CallToolResult Raw, TimeSpan Duration)
    {
        /// <summary>The tool's own error flag, or an explicit <c>success:false</c> payload.</summary>
        public bool Failed => (Raw.IsError ?? false) || PayloadIndicatesFailure(Flatten(Raw));
    }

    public async Task<Result> InvokeAsync(
        McpServerTool tool,
        IReadOnlyDictionary<string, JsonElement>? arguments,
        IServiceProvider services,
        CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();

        await using var transport = new NoopTransport();
        await using var server = McpServer.Create(transport, serverOptions.Value, loggerFactory, services);

        var request = new JsonRpcRequest
        {
            Method = "tools/call",
            Id = new RequestId(1),
        };

        var parameters = new CallToolRequestParams
        {
            Name = tool.ProtocolTool.Name,
            Arguments = arguments is null
                ? null
                : new Dictionary<string, JsonElement>(arguments, StringComparer.Ordinal),
        };

        // RequestContext derives from MessageContext; Services is what the SDK hands to the
        // tool's AIFunction for DI, so point it at the caller's session-aware provider.
        var context = new RequestContext<CallToolRequestParams>(server, request, parameters)
        {
            Services = services,
        };

        try
        {
            var result = await tool.InvokeAsync(context, ct);
            return new Result(result, stopwatch.Elapsed);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Tool exceptions are a normal outcome for an LLM loop: hand the message back as a
            // tool result rather than a transport error. Never include the arguments.
            return new Result(ErrorResult(ex.Message), stopwatch.Elapsed);
        }
    }

    /// <summary>
    /// The SmarterMail tools report upstream failures in-band: they catch the API exception and
    /// return <c>{"success":false,"error":"API call failed: …","statusCode":500}</c> as ordinary
    /// text, leaving the MCP error flag unset. Two things go wrong if that is taken at face value:
    /// the model is told a hard failure succeeded, and the dispatcher's stale-token retry (which keys
    /// off <see cref="Result.Failed"/>) never fires for a 401. An explicit <c>success:false</c> is
    /// the tools' own failure signal, so treat it as one.
    /// </summary>
    public static bool PayloadIndicatesFailure(string content)
    {
        if (string.IsNullOrEmpty(content) || content[0] != '{')
            return false;

        try
        {
            using var document = JsonDocument.Parse(content);
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty("success", out var success)
                   && success.ValueKind == JsonValueKind.False;
        }
        catch (JsonException)
        {
            // Not JSON, or truncated. Nothing to conclude; leave the flag as the tool set it.
            return false;
        }
    }

    public static CallToolResult ErrorResult(string message) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = message }],
    };

    /// <summary>The result as one string, the shape /api/tools/call returns.</summary>
    public static string Flatten(CallToolResult result)
    {
        if (result.Content is { Count: > 0 })
        {
            var builder = new StringBuilder();
            foreach (var block in result.Content)
            {
                if (block is TextContentBlock text)
                {
                    if (builder.Length > 0) builder.Append('\n');
                    builder.Append(text.Text);
                }
                else
                {
                    if (builder.Length > 0) builder.Append('\n');
                    builder.Append(JsonSerializer.Serialize(block));
                }
            }

            return builder.ToString();
        }

        if (result.StructuredContent is { } structured)
            return structured.GetRawText();

        return string.Empty;
    }
}
