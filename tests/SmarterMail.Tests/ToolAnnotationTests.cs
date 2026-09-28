using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using SmarterMailMcp.Core.Models;

namespace SmarterMail.Tests;

/// <summary>
/// The read/write split lives on the tools themselves: <c>[McpServerTool(ReadOnly = true)]</c> marks a
/// read, and no annotation means a write (every host fails closed on it). <c>Destructive = true</c>
/// marks writes that delete, disable or disconnect. Read through the SDK, exactly as a host
/// registers the assembly, so this checks what MCP clients actually see in <c>tools/list</c>.
/// </summary>
public sealed class ToolAnnotationTests
{
    private static IReadOnlyList<McpServerTool> SdkTools(Assembly assembly)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<UserContext>(_ => throw new InvalidOperationException("placeholder"));
        services.AddSingleton<GlobalContext>(_ => throw new InvalidOperationException("placeholder"));
        services.AddMcpServer().WithToolsFromAssembly(assembly);
        using var provider = services.BuildServiceProvider();
        return provider.GetServices<McpServerTool>().ToList();
    }

    private static Assembly ByLabel(string label) => label switch
    {
        "Mailbox" => ToolLibraryTests.Mailbox,
        "DomainAdmin" => ToolLibraryTests.DomainAdmin,
        "SysAdmin" => ToolLibraryTests.SysAdmin,
        _ => throw new ArgumentOutOfRangeException(nameof(label)),
    };

    /// <summary>
    /// Read-only tools per library. Changing one of these means a tool moved between read and write:
    /// update the read/write lists in tests/Agent.Tests/ToolPolicyTests.cs in the same change.
    /// </summary>
    public static TheoryData<string, int> ExpectedReadOnlyCounts => new()
    {
        { "Mailbox", 23 },
        { "DomainAdmin", 44 },
        { "SysAdmin", 29 },
    };

    [Theory]
    [MemberData(nameof(ExpectedReadOnlyCounts))]
    public void Read_only_count_per_library(string label, int expected)
    {
        var tools = SdkTools(ByLabel(label));
        Assert.Equal(expected, tools.Count(t => t.ProtocolTool.Annotations?.ReadOnlyHint == true));
    }

    [Theory]
    [InlineData("Mailbox")]
    [InlineData("DomainAdmin")]
    [InlineData("SysAdmin")]
    public void No_tool_is_both_read_only_and_destructive(string label)
    {
        var both = SdkTools(ByLabel(label))
            .Where(t => t.ProtocolTool.Annotations is { ReadOnlyHint: true, DestructiveHint: true })
            .Select(t => t.ProtocolTool.Name)
            .ToList();
        Assert.True(both.Count == 0, "Read-only and destructive: " + string.Join(", ", both));
    }

    [Theory]
    [InlineData("Mailbox")]
    [InlineData("DomainAdmin")]
    [InlineData("SysAdmin")]
    public void Delete_tools_are_destructive(string label)
    {
        var missing = SdkTools(ByLabel(label))
            .Select(t => t.ProtocolTool)
            .Where(t => (t.Name.StartsWith("delete_", StringComparison.Ordinal) ||
                         t.Name.StartsWith("domain_delete_", StringComparison.Ordinal)) &&
                        t.Annotations?.DestructiveHint != true)
            .Select(t => t.Name)
            .ToList();
        Assert.True(missing.Count == 0, "delete tools without Destructive = true: " + string.Join(", ", missing));
    }
}
