using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SmarterMailAgent.Auth;
using SmarterMailAgent.Llm;
using SmarterMailAgent.Profiles;
using SmarterMailAgent.Server;
using SmarterMailAgent.Storage;
using SmarterMailAgent.Tasks;
using Xunit;

namespace SmarterMailAgent.Tests;

/// <summary>Invite-only scheduled tasks (<c>TASKS_ACCESS=invite</c>): codes, the store, the gates, the CLI.</summary>
public sealed class TaskInviteStoreTests
{
    private static (TaskInviteStore Invites, ProfileStore Profiles, TaskStore Tasks, ProposalStore Proposals) Stores()
    {
        var db = new DataStore(new ServerOptions { DataDir = TestEnvironment.NewDataDir() }, NullLogger<DataStore>.Instance);
        return (new TaskInviteStore(db), new ProfileStore(db), new TaskStore(db), new ProposalStore(db));
    }

    internal static string NewProfile(ProfileStore store, string? taskKey = null)
    {
        var id = ProfileCrypto.NewId();
        var now = DataStore.Now();
        store.CreateProfile(new ProfileRow(id, now, now, "pub", "priv", null, 0, "check", null, null, taskKey, false),
            new PasskeyRow(ProfileCrypto.NewId(), id, [1], 0, null, null, "w", now, null), []);
        return id;
    }

    [Fact]
    public void Codes_are_80_bits_of_crockford_base32_and_forgiving_to_type()
    {
        var code = TaskInviteStore.NewCode();
        Assert.Matches("^[0-9A-HJKMNP-TV-Z]{4}(-[0-9A-HJKMNP-TV-Z]{4}){3}$", code);
        Assert.NotEqual(code, TaskInviteStore.NewCode());

        var plain = code.Replace("-", "");
        Assert.Equal(plain, TaskInviteStore.Normalize(code));
        Assert.Equal(plain, TaskInviteStore.Normalize($"  {code.ToLowerInvariant().Replace("-", " ")} "));
        Assert.Equal("0000111100001111", TaskInviteStore.Normalize("oOoO-iIlL-0000-1111"));

        Assert.Null(TaskInviteStore.Normalize(null));
        Assert.Null(TaskInviteStore.Normalize(plain[..15]));
        Assert.Null(TaskInviteStore.Normalize(plain + "0"));
        Assert.Null(TaskInviteStore.Normalize("UUUU-0000-0000-0000"));   // U is not in the alphabet
    }

    [Fact]
    public void A_code_grants_access_once_per_use_and_an_admitted_profile_spends_nothing()
    {
        var (invites, profiles, _, _) = Stores();
        var (invite, code) = invites.Create("for a friend", maxUses: 1, lifetime: null);
        var first = NewProfile(profiles);
        var second = NewProfile(profiles);

        Assert.Null(profiles.GetProfile(first)!.TaskAccessAt);
        Assert.Equal(TaskInviteStore.RedeemOutcome.Granted, invites.Redeem(first, code.ToLowerInvariant()));
        Assert.NotNull(profiles.GetProfile(first)!.TaskAccessAt);
        Assert.Equal(TaskInviteStore.RedeemOutcome.AlreadyGranted, invites.Redeem(first, code));
        Assert.Equal(TaskInviteStore.RedeemOutcome.Invalid, invites.Redeem(second, code));   // used up

        var listed = invites.List().Single();
        Assert.Equal((invite.Id, 1, 1, "for a friend"), (listed.Id, listed.Uses, listed.MaxUses, listed.Note));
        var access = invites.AccessList().Single();
        Assert.Equal((first, invite.Id, "for a friend"), (access.ProfileId, access.InviteId, access.InviteNote));

        Assert.Equal(TaskInviteStore.RedeemOutcome.Invalid, invites.Redeem(second, "not a code"));
        Assert.Equal(TaskInviteStore.RedeemOutcome.Invalid, invites.Redeem(second, TaskInviteStore.NewCode()));
        Assert.Equal(TaskInviteStore.RedeemOutcome.Invalid, invites.Redeem("no-such-profile", code));
    }

