using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SmarterMailAgent.Auth;

namespace SmarterMailAgent.Mcp;

/// <summary>Records which <see cref="ToolGroup"/> each registered tool object came from.</summary>
public sealed class ToolOrigins
{
    private readonly ConcurrentDictionary<McpServerTool, ToolGroup> _groups = new(ReferenceEqualityComparer.Instance);

    internal void Record(McpServerTool tool, ToolGroup group) => _groups[tool] = group;

    public bool TryGet(McpServerTool tool, out ToolGroup group) => _groups.TryGetValue(tool, out group!);
}

public static class ToolRegistration
{
    /// <summary>
    /// Registers exactly the classes in <see cref="ToolPolicy.Groups"/>, the same way the SDK's
    /// <c>WithTools&lt;T&gt;()</c> does (one singleton per <c>[McpServerTool]</c> method, created with
    /// the root provider so <c>AIFunctionFactory</c> can tell DI parameters from arguments), while
    /// recording each tool's group. Throws if this assembly or a shared tool library
    /// (src/Tools.Mailbox, src/Tools.DomainAdmin, src/Tools.SysAdmin) holds a tool class the
    /// policy does not list, so a tool added to a library can never ship here without a scope.
    /// </summary>
    public static IMcpServerBuilder WithAgentTools(this IMcpServerBuilder builder)
    {
        var listed = ToolPolicy.Groups.Select(g => g.Type).ToHashSet();
        var unlisted = ToolPolicy.Groups.Select(g => g.Type.Assembly)
            .Append(typeof(ToolRegistration).Assembly)
            .Distinct()
            .SelectMany(a => a.GetTypes())
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null && !listed.Contains(t))
            .Select(t => t.FullName)
            .ToList();
        if (unlisted.Count > 0)
        {
            throw new InvalidOperationException(
                $"Tool classes without a scope in ToolPolicy.Groups: {string.Join(", ", unlisted)}.");
        }

        builder.Services.AddSingleton<ToolOrigins>();

        foreach (var group in ToolPolicy.Groups)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
                                       BindingFlags.Static | BindingFlags.Instance;
            foreach (var method in group.Type.GetMethods(flags))
            {
                if (method.GetCustomAttribute<McpServerToolAttribute>() is null)
                    continue;

                if (!method.IsStatic)
                {
                    throw new InvalidOperationException(
                        $"{group.Type.Name}.{method.Name} is an instance tool; only static tools are supported.");
                }

                builder.Services.AddSingleton(services =>
                {
                    var tool = McpServerTool.Create(method, (object?)null,
                        new McpServerToolCreateOptions { Services = services });
                    services.GetRequiredService<ToolOrigins>().Record(tool, group);
                    return tool;
                });
            }
        }

        return builder;
    }
}

/// <summary>One registered tool with its policy attached.</summary>
public sealed record ToolEntry(McpServerTool Tool, ToolGroup Group, bool Write)
{
    public string Name => Tool.ProtocolTool.Name;
    public ToolScope Scope => Group.Scope;
    public string Category => Group.Category;
}

/// <summary>
/// The tool set with each tool's scope, category and write flag, and the per-session views of it
/// that <c>GET /api/tools</c> and MCP <c>tools/list</c> return.
/// </summary>
public sealed class ToolCatalog
{
    private readonly IReadOnlyDictionary<string, ToolEntry> _tools;

    public ToolCatalog(IOptions<McpServerOptions> options, ToolOrigins origins)
    {
        var map = new Dictionary<string, ToolEntry>(StringComparer.Ordinal);
        var missing = new List<string>();

        foreach (var tool in options.Value.ToolCollection ?? [])
        {
            var name = tool.ProtocolTool.Name;
            if (!origins.TryGet(tool, out var group))
            {
                missing.Add(name);
                continue;
            }

            if (!map.TryAdd(name, new ToolEntry(tool, group, ToolPolicy.IsWrite(tool, group.Scope))))
                throw new InvalidOperationException($"Two tools are named '{name}'.");
        }

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"Tools registered without a scope (not from ToolPolicy.Groups): {string.Join(", ", missing)}.");
        }

        _tools = map;
    }

    public int Count => _tools.Count;

    public IEnumerable<ToolEntry> Entries => _tools.Values.OrderBy(t => t.Name, StringComparer.Ordinal);

    public bool TryGet(string name, out ToolEntry entry) => _tools.TryGetValue(name, out entry!);

    /// <summary>The tool list as the UI consumes it, for these accounts.</summary>
    public List<ToolDescriptor> List<T>(IReadOnlyList<T> accounts) where T : IToolAccount
    {
        var result = new List<ToolDescriptor>();
        foreach (var (entry, schema) in Visible(accounts))
        {
            result.Add(new ToolDescriptor(
                entry.Name,
                entry.Tool.ProtocolTool.Description ?? string.Empty,
                JsonSerializer.SerializeToNode(schema) ?? new JsonObject(),
                entry.Category,
                entry.Scope.ToString(),
                entry.Write,
                entry.Tool.ProtocolTool.Annotations?.DestructiveHint == true));
        }

        return result;
    }

    /// <summary>The same view for MCP <c>tools/list</c>: protocol tools with rewritten schemas.</summary>
    public List<Tool> ListProtocolTools<T>(IReadOnlyList<T> accounts) where T : IToolAccount
    {
        var result = new List<Tool>();
        foreach (var (entry, schema) in Visible(accounts))
        {
            var source = entry.Tool.ProtocolTool;
            result.Add(new Tool
            {
                Name = source.Name,
                Title = source.Title,
                Description = source.Description,
                InputSchema = schema,
                OutputSchema = source.OutputSchema,
                Annotations = source.Annotations,
                Icons = source.Icons,
                Meta = source.Meta,
            });
        }

        return result;
    }

    private IEnumerable<(ToolEntry Entry, JsonElement Schema)> Visible<T>(IReadOnlyList<T> accounts)
        where T : IToolAccount
    {
        foreach (var entry in Entries)
        {
            var eligible = ToolPolicy.Eligible(entry.Scope, entry.Write, accounts);
            if (eligible.Count == 0)
                continue;

            yield return (entry, ToolPolicy.InjectAccount(
                entry.Tool.ProtocolTool.InputSchema, eligible.Select(a => a.Handle).ToList(), accounts.Count));
        }
    }

    /// <param name="Destructive">Marked <c>Destructive = true</c>: deletes, disables or disconnects something.</param>
    public sealed record ToolDescriptor(
        string Name, string Description, JsonNode InputSchema, string Category, string Scope, bool Write, bool Destructive = false);
}
