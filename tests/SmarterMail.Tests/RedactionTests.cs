using System.Text.Json;
using SmarterMailMcp.Core;
using SmarterMailMcp.SystemAdmin.Tools;
using Xunit;

namespace SmarterMail.Tests;

public class RedactionTests
{
    private const string Sample = """
        {
          "name": "example.com",
          "ldapPassword": "hunter2",
          "emptyPassword": "",
          "nested": { "clientSecret": "s3cr3t", "accessToken": "abc", "items": [ { "privateKey": "PRIV", "publicKey": "PUB" } ] },
          "apiKey": "k",
          "port": 389
        }
        """;

    [Fact]
    public void Blanks_secret_strings_at_any_depth_and_keeps_the_rest()
    {
        var result = SecretRedactor.Redact(JsonDocument.Parse(Sample).RootElement);
        var text = result.GetRawText();

        Assert.DoesNotContain("hunter2", text);
        Assert.DoesNotContain("s3cr3t", text);
        Assert.DoesNotContain("\"abc\"", text);
        Assert.DoesNotContain("PRIV", text);
        Assert.Equal("[redacted]", result.GetProperty("ldapPassword").GetString());
        Assert.Equal("", result.GetProperty("emptyPassword").GetString());
        Assert.Equal("example.com", result.GetProperty("name").GetString());
        Assert.Equal(389, result.GetProperty("port").GetInt32());
        Assert.Equal("PUB", result.GetProperty("nested").GetProperty("items")[0].GetProperty("publicKey").GetString());
    }

    /// <summary>
    /// Every <c>domain_*</c> result is redacted with at least the shared fragments (token and
    /// credential included), so no file in Tools.DomainAdmin can drift to a shorter private list.
    /// </summary>
    [Fact]
    public void Every_domain_tool_file_redacts_at_least_the_default_fragments()
    {
        var assembly = typeof(SmarterMailMcp.Server.Tools.DomainAdminTools).Assembly;
        var withFragments = assembly.GetTypes()
            .Select(t => (Type: t, Field: t.GetField("SecretKeyFragments",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)))
            .Where(x => x.Field is not null)
            .ToList();

        Assert.Equal(
            ["DomainAdminTools", "DomainMailingListTools", "DomainRoutingTools", "DomainSecurityTools", "DomainUserTools"],
            withFragments.Select(x => x.Type.Name).Order(StringComparer.Ordinal));
        foreach (var (type, field) in withFragments)
        {
            var fragments = (string[])field!.GetValue(null)!;
            Assert.All(SecretRedactor.DefaultFragments, f => Assert.Contains(f, fragments));
            Assert.True(fragments.Length > 0, type.Name);
        }
    }

    [Fact]
    public void Key_value_pairs_are_blanked_when_asked()
    {
        var doc = JsonDocument.Parse("""[{ "key": "authToken", "value": "v1" }, { "key": "color", "value": "red" }]""");
        var result = SecretRedactor.Redact(doc.RootElement, SecretRedactor.DefaultFragments, pairValues: true).GetRawText();
        Assert.DoesNotContain("v1", result);
        Assert.Contains("red", result);
    }

    [Fact]
    public void Dkim_shaping_returns_only_dkim_fields()
    {
        var response = JsonDocument.Parse("""
            {
              "domainSettings": {
                "authenticationSettings": { "ldapPassword": "hunter2", "server": "ldap.internal" },
                "domainKeysSettings": {
                  "selector": "default", "publicKey": "MIGfMA0...", "keySize": 2048, "pending": false,
                  "isActive": true, "forced": false, "dkimHeaderFields": "from,to",
                  "privateKey": "PRIVATE", "somethingElse": "x"
                }
              },
              "ldapPassword": "top-level-secret"
            }
            """).RootElement;

        var text = DkimTools.ShapeDkimSettings(response);
        using var doc = JsonDocument.Parse(text);
        var dkim = doc.RootElement.GetProperty("dkim");

        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("default", dkim.GetProperty("selector").GetString());
        Assert.Equal("MIGfMA0...", dkim.GetProperty("publicKey").GetString());
        Assert.Equal(2048, dkim.GetProperty("keySize").GetInt32());
        foreach (var leaked in new[] { "hunter2", "ldap", "PRIVATE", "somethingElse", "top-level-secret", "authenticationSettings" })
            Assert.DoesNotContain(leaked, text);
    }

    [Fact]
    public void Dkim_shaping_without_a_dkim_section_is_an_error_not_the_raw_response()
    {
        var response = JsonDocument.Parse("""{ "domainSettings": { "ldapPassword": "hunter2" } }""").RootElement;
        var text = DkimTools.ShapeDkimSettings(response);
        Assert.DoesNotContain("hunter2", text);
        Assert.False(JsonDocument.Parse(text).RootElement.GetProperty("success").GetBoolean());
    }

    [Fact]
    public void Text_window_tail_and_forward_paging_cover_the_text()
    {
        var text = string.Concat(Enumerable.Range(0, 100).Select(i => $"line {i:000} xxxxxxxxxx\n"));

        var last = TextWindow.Slice(text, 500, 0, tail: true);
        Assert.True(last.HasMore);
        Assert.EndsWith("line 099 xxxxxxxxxx\n", last.Text);
        Assert.StartsWith("line ", last.Text);
        Assert.True(last.ReturnedChars <= 500);

        var earlier = TextWindow.Slice(text, 500, last.NextOffset!.Value, tail: true);
        Assert.Contains(earlier.Text + last.Text, text);

        var seen = 0; int? next = 0;
        while (next is { } o) { var w = TextWindow.Slice(text, 500, o); seen += w.ReturnedChars; next = w.NextOffset; }
        Assert.Equal(text.Length, seen);
    }

    [Fact]
    public void Search_log_result_is_bounded_and_filterable()
    {
        var log = string.Concat(Enumerable.Range(0, 5000).Select(i => $"{i} {(i % 100 == 0 ? "REJECT" : "ok")} row\n"));
        using var doc = JsonDocument.Parse(LogSearchTools.ShapeResult("smtpLog", "", log, false, 2000, 0, true, null));
        Assert.True(doc.RootElement.GetProperty("returnedChars").GetInt32() <= 2000);
        Assert.True(doc.RootElement.GetProperty("hasMore").GetBoolean());
        Assert.Equal(log.Length, doc.RootElement.GetProperty("totalChars").GetInt32());

        using var filtered = JsonDocument.Parse(LogSearchTools.ShapeResult("smtpLog", "", log, false, 20000, 0, true, "reject"));
        Assert.Equal(50, filtered.RootElement.GetProperty("matchedLines").GetInt32());
        Assert.False(filtered.RootElement.GetProperty("hasMore").GetBoolean());
    }
}
