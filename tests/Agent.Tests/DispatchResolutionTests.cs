using System.Text.Json;
using SmarterMailAgent.Auth;
using SmarterMailAgent.Mcp;

namespace SmarterMailAgent.Tests;

public sealed class DispatchResolutionTests
{
    private static readonly FakeAccount User = new("me@example.com", AccountRole.User, ReadOnly: true);
    private static readonly FakeAccount Boss = new("boss@example.com", AccountRole.DomainAdmin, ReadOnly: false);
    private static readonly FakeAccount Sys = new("sysadmin:admin@mail.example.com", AccountRole.SysAdmin, ReadOnly: true);

    private static ToolPolicy.Resolution<FakeAccount> Resolve(
        string tool, ToolScope scope, bool write, string? requested, params FakeAccount[] accounts) =>
        ToolPolicy.Resolve(tool, scope, write, accounts, requested);

    [Fact]
    public void Single_eligible_account_is_used_when_omitted()
    {
        var result = Resolve("get_domains", ToolScope.SysAdmin, false, null, User, Sys);
        Assert.Same(Sys, Assert.IsType<ToolPolicy.Resolution<FakeAccount>.Ok>(result).Account);
    }

    [Fact]
    public void Single_account_session_needs_no_argument()
    {
        var result = Resolve("get_emails", ToolScope.Mailbox, false, null, User);
        Assert.Same(User, Assert.IsType<ToolPolicy.Resolution<FakeAccount>.Ok>(result).Account);
    }

    [Fact]
    public void Several_eligible_and_omitted_lists_the_handles()
    {
        var result = Resolve("get_emails", ToolScope.Mailbox, false, null, User, Boss, Sys);
        var invalid = Assert.IsType<ToolPolicy.Resolution<FakeAccount>.Invalid>(result);
        Assert.Equal(
            "'get_emails' needs an 'account' argument: more than one signed-in account can run it. " +
            "Valid accounts: 'me@example.com', 'boss@example.com'.", invalid.Message);
    }

    [Fact]
    public void Explicit_handle_is_matched_case_insensitively()
    {
        var result = Resolve("get_emails", ToolScope.Mailbox, false, "BOSS@example.com", User, Boss);
        Assert.Same(Boss, Assert.IsType<ToolPolicy.Resolution<FakeAccount>.Ok>(result).Account);
    }

    [Fact]
    public void Unknown_handle_lists_the_valid_ones()
    {
        var result = Resolve("get_emails", ToolScope.Mailbox, false, "nobody@example.com", User, Boss, Sys);
        var invalid = Assert.IsType<ToolPolicy.Resolution<FakeAccount>.Invalid>(result);
        Assert.Equal(
            "Unknown account 'nobody@example.com'. Valid accounts for 'get_emails': 'me@example.com', 'boss@example.com'.",
            invalid.Message);
    }

    [Fact]
    public void Wrong_role_explains_what_is_needed()
    {
        var result = Resolve("get_domains", ToolScope.SysAdmin, false, "me@example.com", User, Sys);
        var invalid = Assert.IsType<ToolPolicy.Resolution<FakeAccount>.Invalid>(result);
        Assert.Equal(
            "Account 'me@example.com' is a user account and cannot use 'get_domains', which needs a system " +
            "admin account. Valid accounts for 'get_domains': 'sysadmin:admin@mail.example.com'.",
            invalid.Message);
    }

    [Fact]
    public void No_account_of_the_right_role()
    {
        var result = Resolve("get_domains", ToolScope.SysAdmin, false, null, User);
        var invalid = Assert.IsType<ToolPolicy.Resolution<FakeAccount>.Invalid>(result);
        Assert.Equal("No signed-in account can use 'get_domains': it needs a system admin account.", invalid.Message);
    }

    [Fact]
    public void Write_on_read_only_account_is_refused_with_its_handle()
    {
        var result = Resolve("delete_domain", ToolScope.SysAdmin, true, "sysadmin:admin@mail.example.com", User, Sys);
        var readOnly = Assert.IsType<ToolPolicy.Resolution<FakeAccount>.ReadOnly>(result);
        Assert.Equal("'delete_domain' changes the server and account 'sysadmin:admin@mail.example.com' is read-only.",
            readOnly.Message);
    }

    [Fact]
    public void Write_with_only_read_only_candidates_and_no_argument_is_read_only()
    {
        var result = Resolve("send_email", ToolScope.Mailbox, true, null, User);
        var readOnly = Assert.IsType<ToolPolicy.Resolution<FakeAccount>.ReadOnly>(result);
        Assert.Equal("'send_email' changes the mailbox and account 'me@example.com' is read-only.", readOnly.Message);
    }

    [Fact]
    public void Write_skips_read_only_accounts_when_choosing_a_default()
    {
        var result = Resolve("send_email", ToolScope.Mailbox, true, null, User, Boss);
        Assert.Same(Boss, Assert.IsType<ToolPolicy.Resolution<FakeAccount>.Ok>(result).Account);
    }

    [Fact]
    public void SplitAccount_strips_the_argument()
    {
        var args = new Dictionary<string, JsonElement>
        {
            ["account"] = JsonSerializer.SerializeToElement("me@example.com"),
            ["folderId"] = JsonSerializer.SerializeToElement("Inbox"),
        };

        var (account, rest) = ToolDispatcher.SplitAccount(args);
        Assert.Equal("me@example.com", account);
        Assert.NotNull(rest);
        Assert.False(rest!.ContainsKey("account"));
        Assert.Equal("Inbox", rest["folderId"].GetString());
    }

    [Fact]
    public void SplitAccount_treats_null_as_omitted_and_other_kinds_as_raw_text()
    {
        var nullArgs = new Dictionary<string, JsonElement> { ["account"] = JsonSerializer.SerializeToElement<string?>(null) };
        Assert.Null(ToolDispatcher.SplitAccount(nullArgs).Account);

        var numberArgs = new Dictionary<string, JsonElement> { ["account"] = JsonSerializer.SerializeToElement(5) };
        Assert.Equal("5", ToolDispatcher.SplitAccount(numberArgs).Account);

        Assert.Null(ToolDispatcher.SplitAccount(null).Account);
    }
}