    [Fact]
    public void Expired_and_revoked_codes_are_refused()
    {
        var (invites, profiles, _, _) = Stores();
        var profile = NewProfile(profiles);

        var (_, expired) = invites.Create(null, 5, TimeSpan.FromMilliseconds(-1));
        Assert.Equal(TaskInviteStore.RedeemOutcome.Invalid, invites.Redeem(profile, expired));

        var (revoked, code) = invites.Create(null, 5, null);
        Assert.True(invites.RevokeInvite(revoked.Id, profilesToo: false, out _));
        Assert.Equal(TaskInviteStore.RedeemOutcome.Invalid, invites.Redeem(profile, code));
        Assert.False(invites.RevokeInvite("nope", false, out _));
    }

    [Fact]
    public void Revoking_access_pauses_tasks_deletes_the_key_and_denies_pending_approvals()
    {
        var (invites, profiles, tasks, proposals) = Stores();
        var (invite, code) = invites.Create(null, 2, null);
        var revoked = NewProfile(profiles, taskKey: "sealed-key");
        var kept = NewProfile(profiles, taskKey: "sealed-key");
        invites.Redeem(revoked, code);
        invites.Redeem(kept, code);

        var now = DataStore.Now();
        tasks.Insert(new TaskRow("t1", revoked, true, "def", now + 60_000, "ok", 0, null, now, now));
        proposals.Create(new ProposalRow("p1", revoked, "t1", "r1", "pending", false, "d1", "payload", "display", null, null,
            now, now + 3_600_000, null, null, false), 100);

        Assert.True(invites.RevokeAccess(revoked));
        Assert.False(invites.RevokeAccess(revoked));   // nothing left to revoke

        var row = profiles.GetProfile(revoked)!;
        Assert.Null(row.TaskAccessAt);
        Assert.Null(row.TaskLlmKey);
        var task = tasks.Get(revoked, "t1")!;
        Assert.False(task.Enabled);
        Assert.Equal(TaskInviteStore.NotInvitedCode, task.Status);
        var proposal = proposals.Get(revoked, "p1")!;
        Assert.Equal("denied", proposal.Status);
        Assert.Null(proposal.Payload);

        Assert.NotNull(profiles.GetProfile(kept)!.TaskAccessAt);
        Assert.Equal("sealed-key", profiles.GetProfile(kept)!.TaskLlmKey);

        // Revoking the code with --profiles takes the rest.
        Assert.True(invites.RevokeInvite(invite.Id, profilesToo: true, out var count));
        Assert.Equal(1, count);
        Assert.Null(profiles.GetProfile(kept)!.TaskAccessAt);
    }

