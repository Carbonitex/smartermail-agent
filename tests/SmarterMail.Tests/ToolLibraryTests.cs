using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using SmarterMailMcp.Core.Models;
using MailTools = SmarterMailMcp.Server.Tools.MailTools;
using DomainAdminTools = SmarterMailMcp.Server.Tools.DomainAdminTools;
using ServerTools = SmarterMailMcp.SystemAdmin.Tools.ServerTools;

namespace SmarterMail.Tests;

/// <summary>
/// Guards on the shared tool libraries (src/Tools.Mailbox, src/Tools.DomainAdmin, src/Tools.SysAdmin),
/// which are the only place tools live: the MCP hosts and the agent add none of their own. Tool names come from the SDK itself: each assembly is
/// registered with <c>WithToolsFromAssembly</c>, exactly as the hosts do, and the names are read from
/// the resulting <see cref="McpServerTool.ProtocolTool"/> — so attribute <c>Name</c>s and the SDK's
/// default method-name conversion are both covered without re-implementing either.
/// </summary>
public sealed class ToolLibraryTests
{
    public static readonly Assembly Mailbox = typeof(MailTools).Assembly;
    public static readonly Assembly DomainAdmin = typeof(DomainAdminTools).Assembly;
    public static readonly Assembly SysAdmin = typeof(ServerTools).Assembly;

    private static readonly Lazy<IReadOnlyDictionary<Assembly, IReadOnlyList<string>>> Names = new(() =>
        new[] { Mailbox, DomainAdmin, SysAdmin }.ToDictionary(a => a, SdkToolNames));

    /// <summary>The tool names the MCP SDK registers for one assembly.</summary>
    private static IReadOnlyList<string> SdkToolNames(Assembly assembly)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        // The hosts register these, so AIFunctionFactory treats such parameters as DI, not arguments.
        services.AddSingleton<UserContext>(_ => throw new InvalidOperationException("placeholder"));
        services.AddSingleton<GlobalContext>(_ => throw new InvalidOperationException("placeholder"));
        services.AddMcpServer().WithToolsFromAssembly(assembly);
        using var provider = services.BuildServiceProvider();
        return provider.GetServices<McpServerTool>().Select(t => t.ProtocolTool.Name).ToList();
    }

    /// <summary>[McpServerTool] methods on [McpServerToolType] classes, by plain reflection.</summary>
    private static int AttributedMethodCount(Assembly assembly) =>
        assembly.GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                          BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Count(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null);

    public static TheoryData<string, int> ExpectedCounts => new()
    {
        { "Mailbox", 59 },
        { "DomainAdmin", 107 },
        { "SysAdmin", 58 },
    };

    private static Assembly ByLabel(string label) => label switch
    {
        "Mailbox" => Mailbox,
        "DomainAdmin" => DomainAdmin,
        "SysAdmin" => SysAdmin,
        _ => throw new ArgumentOutOfRangeException(nameof(label)),
    };

    [Theory]
    [MemberData(nameof(ExpectedCounts))]
    public void Tool_count_per_assembly(string label, int expected)
    {
        var assembly = ByLabel(label);
        Assert.Equal(expected, Names.Value[assembly].Count);
        // The SDK found every attributed method (none silently skipped).
        Assert.Equal(expected, AttributedMethodCount(assembly));
    }

    [Fact]
    public void Tool_names_are_unique_across_all_libraries()
    {
        var duplicates = Names.Value
            .SelectMany(kv => kv.Value.Select(name => (name, assembly: kv.Key.GetName().Name)))
            .GroupBy(x => x.name)
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key} ({string.Join(", ", g.Select(x => x.assembly))})")
            .ToList();

        Assert.True(duplicates.Count == 0, "Duplicate tool names: " + string.Join("; ", duplicates));
    }

    [Fact]
    public void Every_DomainAdmin_tool_is_domain_prefixed()
    {
        var unprefixed = Names.Value[DomainAdmin].Where(n => !n.StartsWith("domain_", StringComparison.Ordinal)).ToList();
        Assert.True(unprefixed.Count == 0, "Tools.DomainAdmin tools without domain_: " + string.Join(", ", unprefixed));
    }

    [Fact]
    public void No_domain_prefixed_tool_lives_outside_DomainAdmin()
    {
        var stray = Names.Value
            .Where(kv => kv.Key != DomainAdmin)
            .SelectMany(kv => kv.Value.Where(n => n.StartsWith("domain_", StringComparison.Ordinal))
                .Select(n => $"{n} ({kv.Key.GetName().Name})"))
            .ToList();
        Assert.True(stray.Count == 0, "domain_* tools outside Tools.DomainAdmin: " + string.Join(", ", stray));
    }
}
