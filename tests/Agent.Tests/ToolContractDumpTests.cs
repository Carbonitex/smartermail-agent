using System.Text.Encodings.Web;
using System.Text.Json;
using SmarterMailAgent.Auth;

namespace SmarterMailAgent.Tests;

/// <summary>
/// Writes <c>GET /api/tools</c> as JSON fixtures for offline model evaluation
/// (src/Agent/wwwroot/dev/local-model/fixtures). A no-op unless
/// <c>DUMP_TOOL_CONTRACT=&lt;dir&gt;</c> is set, so CI is unaffected.
/// </summary>
public sealed class ToolContractDumpTests(CatalogFixture fixture) : IClassFixture<CatalogFixture>
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [Fact]
    public void Dump_tool_contract_when_asked()
    {
        var dir = Environment.GetEnvironmentVariable("DUMP_TOOL_CONTRACT");
        if (string.IsNullOrWhiteSpace(dir))
            return;

        var user = new FakeAccount("user@example.com", AccountRole.User, ReadOnly: false);
        var domain = new FakeAccount("domainadmin@example.com", AccountRole.DomainAdmin, ReadOnly: false);
        var sys = new FakeAccount("sysadmin:admin@mail.example.com", AccountRole.SysAdmin, ReadOnly: false);

        Directory.CreateDirectory(dir);
        Write(dir, "tools-user.json", [user]);
        Write(dir, "tools-domainadmin.json", [domain]);
        Write(dir, "tools-sysadmin.json", [sys]);
        Write(dir, "tools-multi.json", [user, sys]);
    }

    private void Write(string dir, string file, IReadOnlyList<FakeAccount> accounts) =>
        File.WriteAllText(Path.Combine(dir, file),
            JsonSerializer.Serialize(fixture.Catalog.List(accounts), Options) + "\n");
}