    [Fact]
    public void The_operator_can_grant_without_a_code()
    {
        var (invites, profiles, _, _) = Stores();
        var profile = NewProfile(profiles);
        Assert.True(invites.Grant(profile));
        Assert.Null(invites.AccessList().Single().InviteId);
        Assert.False(invites.Grant("no-such-profile"));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("open", false)]
    [InlineData(" Invite ", true)]
    public void Tasks_access_is_open_unless_set_to_invite(string? value, bool inviteOnly)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["TASKS_ACCESS"] = value }).Build();
        var options = ServerOptions.FromConfiguration(config);
        Assert.Equal(inviteOnly, options.TaskInviteOnly);

        var row = new ProfileRow("p", 0, 0, "pub", "priv", null, 0, "c", null, null, null, false);
        Assert.Equal(!inviteOnly, options.AllowsTasks(row));
        Assert.True(options.AllowsTasks(row with { TaskAccessAt = 1 }));
    }

    [Fact]
    public void A_bad_tasks_access_value_fails_startup()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["TASKS_ACCESS"] = "closed" }).Build();
        Assert.Contains("TASKS_ACCESS", Assert.Throws<InvalidOperationException>(() => ServerOptions.FromConfiguration(config)).Message);
    }

    [Fact]
    public void The_cli_creates_lists_and_revokes()
    {
        var dir = TestEnvironment.NewDataDir();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DATA_DIR"] = dir,
            ["TASKS_ACCESS"] = "invite",
        }).Build();

        (int Exit, string Out, string Err) Run(params string[] args)
        {
            var output = new StringWriter();
            var error = new StringWriter();
            var exit = AdminCli.Run(args, output, error, config);
            return (exit, output.ToString().Trim(), error.ToString().Trim());
        }

        Assert.True(AdminCli.Handles(["invites", "list"]));
        Assert.True(AdminCli.Handles(["access"]));
        Assert.False(AdminCli.Handles(["--urls", "http://x"]));
        Assert.False(AdminCli.Handles([]));

        var created = Run("invites", "create", "--uses", "3", "--days", "7", "--note", "for a friend");
        Assert.Equal(0, created.Exit);
        Assert.Matches("^[0-9A-Z]{4}(-[0-9A-Z]{4}){3}$", created.Out);   // stdout is the code alone, for scripts
        Assert.Contains("3 use(s)", created.Err);

        var listed = Run("invites", "list");
        Assert.Contains("0/3 used", listed.Out);
        Assert.Contains("for a friend", listed.Out);
        Assert.DoesNotContain(created.Out, listed.Out);   // the code is never shown again

        var inviteId = listed.Out.Split(' ')[0];
        var store = new TaskInviteStore(new DataStore(new ServerOptions { DataDir = dir }, NullLogger<DataStore>.Instance));
        var profiles = new ProfileStore(new DataStore(new ServerOptions { DataDir = dir }, NullLogger<DataStore>.Instance));
        var profile = NewProfile(profiles);
        Assert.Equal(TaskInviteStore.RedeemOutcome.Granted, store.Redeem(profile, created.Out));

        Assert.Contains(profile, Run("access", "list").Out);
        Assert.Equal(0, Run("access", "revoke", profile).Exit);
        Assert.Equal(1, Run("access", "revoke", profile).Exit);
        Assert.Equal(0, Run("access", "grant", profile).Exit);
        Assert.Equal(1, Run("access", "grant", "no-such-profile").Exit);
        Assert.Equal(0, Run("invites", "revoke", inviteId).Exit);
        Assert.Contains("revoked", Run("invites", "list").Out);

        Assert.Equal(1, Run("invites", "create", "--uses", "0").Exit);
        Assert.Equal(2, Run("invites", "frobnicate").Exit);
    }

    [Fact]
    public void The_cli_refuses_in_browser_only_mode()
    {
        var dir = TestEnvironment.NewDataDir();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DATA_DIR"] = dir,
            ["BROWSER_ONLY_MODE"] = "true",
        }).Build();
        Assert.Equal(2, AdminCli.Run(["invites", "list"], new StringWriter(), new StringWriter(), config));
        Assert.False(Directory.Exists(dir));
    }
}

public sealed class TaskInviteHttpTests : IDisposable
{
    private readonly StubSmarterMail _stub = new();
    private readonly FakeLlm _llm = new();
    private readonly WebApplicationFactory<Program> _app;

