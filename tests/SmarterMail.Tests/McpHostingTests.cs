using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using SmarterMailMcp.Core.Models;
using SmarterMailMcp.Hosting;

namespace SmarterMail.Tests;

/// <summary>src/Mcp.Hosting: environment parsing and the read-only tool filter.</summary>
public sealed class McpHostingTests
{
    private static readonly McpHostDefinition User = new()
    {
        ServerName = "smartermail-mcp-user",
        UserVariable = "SMARTERMAIL_USER",
        PasswordVariable = "SMARTERMAIL_PASSWORD",
        UserType = "user",
        DefaultTokenFile = "/tmp/t.json",
    };

    private static readonly Dictionary<string, string> Complete = new()
    {
        ["SMARTERMAIL_URL"] = "https://mail.example.com",
        ["SMARTERMAIL_USER"] = "user@example.com",
        ["SMARTERMAIL_PASSWORD"] = "pw",
        ["API_KEY"] = "key",
    };

    private static (McpHostSettings? Settings, IReadOnlyList<string> Errors) Parse(
        Dictionary<string, string> env, params string[] args) =>
        McpHostSettings.Parse(User, args, name => env.GetValueOrDefault(name));

    private static Dictionary<string, string> With(params (string Key, string? Value)[] changes)
    {
        var env = new Dictionary<string, string>(Complete);
        foreach (var (key, value) in changes)
        {
            if (value is null) env.Remove(key);
            else env[key] = value;
        }
        return env;
    }

    [Fact]
    public void Defaults_are_http_and_read_only()
    {
        var (settings, errors) = Parse(Complete);
        Assert.Empty(errors);
        Assert.Equal(McpTransport.Http, settings!.Transport);
        Assert.True(settings.ReadOnly);
        Assert.Equal("/tmp/t.json", settings.TokenFile);
    }

    [Theory]
    [InlineData("false", false)]
    [InlineData("0", false)]
    [InlineData("FALSE", false)]
    [InlineData("true", true)]
    [InlineData("", true)]
    public void Read_only_is_only_off_when_explicitly_disabled(string value, bool expected)
    {
        var (settings, _) = Parse(With(("SMARTERMAIL_READ_ONLY", value)));
        Assert.Equal(expected, settings!.ReadOnly);
    }

    [Fact]
    public void An_unrecognised_read_only_value_is_an_error_not_a_guess()
    {
        var (settings, errors) = Parse(With(("SMARTERMAIL_READ_ONLY", "nope")));
        Assert.Null(settings);
        Assert.Contains(errors, e => e.Contains("SMARTERMAIL_READ_ONLY"));
    }

    [Fact]
    public void Http_without_an_api_key_fails_before_signing_in()
    {
        var (settings, errors) = Parse(With(("API_KEY", null)));
        Assert.Null(settings);
        Assert.Contains(errors, e => e.Contains("API_KEY"));
    }

    [Theory]
    [InlineData(new[] { "--stdio" }, null)]
    [InlineData(new string[0], "stdio")]
    public void Stdio_needs_no_api_key(string[] args, string? transport)
    {
        var (settings, errors) = Parse(With(("API_KEY", null), ("MCP_TRANSPORT", transport)), args);
        Assert.Empty(errors);
        Assert.Equal(McpTransport.Stdio, settings!.Transport);
        Assert.Null(settings.ApiKey);
    }

    [Fact]
    public void Missing_credentials_are_all_reported_at_once()
    {
        var (settings, errors) = Parse(With(("SMARTERMAIL_URL", null), ("SMARTERMAIL_PASSWORD", null)));
        Assert.Null(settings);
        var missing = Assert.Single(errors, e => e.StartsWith("Missing", StringComparison.Ordinal));
        Assert.Contains("SMARTERMAIL_URL", missing);
        Assert.Contains("SMARTERMAIL_PASSWORD", missing);
    }

    [Fact]
    public void Token_file_can_be_overridden()
    {
        var (settings, _) = Parse(With(("SMARTERMAIL_TOKEN_FILE", "/data/token.json")));
        Assert.Equal("/data/token.json", settings!.TokenFile);
    }

    [Fact]
    public void Unknown_transport_is_an_error()
    {
        var (_, errors) = Parse(With(("MCP_TRANSPORT", "sse")));
        Assert.Contains(errors, e => e.Contains("MCP_TRANSPORT"));
    }

    // ------------------------------------------------------------------ read-only filter

    /// <summary>The tools a host would list, registered the way the hosts register them.</summary>
    private static List<McpServerTool> ListedTools(bool readOnly, params Assembly[] assemblies)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<UserContext>(_ => throw new InvalidOperationException("placeholder"));
        services.AddSingleton<GlobalContext>(_ => throw new InvalidOperationException("placeholder"));
        var mcp = services.AddMcpServer();
        foreach (var assembly in assemblies)
            mcp.WithToolsFromAssembly(assembly);
        mcp.WithReadOnlyFilter(readOnly);
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<McpServerOptions>>().Value.ToolCollection!.ToList();
    }

    public static TheoryData<string, int, int> HostCounts => new()
    {
        // label, read-write count, read-only count
        { "user", 59, 23 },
        { "user+domain", 59 + 107, 23 + 44 },
        { "admin", 58, 29 },
    };

    private static Assembly[] HostAssemblies(string label) => label switch
    {
        "user" => [ToolLibraryTests.Mailbox],
        "user+domain" => [ToolLibraryTests.Mailbox, ToolLibraryTests.DomainAdmin],
        "admin" => [ToolLibraryTests.SysAdmin],
        _ => throw new ArgumentOutOfRangeException(nameof(label)),
    };

    [Theory]
    [MemberData(nameof(HostCounts))]
    public void Read_only_hosts_list_only_read_tools(string label, int all, int reads)
    {
        Assert.Equal(all, ListedTools(readOnly: false, HostAssemblies(label)).Count);

        var listed = ListedTools(readOnly: true, HostAssemblies(label));
        Assert.Equal(reads, listed.Count);
        Assert.All(listed, t => Assert.True(ReadOnlyTools.IsReadOnly(t), t.ProtocolTool.Name));
    }

    [Fact]
    public void Read_only_admin_server_cannot_delete_a_domain()
    {
        var names = ListedTools(readOnly: true, ToolLibraryTests.SysAdmin).Select(t => t.ProtocolTool.Name).ToList();
        Assert.DoesNotContain("delete_domain", names);
        Assert.Contains("get_domains", names);
    }
}
