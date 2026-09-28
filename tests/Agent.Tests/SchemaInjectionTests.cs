using System.Text.Json;
using SmarterMailAgent.Mcp;

namespace SmarterMailAgent.Tests;

public sealed class SchemaInjectionTests
{
    private static readonly JsonElement Schema = JsonDocument.Parse(
        """{"type":"object","properties":{"folderId":{"type":"string"}},"required":["folderId"]}""").RootElement;

    [Fact]
    public void Single_account_session_is_untouched()
    {
        var result = ToolPolicy.InjectAccount(Schema, ["me@example.com"], sessionAccountCount: 1);
        Assert.Equal(Schema.GetRawText(), result.GetRawText());
    }

    [Fact]
    public void No_eligible_accounts_is_untouched()
    {
        var result = ToolPolicy.InjectAccount(Schema, [], sessionAccountCount: 3);
        Assert.Equal(Schema.GetRawText(), result.GetRawText());
    }

    [Fact]
    public void One_eligible_of_several_is_optional_with_a_one_value_enum()
    {
        var result = ToolPolicy.InjectAccount(Schema, ["me@example.com"], sessionAccountCount: 2);
        var account = result.GetProperty("properties").GetProperty("account");
        Assert.Equal("string", account.GetProperty("type").GetString());
        Assert.Equal(["me@example.com"], account.GetProperty("enum").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(["folderId"], result.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
        Assert.True(result.GetProperty("properties").TryGetProperty("folderId", out _));
    }

    [Fact]
    public void Several_eligible_makes_account_required()
    {
        var result = ToolPolicy.InjectAccount(Schema, ["a@x.com", "b@x.com"], sessionAccountCount: 2);
        Assert.Equal(["a@x.com", "b@x.com"],
            result.GetProperty("properties").GetProperty("account").GetProperty("enum").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(["folderId", "account"], result.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public void Schema_without_properties_or_required_gets_both()
    {
        var bare = JsonDocument.Parse("""{"type":"object"}""").RootElement;
        var result = ToolPolicy.InjectAccount(bare, ["a@x.com", "b@x.com"], sessionAccountCount: 2);
        Assert.True(result.GetProperty("properties").TryGetProperty("account", out _));
        Assert.Equal(["account"], result.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public void Original_schema_is_not_mutated()
    {
        var before = Schema.GetRawText();
        ToolPolicy.InjectAccount(Schema, ["a@x.com", "b@x.com"], sessionAccountCount: 2);
        Assert.Equal(before, Schema.GetRawText());
    }
}