    public TaskInviteHttpTests()
    {
        _app = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("DATA_DIR", TestEnvironment.NewDataDir());
            builder.UseSetting("DATA_KEY", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
            builder.UseSetting("TASKS_ACCESS", "invite");
            builder.UseSetting("LLM_BASE_URL", "https://llm.test/api/v1/");
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton(_stub.Auth());
                services.AddHttpClient<OpenRouterClient>().ConfigurePrimaryHttpMessageHandler(() => _llm);
            });
        });
    }

    public void Dispose() => _app.Dispose();

    private HttpClient Client() =>
        _app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, AllowAutoRedirect = false });

    private static HttpRequestMessage Req(HttpMethod method, string path, string cookie, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Cookie", $"{SessionStore.CookieName}={cookie}");
        if (body is not null)
            request.Content = JsonContent.Create(body);
        return request;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    /// <summary>An unlocked profile session with one live, read-write account (not delegated).</summary>
    private (string ProfileId, Session Session, Account Account, byte[] AccountsKey) ProfileSession()
    {
        var registry = _app.Services.GetRequiredService<ProfileRegistry>();
        var store = _app.Services.GetRequiredService<ProfileStore>();
        var profileId = ProfileCrypto.NewId();
        var accountsKey = RandomNumberGenerator.GetBytes(32);
        var now = DataStore.Now();
        using var inbox = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        store.CreateProfile(
            new ProfileRow(profileId, now, now, Base64Url.Encode(inbox.ExportSubjectPublicKeyInfo()), "priv", null, 0,
                ProfileCrypto.AccountsKeyCheck(accountsKey), null, null, null, false),
            new PasskeyRow(ProfileCrypto.NewId(), profileId, [1], 0, null, null, "w", now, null), []);

        var runtime = registry.AcquireSession(profileId);
        runtime.Unlock(accountsKey, store.GetProfile(profileId)!.AccountsKeyCheck);
        var account = ResumeFixtures.NewAccount(_stub.Auth(), readOnly: false);
        runtime.Accounts.Add(account, 5, out _);
        runtime.Save(account);
        var session = _app.Services.GetRequiredService<SessionStore>().CreateForProfile(runtime);
        return (profileId, session, account, accountsKey);
    }

    [Fact]
    public async Task Without_an_invite_every_task_door_is_shut_and_a_code_opens_them()
    {
        using var client = Client();
        var (profileId, session, account, _) = ProfileSession();
        var cookie = session.Id;

        var config = await Json(await client.GetAsync("/api/config"));
        Assert.True(config.GetProperty("tasks").GetProperty("enabled").GetBoolean());
        Assert.True(config.GetProperty("tasks").GetProperty("inviteOnly").GetBoolean());

        var view = await Json(await client.SendAsync(Req(HttpMethod.Get, "/api/profile", cookie)));
        Assert.True(view.GetProperty("taskAccess").GetProperty("inviteOnly").GetBoolean());
        Assert.False(view.GetProperty("taskAccess").GetProperty("granted").GetBoolean());
        Assert.False(view.GetProperty("canDelegate").GetBoolean());

        foreach (var (method, path, body) in new (HttpMethod, string, object)[]
                 {
                     (HttpMethod.Put, $"/api/profile/accounts/{account.Id}/delegation", new { enabled = true }),
                     (HttpMethod.Put, "/api/profile/task-key", new { key = "sk-or-test" }),
                     (HttpMethod.Post, "/api/tasks", new { name = "T", prompt = "p", cron = "0 7 * * *" }),
                     (HttpMethod.Put, "/api/tasks/t1", new { name = "T", prompt = "p", cron = "0 7 * * *" }),
                     (HttpMethod.Post, "/api/tasks/t1/run", new { dryRun = true }),
                     (HttpMethod.Post, "/api/tasks/probe", new { accountId = account.Id, tool = "get_emails", arguments = new { } }),
                     (HttpMethod.Post, "/api/tasks/proposals/p1/approve/options", new { argsHash = new string('A', 43) }),
                 })
        {
            using var response = await client.SendAsync(Req(method, path, cookie, body));
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{method} {path}: {(int)response.StatusCode}");
            Assert.Equal(TaskInviteStore.NotInvitedCode, (await Json(response)).GetProperty("code").GetString());
        }

        // Reading, clearing and turning things off stay open.
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Req(HttpMethod.Get, "/api/tasks", cookie))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Req(HttpMethod.Put, "/api/profile/task-key", cookie, new { key = (string?)null }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Req(HttpMethod.Put, $"/api/profile/accounts/{account.Id}/delegation", cookie, new { enabled = false }))).StatusCode);

        // A wrong code, then the right one.
        using (var wrong = await client.SendAsync(Req(HttpMethod.Post, "/api/profile/task-access", cookie, new { code = TaskInviteStore.NewCode() })))
        {
            Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
            Assert.Equal("INVITE_INVALID", (await Json(wrong)).GetProperty("code").GetString());
        }

        var (_, code) = _app.Services.GetRequiredService<TaskInviteStore>().Create(null, 1, null);
        using (var redeemed = await client.SendAsync(Req(HttpMethod.Post, "/api/profile/task-access", cookie, new { code })))
        {
            Assert.Equal(HttpStatusCode.OK, redeemed.StatusCode);
            view = await Json(redeemed);
        }
        Assert.True(view.GetProperty("taskAccess").GetProperty("granted").GetBoolean());
        Assert.True(view.GetProperty("canDelegate").GetBoolean());

        using var delegated = await client.SendAsync(Req(HttpMethod.Put, $"/api/profile/accounts/{account.Id}/delegation", cookie, new { enabled = true }));
        Assert.Equal(HttpStatusCode.OK, delegated.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Req(HttpMethod.Put, "/api/profile/task-key", cookie, new { key = "sk-or-test" }))).StatusCode);
        Assert.NotNull(_app.Services.GetRequiredService<ProfileStore>().GetProfile(profileId)!.TaskLlmKey);
    }

    [Fact]
    public async Task A_scheduled_run_without_access_fails_hard_and_pauses()
    {
        var registry = _app.Services.GetRequiredService<ProfileRegistry>();
        var profiles = _app.Services.GetRequiredService<ProfileStore>();
        var tasks = _app.Services.GetRequiredService<TaskStore>();
        var (profileId, session, account, _) = ProfileSession();
        Assert.True(session.Profile!.SetDelegation(account.Id, true));   // delegated while it still had access
        profiles.UpdateTaskLlmKey(profileId, registry.ServerSealer!.SealString(Encoding.UTF8.GetBytes("sk-or-test"), ProfileCrypto.TaskLlmKeyLabel, profileId));

        var now = DataStore.Now();
        var definition = new TaskDefinition(1, "T", "p", "0 7 * * *", "UTC", [account.Id], [], 0, "m", null);
        tasks.Insert(new TaskRow("t1", profileId, true, definition.Seal(registry.ServerSealer!, profileId, "t1"), now - 1000, "ok", 0, null, now, now));

        var scheduler = _app.Services.GetRequiredService<TaskRunScheduler>();
        Assert.Equal(1, scheduler.StartDue(DateTimeOffset.UtcNow));
        await scheduler.WhenIdleAsync();

        var run = tasks.Runs(profileId, "t1", 5).Single();
        Assert.Equal(("failed", TaskInviteStore.NotInvitedCode), (run.Status, run.ErrorCode));
        var task = tasks.Get(profileId, "t1")!;
        Assert.False(task.Enabled);
        Assert.Empty(_llm.Requests);   // never reached the model
    }

    [Fact]
    public async Task Unlocking_a_profile_without_access_moves_delegated_accounts_back_under_its_key()
    {
        using var client = Client();
        var profiles = _app.Services.GetRequiredService<ProfileStore>();
        var (profileId, session, account, accountsKey) = ProfileSession();
        Assert.True(session.Profile!.SetDelegation(account.Id, true));
        Assert.Equal(ProfileStore.SealServer, profiles.Accounts(profileId).Single().Seal);

        // The browser goes away (the profile locks), then comes back and unlocks.
        var sessions = _app.Services.GetRequiredService<SessionStore>();
        await sessions.RemoveAsync(session.Id);
        var registry = _app.Services.GetRequiredService<ProfileRegistry>();
        var again = sessions.CreateForProfile(registry.AcquireSession(profileId));

        using var unlocked = await client.SendAsync(Req(HttpMethod.Post, "/api/profile/unlock", again.Id,
            new { accountsKey = Base64Url.Encode(accountsKey) }));
        Assert.Equal(HttpStatusCode.OK, unlocked.StatusCode);
        Assert.Equal(ProfileStore.SealProfile, profiles.Accounts(profileId).Single().Seal);
    }
}

