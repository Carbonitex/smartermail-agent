using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace SmarterMailMcp.Hosting;

/// <summary>
/// Read-only mode for the MCP hosts. A tool is a read only when its <c>[McpServerTool]</c> sets
/// <c>ReadOnly = true</c>; everything else counts as a write (fail closed: a new tool without the
/// annotation is hidden from read-only servers until someone marks it on purpose). Write tools are
/// removed from the server's tool collection, so they never appear in <c>tools/list</c>.
/// </summary>
public static class ReadOnlyTools
{
    public static bool IsReadOnly(McpServerTool tool) => tool.ProtocolTool.Annotations?.ReadOnlyHint == true;

    /// <summary>
    /// When <paramref name="readOnly"/> is true, drops every write tool once all tools have been
    /// registered (<c>PostConfigure</c> runs after the SDK has filled <see cref="McpServerOptions.ToolCollection"/>).
    /// </summary>
    public static IMcpServerBuilder WithReadOnlyFilter(this IMcpServerBuilder builder, bool readOnly)
    {
        if (!readOnly)
            return builder;

        builder.Services.PostConfigure<McpServerOptions>(options =>
        {
            if (options.ToolCollection is not { } tools)
                return;
            foreach (var tool in tools.Where(t => !IsReadOnly(t)).ToList())
                tools.Remove(tool);
        });
        return builder;
    }
}