/// <summary>The Profile menu's Invites section: <c>/api/admin/*</c> for the profiles in <c>ADMIN_PROFILES</c> only.</summary>
public sealed class TaskInviteAdminTests : IDisposable
{
    private const string AdminId = "admin-profile-0001";
    private const string LockedAdminId = "admin-profile-0002";
    private readonly StubSmarterMail _stub = new();
    private readonly WebApplicationFactory<Program> _app;

    public TaskInviteAdminTests()
    {
        _app = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("DATA_DIR", TestEnvironment.NewDataDir());
            builder.UseSetting("DATA_KEY", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
            builder.UseSetting("TASKS_ACCESS", "invite");
            builder.UseSetting("ADMIN_PROFILES", $" {AdminId} , {LockedAdminId} ");
            builder.ConfigureTestServices(services => services.AddSingleton(_stub.Auth()));
        });
    }

    public void Dispose() => _app.Dispose();

    /// <summary>A profile session for <paramref name="profileId"/>, unlocked or not.</summary>
    private Session ProfileSession(string profileId, bool unlocked = true)
    {
        var store = _app.Services.GetRequiredService<ProfileStore>();
        var accountsKey = RandomNumberGenerator.GetBytes(32);
        var now = DataStore.Now();
        store.CreateProfile(new ProfileRow(profileId, now, now, "pub", "priv", null, 0, ProfileCrypto.AccountsKeyCheck(accountsKey), null, null, null, false),
            new PasskeyRow(ProfileCrypto.NewId(), profileId, [1], 0, null, null, "w", now, null), []);
        var runtime = _app.Services.GetRequiredService<ProfileRegistry>().AcquireSession(profileId);
        if (unlocked)
            runtime.Unlock(accountsKey, store.GetProfile(profileId)!.AccountsKeyCheck);
        return _app.Services.GetRequiredService<SessionStore>().CreateForProfile(runtime);
    }

    private async Task<HttpResponseMessage> Send(HttpMethod method, string path, Session? session, object? body = null, string? bearer = null)
    {
        using var client = _app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var request = new HttpRequestMessage(method, path);
        if (session is not null)
            request.Headers.Add("Cookie", $"{SessionStore.CookieName}={session.Id}");
        if (bearer is not null)
            request.Headers.Add("Authorization", $"Bearer {bearer}");
        if (body is not null)
            request.Content = JsonContent.Create(body);
        return await client.SendAsync(request);
    }

    [Fact]
    public async Task Only_an_unlocked_admin_profile_sees_the_endpoints()
    {
        var admin = ProfileSession(AdminId);
        var locked = ProfileSession(LockedAdminId, unlocked: false);
        var other = ProfileSession(ProfileCrypto.NewId());
        var plain = _app.Services.GetRequiredService<SessionStore>().Create(ResumeFixtures.NewAccount(_stub.Auth()));

        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, "/api/admin/invites", admin)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Get, "/api/admin/invites", null)).StatusCode);
        foreach (var session in new[] { other, plain })
            Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Get, "/api/admin/invites", session)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Post, "/api/admin/invites", other, new { })).StatusCode);

        // On the list, but not unlocked with its passkey.
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Get, "/api/admin/invites", locked)).StatusCode);

        // An MCP token of the admin's own session is not a cookie.
        var token = _app.Services.GetRequiredService<SessionStore>().IssueMcpToken(admin)!.Value.Token;
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Get, "/api/admin/invites", null, bearer: token)).StatusCode);

        var view = await (await Send(HttpMethod.Get, "/api/profile", admin)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(view.GetProperty("admin").GetBoolean());
        view = await (await Send(HttpMethod.Get, "/api/profile", other)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(view.GetProperty("admin").GetBoolean());
    }

    [Fact]
    public async Task An_admin_makes_a_code_someone_redeems_it_and_the_admin_revokes_them()
    {
        var admin = ProfileSession(AdminId);
        var guest = ProfileSession(ProfileCrypto.NewId());

        using (var bad = await Send(HttpMethod.Post, "/api/admin/invites", admin, new { uses = 0 }))
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var made = await (await Send(HttpMethod.Post, "/api/admin/invites", admin, new { note = " for\u0007 Sam ", uses = 2, days = 7 }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var code = made.GetProperty("code").GetString()!;
        var inviteId = made.GetProperty("invite").GetProperty("id").GetString()!;
        Assert.Equal("for Sam", made.GetProperty("invite").GetProperty("note").GetString());
        Assert.Equal("open", made.GetProperty("invite").GetProperty("state").GetString());

        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Post, "/api/profile/task-access", guest, new { code })).StatusCode);

        var list = await (await Send(HttpMethod.Get, "/api/admin/invites", admin)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(list.GetProperty("inviteOnly").GetBoolean());
        var invite = list.GetProperty("invites").EnumerateArray().Single();
        Assert.Equal(1, invite.GetProperty("uses").GetInt32());
        Assert.DoesNotContain(code, list.GetRawText());   // the code is never listed
        var access = list.GetProperty("access").EnumerateArray().Single();
        Assert.Equal(guest.Profile!.ProfileId, access.GetProperty("profileId").GetString());
        Assert.Equal("for Sam", access.GetProperty("inviteNote").GetString());
        Assert.False(access.GetProperty("you").GetBoolean());

        Assert.Equal(HttpStatusCode.NoContent, (await Send(HttpMethod.Delete, $"/api/admin/access/{guest.Profile.ProfileId}", admin)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Delete, $"/api/admin/access/{guest.Profile.ProfileId}", admin)).StatusCode);
        Assert.Null(_app.Services.GetRequiredService<ProfileStore>().GetProfile(guest.Profile.ProfileId)!.TaskAccessAt);

        Assert.Equal(HttpStatusCode.NoContent, (await Send(HttpMethod.Post, $"/api/admin/access/{AdminId}", admin)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Delete, $"/api/admin/invites/{inviteId}", admin)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Delete, "/api/admin/invites/nope", admin)).StatusCode);
        list = await (await Send(HttpMethod.Get, "/api/admin/invites", admin)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("revoked", list.GetProperty("invites")[0].GetProperty("state").GetString());
        Assert.True(list.GetProperty("access").EnumerateArray().Single().GetProperty("you").GetBoolean());
    }
}
