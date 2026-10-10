using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SmarterMailAgent.Auth;
using SmarterMailAgent.Llm;
using SmarterMailAgent.Mcp;
using SmarterMailAgent.Profiles;
using SmarterMailAgent.Server;
using SmarterMailAgent.Storage;
using SmarterMailAgent.Tasks;
using SmarterMailAgent.Tasks.Approvals;
using SmarterMailMcp.Core.Models;

namespace SmarterMailAgent.Tests;

/// <summary>
/// A fake SmarterMail REST API for the tools themselves (not sign-in): records every call and answers
/// <c>{"success":true}</c>, or 500 for paths containing one of <see cref="FailPaths"/>.
/// </summary>
internal sealed class FakeMailApi : HttpMessageHandler
{
    public sealed record Call(string Method, string Path, string Body);

    public List<Call> Calls { get; } = [];

    public HashSet<string> FailPaths { get; } = [];

    /// <summary>Points an account's tool client at this fake (the agent builds a real, guarded one).</summary>
    public void Attach(Account account)
    {
        var field = typeof(UserContext).GetField("_httpClient", BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(account.UserContext, new HttpClient(this, disposeHandler: false) { BaseAddress = new Uri(account.BaseUrl) });
        // As a real sign-in sets it (the test fixture's TokenData leaves Core's default).
        account.UserContext.ReadOnlyMode = account.ReadOnly;
    }

    public int Count(string pathPart)
    {
        lock (Calls)
            return Calls.Count(c => c.Path.Contains(pathPart, StringComparison.Ordinal));
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        var path = request.RequestUri!.AbsolutePath;
        lock (Calls)
            Calls.Add(new Call(request.Method.Method, path, body));

        if (FailPaths.Any(p => path.Contains(p, StringComparison.Ordinal)))
            return new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = JsonContent.Create(new { message = "boom" }) };
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { success = true }) };
    }
}

/// <summary>A sign-in stub whose refresh-token can be made unreachable (the mail server is down).</summary>
internal sealed class DownableAuth : HttpMessageHandler
{
    private readonly HttpMessageInvoker _inner = new(new StubSmarterMail());

    public bool Down { get; set; }

    /// <summary>The mail server answers this late (honouring cancellation).</summary>
    public TimeSpan? Delay { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (Delay is { } delay)
            await Task.Delay(delay, ct);
        return Down ? throw new HttpRequestException("connection refused") : await _inner.SendAsync(request, ct);
    }

    public SmarterMailAuth Auth() => new(NullLogger<SmarterMailAuth>.Instance, new HttpClient(this), TimeSpan.FromSeconds(2));
}

public sealed class ProposalHashTests
{
    private static Dictionary<string, JsonElement> Args(string json) => ProposalHash.Parse(json);

    [Fact]
    public void Canonical_form_sorts_keys_recursively_and_keeps_raw_values()
    {
        var canonical = ProposalHash.Canonical(Args("""{ "b": 1.50, "a": { "z": [3, {"y": true, "x": null}], "é": "café" }, "B": "x" }"""));
        Assert.Equal("""{"B":"x","a":{"z":[3,{"x":null,"y":true}],"é":"café"},"b":1.50}""", canonical);

        // Canonicalising a canonical string changes nothing: the executor relies on it.
        Assert.Equal(canonical, ProposalHash.Canonical(canonical));
        Assert.Equal("{}", ProposalHash.Canonical((IReadOnlyDictionary<string, JsonElement>?)null));

        // A value keeps the exact escapes it arrived with (the browser hashes the string, never re-encodes).
        const string backslash = "\\";
        var escaped = "{\"v\":\"caf" + backslash + "u00e9 " + backslash + "\"q" + backslash + "\"\",\"n\":1e2}";
        Assert.Equal("{\"n\":1e2,\"v\":\"caf" + backslash + "u00e9 " + backslash + "\"q" + backslash + "\"\"}", ProposalHash.Canonical(escaped));
    }

    [Fact]
    public void Ordinal_order_is_utf16_code_units()
    {
        // 'Z' (0x5A) < '_' (0x5F) < 'a' (0x61) < 'é' (0xE9): ordinal, not culture order.
        Assert.Equal("""{"Z":1,"_":2,"a":3,"é":4}""", ProposalHash.Canonical(Args("""{"é":4,"a":3,"_":2,"Z":1}""")));
    }

    [Fact]
    public void Hash_binds_tool_account_and_every_byte_of_the_arguments()
    {
        var args = ProposalHash.Canonical(Args("""{"to":"a@example.com","n":1}"""));
        var hash = ProposalHash.Of("send_email", "acc1", args);
        Assert.True(ProposalHash.IsWellFormed(hash));
        Assert.Equal(hash, ProposalHash.Of("send_email", "acc1", Args("""{"n":1,"to":"a@example.com"}""")));
        Assert.NotEqual(hash, ProposalHash.Of("send_email", "acc2", args));
        Assert.NotEqual(hash, ProposalHash.Of("forward_email", "acc1", args));
        Assert.NotEqual(hash, ProposalHash.Of("send_email", "acc1", Args("""{"to":"a@example.com","n":1.0}""")));
    }

    [Fact]
    public void Duplicate_keys_are_refused() =>
        Assert.Throws<ProposalHash.InvalidArgumentsException>(() =>
            ProposalHash.Canonical(Args("""{"a":{"x":1,"x":2}}""")));

    /// <summary>
    /// The browser hashes the argsJson it displays (approvals-core.js); this vector proves both sides
    /// agree. Checked in for the node test; regenerate with UPDATE_VECTORS=1.
    /// </summary>
    [Fact]
    public void Hash_vector_for_the_browser()
    {
        var path = Path.Combine(RepoRoot(), "src", "Agent", "wwwroot", "dev", "test", "vectors", "proposal-hash.json");
        if (Environment.GetEnvironmentVariable("UPDATE_VECTORS") == "1" || !File.Exists(path))
        {
            var argsJson = ProposalHash.Canonical(Args(
                """{"to":"archive@example.net","subject":"Café ☕ 🎉","body":"line 1\nline 2 \"quoted\" <b>","n":1.50,"list":[1,"two",{"b":2,"a":1}],"empty":""}"""));
            var vector = new
            {
                note = "Generated by ProposalHashTests.Hash_vector_for_the_browser; checked by approvals.test.mjs.",
                tool = "forward_email",
                accountId = "acc-123",
                argsJson,
                argsHash = ProposalHash.Of("forward_email", "acc-123", argsJson),
            };
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(vector, new JsonSerializerOptions { WriteIndented = true }) + "\n");
        }

        var json = JsonDocument.Parse(File.ReadAllText(path)).RootElement;
        var stored = json.GetProperty("argsJson").GetString()!;
        Assert.Equal(stored, ProposalHash.Canonical(stored));
        Assert.Equal(json.GetProperty("argsHash").GetString(),
            ProposalHash.Of(json.GetProperty("tool").GetString()!, json.GetProperty("accountId").GetString()!, stored));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SmarterMail.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repo root not found.");
    }
}

public sealed class ApprovalGateTests : IClassFixture<CatalogFixture>
{
    private readonly CatalogFixture _fixture;

    public ApprovalGateTests(CatalogFixture fixture) => _fixture = fixture;

    private static Account Account(string id = "acc1") =>
        ResumeFixtures.NewAccount(new StubSmarterMail().Auth(), readOnly: false, id: id);

    private static Dictionary<string, JsonElement> Args(string json) => ProposalHash.Parse(json);

    [Fact]
    public void Approval_writes_have_their_own_budget_and_never_consume_writes()
    {
        var gate = new ToolGate(new HashSet<string> { "send_email", "delete_contacts" }, maxWrites: 1, dryRun: false)
        {
            ApprovalWrites = new HashSet<string> { "delete_contacts" },
            MaxProposals = 2,
        };
        var a = Account();
        Assert.Equal(ToolGate.Decision.NotAllowed, gate.Admit("move_emails", a, null));
        Assert.Equal(ToolGate.Decision.Propose, gate.Admit("delete_contacts", a, null));
        Assert.Equal(ToolGate.Decision.Propose, gate.Admit("delete_contacts", a, null));
        Assert.Equal(ToolGate.Decision.OverBudget, gate.Admit("delete_contacts", a, null));
        Assert.Equal(0, gate.Writes);
        Assert.Equal(ToolGate.Decision.Run, gate.Admit("send_email", a, null));
        Assert.Equal(ToolGate.Decision.OverBudget, gate.Admit("send_email", a, null));
        Assert.Equal(2, gate.ProposalsAdmitted);
    }

    /// <summary>Mail that leaves the server needs the passkey step-up; the rule is pinned here.</summary>
    [Fact]
    public void Outbound_mail_tools_need_a_passkey()
    {
        var catalog = _fixture.Catalog;
        var sending = catalog.Entries.Where(e => TaskProposalSink.SendsMail(e.Name)).Select(e => e.Name).Order(StringComparer.Ordinal);
        Assert.Equal(
        [
            "create_content_filter", "create_content_filter_simple", "forward_email", "new_or_update_calendar_event", "reply_to_email",
            "respond_to_meeting", "send_draft", "send_email", "send_email_with_attachments", "update_content_filter",
        ], sending);

        // Every name in the list is a real mailbox write (a rename would otherwise silently drop it).
        foreach (var name in TaskProposalSink.OutboundMailTools)
        {
            Assert.True(catalog.TryGet(name, out var entry), name);
            Assert.True(entry.Write, name);
            Assert.Equal(ToolScope.Mailbox, entry.Scope);
        }

        var none = new TaskApprovals();
        Assert.True(catalog.TryGet("send_email", out var send));
        Assert.True(TaskProposalSink.NeedsPasskey(send, none));
        Assert.True(catalog.TryGet("block_senders", out var block));
        Assert.False(TaskProposalSink.NeedsPasskey(block, none));
        Assert.True(catalog.TryGet("move_emails", out var move));
        Assert.False(TaskProposalSink.NeedsPasskey(move, none));
    }

    [Fact]
    public void Client_supplied_ids_are_logged_only_when_well_formed()
    {
        Assert.Equal("run-1_A", SmarterMailAgent.Controllers.ApprovalsController.LoggableId("run-1_A"));
        Assert.Equal("(malformed)", SmarterMailAgent.Controllers.ApprovalsController.LoggableId("x\nFAKE LOG LINE"));
        Assert.Equal("(malformed)", SmarterMailAgent.Controllers.ApprovalsController.LoggableId(new string('a', 65)));
        Assert.Equal("(malformed)", SmarterMailAgent.Controllers.ApprovalsController.LoggableId(""));
    }

    [Fact]
    public void Dry_run_simulates_approval_writes_without_proposing()
    {
        var gate = new ToolGate(new HashSet<string> { "delete_contacts" }, 0, dryRun: true)
        {
            ApprovalWrites = new HashSet<string> { "delete_contacts" },
            MaxProposals = 1,
        };
        Assert.Equal(ToolGate.Decision.DryRun, gate.Admit("delete_contacts", Account(), null));
        Assert.Equal(ToolGate.Decision.OverBudget, gate.Admit("delete_contacts", Account(), null));
    }

    [Fact]
    public void Approved_gate_admits_exactly_one_matching_call()
    {
        var args = Args("""{"domain":"old.example","deleteFiles":false,"opts":{"b":1,"a":2}}""");
        var hash = ProposalHash.Of("delete_domain", "acc1", args);

        // Another account, a changed argument, another tool: refused, and the shot is not spent.
        Assert.Equal(ToolGate.Decision.NotAllowed, ToolGate.Approved("delete_domain", "acc1", hash).Admit("delete_domain", Account("acc2"), args));
        Assert.Equal(ToolGate.Decision.NotAllowed, ToolGate.Approved("delete_domain", "acc1", hash)
            .Admit("delete_domain", Account(), Args("""{"domain":"old.example","deleteFiles":true,"opts":{"b":1,"a":2}}""")));
        Assert.Equal(ToolGate.Decision.NotAllowed, ToolGate.Approved("delete_domain", "acc1", hash).Admit("delete_user", Account(), args));
        Assert.Equal(ToolGate.Decision.NotAllowed, ToolGate.Approved("delete_domain", "acc1", hash).Admit("delete_domain"));

        var gate = ToolGate.Approved("delete_domain", "acc1", hash);
        Assert.Equal(ToolGate.Decision.NotAllowed, gate.Admit("delete_domain", Account("acc2"), args));
        // A reordered but equal object hashes the same: accepted.
        Assert.Equal(ToolGate.Decision.Run, gate.Admit("delete_domain", Account(),
            Args("""{"opts":{"a":2,"b":1},"deleteFiles":false,"domain":"old.example"}""")));
        // Then never again, not even the identical call.
        Assert.Equal(ToolGate.Decision.NotAllowed, gate.Admit("delete_domain", Account(), args));
    }

    [Fact]
    public void No_tool_takes_a_reserved_argument_name()
    {
        foreach (var entry in _fixture.Catalog.Entries)
        {
            var schema = entry.Tool.ProtocolTool.InputSchema;
            if (!schema.TryGetProperty("properties", out var properties))
                continue;
            Assert.False(properties.TryGetProperty(ApprovalNote.Property, out _), $"{entry.Name} has an approvalNote parameter");
            Assert.False(properties.TryGetProperty(ToolPolicy.AccountProperty, out _), $"{entry.Name} has an account parameter");
        }
    }

    [Fact]
    public void The_note_is_injected_only_on_approval_tools()
    {
        var tools = new JsonArray(
            new JsonObject { ["type"] = "function", ["function"] = new JsonObject { ["name"] = "send_email", ["parameters"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() } } },
            new JsonObject { ["type"] = "function", ["function"] = new JsonObject { ["name"] = "get_emails", ["parameters"] = new JsonObject { ["type"] = "object" } } });
        ApprovalNote.Inject(tools, new HashSet<string> { "send_email" });
        Assert.Equal(500, tools[0]!["function"]!["parameters"]!["properties"]![ApprovalNote.Property]!["maxLength"]!.GetValue<int>());
        Assert.Null(tools[1]!["function"]!["parameters"]!["properties"]);
    }

    [Fact]
    public void Definition_validation()
    {
        var approvals = new TaskApprovals(["delete_contacts", "send_email"], 60, null, 200);
        var errors = approvals.Validate(["send_email"], maxProposalsLimit: 50);
        Assert.Contains(errors, e => e.Contains("'delete_contacts'"));
        Assert.Contains(errors, e => e.Contains("approval limit"));
        Assert.Contains(errors, e => e.Contains("7 days"));
        Assert.Empty(new TaskApprovals(["send_email"], 10, true, 168).Validate(["send_email"], 50));
        Assert.Empty(TaskApprovals.Normalize(new TaskApprovals())!.Validate([], 50));
        Assert.Equal(72, TaskApprovals.Normalize(new TaskApprovals())!.TtlHours);
        Assert.Contains(new TaskApprovals(TtlHours: 0).Validate([], 50), e => e.Contains("1 hour"));
    }

    [Fact]
    public void Older_definitions_open_without_approvals()
    {
        var sealer = new Sealer(RandomNumberGenerator.GetBytes(32));
        // A v1 blob as it was sealed before approvals existed.
        var v1 = """{"version":1,"name":"T","prompt":"p","cron":"0 7 * * *","timeZone":"UTC","accountIds":["a"],"allowedWrites":[],"maxWrites":5,"model":"m","emailAccountId":null}""";
        var sealedValue = sealer.SealString(Encoding.UTF8.GetBytes(v1), ProfileCrypto.TaskDefinitionLabel, ProfileCrypto.Context("p", "t"));
        var definition = TaskDefinition.Open(sealer, "p", "t", sealedValue)!;
        Assert.Null(definition.Approvals);
        Assert.Equal(5, definition.MaxWrites);
    }

    [Fact]
    public void Prompt_and_footer()
    {
        Assert.Empty(ApprovalPrompt.Lines([]));
        var lines = string.Join("\n", ApprovalPrompt.Lines(["send_email"]));
        Assert.Contains("Changes that need approval", lines);
        Assert.Contains("approvalNote", lines);

        var footer = ApprovalPrompt.EmailFooter(2, new DateTimeOffset(2026, 10, 12, 14, 0, 0, TimeSpan.Zero),
            TimeZoneInfo.FindSystemTimeZoneById("America/Phoenix"), "https://agent.example.com/");
        Assert.Equal("2 changes are waiting for your approval in SmarterMail Agent (https://agent.example.com/) → Tasks → To approve. " +
                     "The first expires 2026-10-12 07:00 (America/Phoenix).", footer);
        Assert.Null(ApprovalPrompt.EmailFooter(0, null, TimeZoneInfo.Utc, null));
        Assert.Equal("report", ApprovalPrompt.WithFooter("report", null));
        Assert.StartsWith("(The task finished without a report.)", ApprovalPrompt.WithFooter(null, "f"));
    }
}

/// <summary>
/// The approval queue in-process: a profile created over HTTP with a software passkey, two delegated
/// read-write accounts (a mailbox and a system admin) whose tool calls go to <see cref="FakeMailApi"/>,
/// and a task whose writes need approval.
/// </summary>
public sealed class ApprovalQueueTests : IDisposable
{
    private readonly DownableAuth _auth = new();
    private readonly FakeMailApi _mail = new();
    private readonly FakeLlm _llm = new();
    private readonly WebApplicationFactory<Program> _app;
    private readonly HttpClient _client;
    private readonly List<IDisposable> _disposables = [];

    public ApprovalQueueTests()
    {
        _app = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("DATA_DIR", TestEnvironment.NewDataDir());
            builder.UseSetting("DATA_KEY", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
            builder.UseSetting("LLM_BASE_URL", "https://llm.test/api/v1/");
            builder.UseSetting("APPROVAL_MAX_PENDING", "5");
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton(_auth.Auth());
                services.AddHttpClient<OpenRouterClient>().ConfigurePrimaryHttpMessageHandler(() => _llm);
            });
        });
        _client = _app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, AllowAutoRedirect = false });
    }

    public void Dispose()
    {
        foreach (var d in _disposables)
            d.Dispose();
        _client.Dispose();
        _app.Dispose();
    }

    private sealed record Profile(
        string Cookie, string ProfileId, SoftAuthenticator Passkey, ECDiffieHellman Inbox, Account Mailbox, Account Admin);

    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();

    private static HttpRequestMessage Req(HttpMethod method, string path, string? cookie = null, object? body = null, string? bearer = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (cookie is not null)
            request.Headers.Add("Cookie", $"{SessionStore.CookieName}={cookie}");
        if (bearer is not null)
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        return request;
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> Send(HttpMethod method, string path, string? cookie = null, object? body = null,
        string? bearer = null)
    {
        using var response = await _client.SendAsync(Req(method, path, cookie, body, bearer));
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, string.IsNullOrEmpty(text) ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private static string? CookieOf(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.Select(v => v.Split(';')[0]).FirstOrDefault(v => v.StartsWith(SessionStore.CookieName + "=", StringComparison.Ordinal))
                ?[(SessionStore.CookieName.Length + 1)..]
            : null;

    /// <summary>A profile with a passkey, unlocked, holding two delegated read-write accounts.</summary>
    private async Task<Profile> CreateProfileAsync(string mailboxLogin = "alice@example.com")
    {
        var sessions = Service<SessionStore>();
        var suffix = ProfileCrypto.NewId(6);
        var mailbox = ResumeFixtures.NewAccount(_auth.Auth(), login: mailboxLogin, readOnly: false,
            refresh: $"r-m-{suffix}", clientId: $"smartermail-agent-m-{suffix}");
        var admin = ResumeFixtures.NewAccount(_auth.Auth(), login: "admin", readOnly: false,
            refresh: $"r-a-{suffix}", clientId: $"smartermail-agent-a-{suffix}");
        _mail.Attach(mailbox);
        _mail.Attach(admin);
        var first = sessions.Create(mailbox);
        first.Add(admin, 5, out _);

        var (_, begin) = await Send(HttpMethod.Post, "/api/profile/register/options", first.Id, new { });
        var passkey = new SoftAuthenticator();
        var inbox = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        _disposables.Add(inbox);

        using var created = await _client.SendAsync(Req(HttpMethod.Post, "/api/profile", first.Id, new
        {
            ceremonyId = begin.GetProperty("ceremonyId").GetString(),
            credential = passkey.Create(begin.GetProperty("options"), "http://localhost"),
            wrappedKey = Base64Url.Encode(RandomNumberGenerator.GetBytes(60)),
            accountsKey = Base64Url.Encode(RandomNumberGenerator.GetBytes(32)),
            publicKey = Base64Url.Encode(inbox.ExportSubjectPublicKeyInfo()),
            encryptedPrivateKey = Base64Url.Encode(RandomNumberGenerator.GetBytes(200)),
        }));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var cookie = CookieOf(created)!;
        var profileId = sessions.Get(cookie)!.Profile!.ProfileId;

        foreach (var account in new[] { mailbox, admin })
        {
            var (status, _) = await Send(HttpMethod.Put, $"/api/profile/accounts/{account.Id}/delegation", cookie, new { enabled = true });
            Assert.Equal(HttpStatusCode.OK, status);
        }

        return new Profile(cookie, profileId, passkey, inbox, mailbox, admin);
    }

    private async Task<string> CreateTaskAsync(Profile p, string[]? allowed = null, string[]? approval = null, bool requirePasskey = false,
        string? emailAccountId = null)
    {
        var (status, body) = await Send(HttpMethod.Post, "/api/tasks", p.Cookie, new
        {
            name = "Tidy up",
            prompt = "Block the spammers and remove old domains.",
            cron = "0 7 * * *",
            timeZone = "UTC",
            accountIds = new[] { p.Mailbox.Id, p.Admin.Id },
            allowedWrites = allowed ?? ["block_senders", "delete_domain"],
            maxWrites = 5,
            model = "test/model",
            emailAccountId,
            approvals = new { writes = approval ?? ["block_senders", "delete_domain"], maxProposals = 10, requirePasskey, ttlHours = 72 },
        });
        Assert.Equal(HttpStatusCode.OK, status);
        return body.GetProperty("id").GetString()!;
    }

    private static Dictionary<string, JsonElement> Args(object value) =>
        ProposalHash.Parse(JsonSerializer.Serialize(value));

    private static readonly object BlockArgs = new { senders = "spam@evil.example" };
    private static readonly object DeleteArgs = new { domain = "old.example", deleteFiles = false };

    /// <summary>A task run's gate proposing one call, as the dispatcher does it mid-run.</summary>
    private async Task<ToolDispatcher.Outcome> ProposeAsync(Profile p, string taskId, string tool, object arguments, string runId = "run-1",
        bool dryRun = false)
    {
        var registry = Service<ProfileRegistry>();
        var task = Service<TaskStore>().Get(p.ProfileId, taskId)!;
        var definition = TaskDefinition.Open(registry.ServerSealer!, p.ProfileId, taskId, task.Definition)!;
        var gate = Service<ApprovalQueue>().GateFor(definition, Service<ProfileStore>().GetProfile(p.ProfileId)!, taskId, runId, dryRun);
        var runtime = registry.Find(p.ProfileId)!;
        var context = new TaskToolContext([p.Mailbox, p.Admin], runtime, gate);
        var args = Args(arguments);
        args[ApprovalNote.Property] = JsonSerializer.SerializeToElement("Because the task says so.");
        return await Service<ToolDispatcher>().DispatchAsync(context, tool, args, _app.Services, CancellationToken.None);
    }

    private static string HashOf(Account account, string tool, object arguments) => ProposalHash.Of(tool, account.Id, Args(arguments));

    private ProposalRow Row(Profile p, string id) => Service<ProposalStore>().Get(p.ProfileId, id)!;

    private JsonElement Open(Profile p, string sealedValue, string context) =>
        JsonDocument.Parse(ProfileCrypto.OpenWithPrivateKey(sealedValue, p.Inbox, context)).RootElement.Clone();

    private Task<(HttpStatusCode Status, JsonElement Body)> Approve(Profile p, string id, string hash, string? cookie = null,
        string? ceremonyId = null, JsonObject? credential = null) =>
        Send(HttpMethod.Post, $"/api/tasks/proposals/{id}/approve", cookie ?? p.Cookie, new { argsHash = hash, ceremonyId, credential });

    /// <summary>The full step-up: options bound to this proposal and hash, a passkey assertion, approve.</summary>
    private async Task<(HttpStatusCode Status, JsonElement Body)> ApproveWithPasskey(Profile p, string id, string hash,
        SoftAuthenticator? passkey = null, string? optionsHash = null, string? cookie = null, bool userVerified = true)
    {
        var (status, options) = await Send(HttpMethod.Post, $"/api/tasks/proposals/{id}/approve/options", cookie ?? p.Cookie,
            new { argsHash = optionsHash ?? hash });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(options.GetProperty("passkey").GetBoolean());
        var credential = (passkey ?? p.Passkey).Get(options.GetProperty("options"), "http://localhost", userVerified: userVerified);
        return await Approve(p, id, hash, cookie, options.GetProperty("ceremonyId").GetString(), credential);
    }

    // ------------------------------------------------------------------ proposing

    [Fact]
    public async Task Propose_queues_the_exact_call_and_seals_both_copies()
    {
        var p = await CreateProfileAsync();
        var taskId = await CreateTaskAsync(p);

        var outcome = await ProposeAsync(p, taskId, "block_senders", BlockArgs);
        Assert.Equal(ToolDispatcher.Status.Ok, outcome.Status);
        Assert.True(outcome.Proposed);
        var answer = JsonDocument.Parse(ToolInvoker.Flatten(outcome.Result)).RootElement;
        Assert.True(answer.GetProperty("queued").GetBoolean());
        Assert.Contains("NOT happened", answer.GetProperty("note").GetString());
        Assert.Equal(0, _mail.Count("block-senders"));                     // nothing reached SmarterMail

        var row = Row(p, outcome.ProposalId!);
        Assert.Equal(ProposalStore.Pending, row.Status);
        Assert.False(row.NeedsPasskey);                                     // mailbox, not destructive
        Assert.InRange(row.ExpiresAt - row.CreatedAt, 72 * 3_600_000L - 1000, 72 * 3_600_000L + 1000);

        // The display copy opens with the profile's private key and its own context only; note and
        // account are not in the arguments, and the hash is over the arguments shown.
        var display = Open(p, row.Display, TaskProposalSink.DisplayContext(p.ProfileId, row.Id));
        Assert.Equal("block_senders", display.GetProperty("tool").GetString());
        Assert.Equal("""{"senders":"spam@evil.example"}""", display.GetProperty("argsJson").GetString());
        Assert.Equal("Because the task says so.", display.GetProperty("note").GetString());
        Assert.Equal(p.Mailbox.EmailAddress, display.GetProperty("accountLogin").GetString());
        Assert.Equal(ProposalHash.Of("block_senders", p.Mailbox.Id, display.GetProperty("argsJson").GetString()!),
            display.GetProperty("argsHash").GetString());
        Assert.ThrowsAny<CryptographicException>(() => Open(p, row.Display, TaskProposalSink.DisplayContext(p.ProfileId, "other")));

        // The payload opens with DATA_KEY and this row's context only: a blob moved to another row fails.
        var sealer = Service<ProfileRegistry>().ServerSealer!;
        Assert.NotNull(TaskProposalSink.OpenPayload(sealer, p.ProfileId, row.Id, row.Payload));
        var other = (await ProposeAsync(p, taskId, "delete_domain", DeleteArgs)).ProposalId!;
        Assert.Null(TaskProposalSink.OpenPayload(sealer, p.ProfileId, other, row.Payload));
        Assert.True(Row(p, other).NeedsPasskey);                            // destructive, system admin

        // The database holds neither the tool name nor the arguments in clear.
        var text = Encoding.UTF8.GetString(DatabaseBytes());
        Assert.DoesNotContain("spam@evil.example", text);
        Assert.DoesNotContain("old.example", text);
    }

    [Fact]
    public async Task Same_call_dedupes_queue_caps_and_large_calls_are_refused()
    {
        var p = await CreateProfileAsync();
        var taskId = await CreateTaskAsync(p);

        var first = await ProposeAsync(p, taskId, "block_senders", BlockArgs, runId: "run-1");
        var again = await ProposeAsync(p, taskId, "block_senders", BlockArgs, runId: "run-2");
        Assert.Equal(first.ProposalId, again.ProposalId);
        Assert.Single(Service<ProposalStore>().List(p.ProfileId, "pending", null, 50));

        var tooLarge = await ProposeAsync(p, taskId, "block_senders", new { senders = new string('x', 70_000) });
        Assert.Equal(ToolDispatcher.Status.NotAllowed, tooLarge.Status);
        Assert.Contains("too large to queue", tooLarge.Message);

        // APPROVAL_MAX_PENDING = 5 in this host.
        for (var i = 0; i < 4; i++)
            Assert.True((await ProposeAsync(p, taskId, "block_senders", new { senders = $"s{i}@evil.example" })).Proposed);
        var full = await ProposeAsync(p, taskId, "block_senders", new { senders = "one-more@evil.example" });
        Assert.Equal(ToolDispatcher.Status.NotAllowed, full.Status);
        Assert.Contains("approval queue is full", full.Message);
        Assert.Equal(5, Service<ProposalStore>().PendingCount(p.ProfileId));
    }

    [Fact]
    public async Task A_dry_run_never_creates_proposals()
    {
        var p = await CreateProfileAsync();
        var taskId = await CreateTaskAsync(p);
        var outcome = await ProposeAsync(p, taskId, "delete_domain", DeleteArgs, dryRun: true);
        Assert.True(outcome.Simulated);
        Assert.False(outcome.Proposed);
        Assert.Contains("wouldQueue", ToolInvoker.Flatten(outcome.Result));
        Assert.Empty(Service<ProposalStore>().List(p.ProfileId, "all", null, 50));
        Assert.Equal(0, _mail.Count("domain-delete"));
    }

    // ------------------------------------------------------------------ a whole run

    [Fact]
    public async Task A_run_proposes_instead_of_writing_and_the_report_says_so()
    {
        var p = await CreateProfileAsync();
        var (keyStatus, _) = await Send(HttpMethod.Put, "/api/profile/task-key", p.Cookie, new { key = "sk-or-test" });
        Assert.Equal(HttpStatusCode.OK, keyStatus);
        var taskId = await CreateTaskAsync(p, emailAccountId: p.Mailbox.Id);

        _llm.ToolCalls(("block_senders", new { senders = "spam@evil.example", approvalNote = "They keep sending spam." }))
            .Final("Queued one block for approval.");

        var scheduler = Service<TaskRunScheduler>();
        var tasks = Service<TaskStore>();
        Assert.True(scheduler.RunNow(tasks.Get(p.ProfileId, taskId)!, dryRun: false, out var runId));
        await scheduler.WhenIdleAsync();

        var run = tasks.Run(p.ProfileId, runId)!;
        Assert.Equal("ok", run.Status);
        Assert.Equal(0, run.Writes);
        Assert.Equal(0, _mail.Count("block-senders"));                     // the write never reached SmarterMail
        Assert.Equal(1, Service<ProposalStore>().RunProposals(runId));

        var proposal = Assert.Single(Service<ProposalStore>().List(p.ProfileId, "pending", null, 50));
        Assert.Equal(runId, proposal.RunId);
        Assert.Equal("They keep sending spam.",
            Open(p, proposal.Display, TaskProposalSink.DisplayContext(p.ProfileId, proposal.Id)).GetProperty("note").GetString());

        // The model was offered the note on the approval tool and told how approvals work.
        var request = _llm.Requests[0];
        var block = request["tools"]!.AsArray().Single(t => t!["function"]!["name"]!.GetValue<string>() == "block_senders")!;
        Assert.NotNull(block["function"]!["parameters"]!["properties"]![ApprovalNote.Property]);
        Assert.Contains("Changes that need approval", request["messages"]![0]!["content"]!.GetValue<string>());

        // The transcript marks the step as proposed.
        var transcript = Open(p, run.Transcript!, TaskRunner.RunContext(p.ProfileId, runId));
        var step = transcript.GetProperty("steps").EnumerateArray().Single(s => s.GetProperty("kind").GetString() == "tool");
        Assert.Equal(proposal.Id, step.GetProperty("proposalId").GetString());

        // The emailed report carries the fixed footer, and never the proposed arguments.
        var mailed = string.Join("\n", _mail.Calls.Select(c => c.Body));
        Assert.Contains("waiting for your approval", mailed);
        Assert.DoesNotContain("spam@evil.example", mailed);
        Assert.DoesNotContain("They keep sending spam", mailed);
    }

    // ------------------------------------------------------------------ executing

    [Fact]
    public async Task Approving_executes_exactly_once_even_when_two_approvals_race()
    {
        var p = await CreateProfileAsync();
        var taskId = await CreateTaskAsync(p);
        var id = (await ProposeAsync(p, taskId, "block_senders", BlockArgs)).ProposalId!;
        var hash = HashOf(p.Mailbox, "block_senders", BlockArgs);

        var (options, optionsBody) = await Send(HttpMethod.Post, $"/api/tasks/proposals/{id}/approve/options", p.Cookie, new { argsHash = hash });
        Assert.Equal(HttpStatusCode.OK, options);
        Assert.False(optionsBody.GetProperty("passkey").GetBoolean());

        var executor = Service<ProposalExecutor>();
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => executor.ExecuteAsync(p.ProfileId, id, hash))));
        Assert.Single(results, r => r.Kind == ProposalExecutor.Kind.Executed);
        Assert.Equal(3, results.Count(r => r.Kind == ProposalExecutor.Kind.NotPending));
        Assert.Equal(1, _mail.Count("block-senders"));

        var row = Row(p, id);
        Assert.Equal(ProposalStore.Executed, row.Status);
        Assert.Null(row.Payload);
        var result = Open(p, row.Result!, TaskProposalSink.ResultContext(p.ProfileId, id));
        Assert.False(result.GetProperty("isError").GetBoolean());

        // Over HTTP now: already decided.
        var (again, body) = await Approve(p, id, hash);
        Assert.Equal(HttpStatusCode.Conflict, again);
        Assert.Equal("PROPOSAL_NOT_PENDING", body.GetProperty("code").GetString());
        Assert.Equal(1, _mail.Count("block-senders"));
    }

    [Fact]
    public async Task Approve_over_http_returns_the_sealed_result()
    {
        var p = await CreateProfileAsync();
        var taskId = await CreateTaskAsync(p);
        var id = (await ProposeAsync(p, taskId, "block_senders", BlockArgs)).ProposalId!;

        var (status, body) = await Approve(p, id, HashOf(p.Mailbox, "block_senders", BlockArgs));
        Assert.Equal(HttpStatusCode.OK, status);
        var result = Open(p, body.GetProperty("result").GetString()!, TaskProposalSink.ResultContext(p.ProfileId, id));
        Assert.True(body.GetProperty("status").GetString() == "executed", body + " " + result);
        Assert.Contains("success", result.GetProperty("content").GetString());

        var listed = (await Send(HttpMethod.Get, "/api/tasks/proposals?status=decided", p.Cookie)).Body;
        Assert.Equal("executed", Assert.Single(listed.EnumerateArray()).GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_hash_mismatch_runs_nothing_and_stays_pending()
    {
        var p = await CreateProfileAsync();
        var taskId = await CreateTaskAsync(p);
        var id = (await ProposeAsync(p, taskId, "block_senders", BlockArgs)).ProposalId!;

        var (status, body) = await Approve(p, id, HashOf(p.Mailbox, "block_senders", new { senders = "someone@else.example" }));
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("PROPOSAL_MISMATCH", body.GetProperty("code").GetString());
        Assert.Equal(ProposalStore.Pending, Row(p, id).Status);
        Assert.NotNull(Row(p, id).Payload);
        Assert.Equal(0, _mail.Count("block-senders"));
    }

    [Fact]
    public async Task Expired_proposals_are_refused_and_swept()
    {
        var p = await CreateProfileAsync();
        var taskId = await CreateTaskAsync(p);
        var id = (await ProposeAsync(p, taskId, "block_senders", BlockArgs)).ProposalId!;
        Sql("UPDATE task_proposals SET expires_at = $t WHERE id = $id", ("$t", DataStore.Now() - 1000), ("$id", id));

        var (status, body) = await Approve(p, id, HashOf(p.Mailbox, "block_senders", BlockArgs));
        Assert.Equal(HttpStatusCode.Gone, status);
        Assert.Equal("PROPOSAL_EXPIRED", body.GetProperty("code").GetString());
        Assert.Equal("expired", (await Send(HttpMethod.Get, $"/api/tasks/proposals/{id}", p.Cookie)).Body.GetProperty("status").GetString());

        Assert.Equal(1, Service<ProposalStore>().ExpireDue(DataStore.Now()));
        Assert.Equal(ProposalStore.Expired, Row(p, id).Status);
        Assert.Null(Row(p, id).Payload);
        Assert.Equal(0, _mail.Count("block-senders"));
    }

    [Fact]
    public async Task A_tightened_task_and_a_read_only_account_are_refused_after_approval()
    {
        var p = await CreateProfileAsync();
        var taskId = await CreateTaskAsync(p);
        var blocked = (await ProposeAsync(p, taskId, "block_senders", BlockArgs)).ProposalId!;
        var deleted = (await ProposeAsync(p, taskId, "delete_domain", DeleteArgs)).ProposalId!;

        // The task no longer allows block_senders.
        var (status, _) = await Send(HttpMethod.Put, $"/api/tasks/{taskId}", p.Cookie, new
        {
            name = "Tidy up", prompt = "x", cron = "0 7 * * *", timeZone = "UTC", accountIds = new[] { p.Mailbox.Id, p.Admin.Id },
            allowedWrites = new[] { "delete_domain" }, maxWrites = 5, model = "test/model",
            approvals = new { writes = new[] { "delete_domain" } },
        });
        Assert.Equal(HttpStatusCode.OK, status);
        var executor = Service<ProposalExecutor>();
        var refused = await executor.ExecuteAsync(p.ProfileId, blocked, HashOf(p.Mailbox, "block_senders", BlockArgs));
        Assert.Equal((ProposalExecutor.Kind.Failed, "TOOL_NO_LONGER_ALLOWED"), (refused.Kind, refused.ErrorCode));
        Assert.Null(Row(p, blocked).Payload);

        // The admin is signed in again without "Allow changes".
        var runtime = Service<ProfileRegistry>().Find(p.ProfileId)!;
        var readOnly = ResumeFixtures.NewAccount(_auth.Auth(), login: "admin", readOnly: true, id: p.Admin.Id, refresh: "r-ro");
        runtime.Accounts.Add(readOnly, 5, out _);
        Assert.True(runtime.Save(readOnly));
        _mail.Attach(readOnly);
        var ro = await executor.ExecuteAsync(p.ProfileId, deleted, HashOf(p.Admin, "delete_domain", DeleteArgs));
        Assert.Equal((ProposalExecutor.Kind.Failed, "ACCOUNT_READ_ONLY"), (ro.Kind, ro.ErrorCode));
        Assert.Equal(0, _mail.Count("domain-delete"));
        Assert.Null(Row(p, deleted).Payload);
    }

    [Fact]
    public async Task A_tool_error_is_a_failed_proposal_with_a_sealed_result()
    {
        var p = await CreateProfileAsync();
        var taskId = await CreateTaskAsync(p);
        var id = (await ProposeAsync(p, taskId, "delete_domain", DeleteArgs)).ProposalId!;
        _mail.FailPaths.Add("domain-delete");

        var (status, body) = await ApproveWithPasskey(p, id, HashOf(p.Admin, "delete_domain", DeleteArgs));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("failed", body.GetProperty("status").GetString());
        Assert.Equal("TOOL_ERROR", body.GetProperty("errorCode").GetString());
        Assert.Equal(1, _mail.Count("domain-delete"));

        var row = Row(p, id);
        Assert.Equal(ProposalStore.Failed, row.Status);
        Assert.Null(row.Payload);
        var result = Open(p, row.Result!, TaskProposalSink.ResultContext(p.ProfileId, id));
        Assert.True(result.GetProperty("isError").GetBoolean());
        Assert.Contains("500", result.GetProperty("content").GetString());
    }

    [Fact]
    public async Task An_unreachable_mail_server_before_the_call_leaves_it_pending()
    {
        var p = await CreateProfileAsync();
        var taskId = await CreateTaskAsync(p);
        var id = (await ProposeAsync(p, taskId, "block_senders", BlockArgs)).ProposalId!;
        var hash = HashOf(p.Mailbox, "block_senders", BlockArgs);

        // Nobody else holds the profile: the executor has to restore the delegated account, and the
        // mail server is down. (A second session keeps the profile unlocked for the HTTP call.)
        var runtime = Service<ProfileRegistry>().Find(p.ProfileId)!;
        await runtime.Accounts.FindById(p.Mailbox.Id)!.ForgetAsync();
        runtime.Accounts.Remove(p.Mailbox.Id);
        _auth.Down = true;

        var (status, body) = await Approve(p, id, hash);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Equal("ACCOUNT_UNAVAILABLE", body.GetProperty("code").GetString());
        Assert.Equal(ProposalStore.Pending, Row(p, id).Status);
        Assert.NotNull(Row(p, id).Payload);
        Assert.Equal(0, _mail.Count("block-senders"));
    }

    [Fact]
    public async Task A_slow_mail_server_while_restoring_the_account_leaves_it_pending()
    {
        var p = await CreateProfileAsync();
        var taskId = await CreateTaskAsync(p);
        var id = (await ProposeAsync(p, taskId, "block_senders", BlockArgs)).ProposalId!;
        var hash = HashOf(p.Mailbox, "block_senders", BlockArgs);

        var runtime = Service<ProfileRegistry>().Find(p.ProfileId)!;
        await runtime.Accounts.FindById(p.Mailbox.Id)!.ForgetAsync();
        runtime.Accounts.Remove(p.Mailbox.Id);
        _auth.Delay = TimeSpan.FromSeconds(30);             // the refresh outlasts the executor's budget
        var executor = Service<ProposalExecutor>();
        executor.RestoreTimeout = TimeSpan.FromMilliseconds(200);

        var execution = await executor.ExecuteAsync(p.ProfileId, id, hash);
        Assert.Equal((ProposalExecutor.Kind.Unavailable, "ACCOUNT_UNAVAILABLE"), (execution.Kind, execution.ErrorCode));
        var row = Row(p, id);
        Assert.Equal(ProposalStore.Pending, row.Status);
        Assert.Null(row.ErrorCode);
        Assert.NotNull(row.Payload);
        Assert.Equal(0, _mail.Count("block-senders"));

        // Once the server answers again the same approval goes through (the stored token was not spent).
        _auth.Delay = null;
        executor.RestoreTimeout = null;
        await runtime.RestoreAsync(Service<AccountRestorer>(), 5, CancellationToken.None, new HashSet<string> { p.Mailbox.Id });
        _mail.Attach(runtime.Accounts.FindById(p.Mailbox.Id)!);
        var retried = await executor.ExecuteAsync(p.ProfileId, id, hash);
        Assert.True(retried.Kind == ProposalExecutor.Kind.Executed, $"{retried.Kind} {retried.ErrorCode}");
        Assert.Equal(1, _mail.Count("block-senders"));
    }

    [Fact]
    public async Task A_duplicate_moves_to_the_newer_run_and_only_tightens_the_passkey()
    {
        var p = await CreateProfileAsync();
        var taskId = await CreateTaskAsync(p, allowed: ["block_senders"], approval: ["block_senders"]);
        var id = (await ProposeAsync(p, taskId, "block_senders", BlockArgs, runId: "run-a")).ProposalId!;
        Assert.False(Row(p, id).NeedsPasskey);

        // The task now asks for a passkey on every approval; the next run proposes the same call.
        var (status, _) = await Send(HttpMethod.Put, $"/api/tasks/{taskId}", p.Cookie, new
        {
            name = "Tidy up", prompt = "x", cron = "0 7 * * *", timeZone = "UTC", accountIds = new[] { p.Mailbox.Id, p.Admin.Id },
            allowedWrites = new[] { "block_senders" }, maxWrites = 5, model = "test/model",
            approvals = new { writes = new[] { "block_senders" }, requirePasskey = true },
        });
        Assert.Equal(HttpStatusCode.OK, status);
        var again = await ProposeAsync(p, taskId, "block_senders", BlockArgs, runId: "run-b");
        Assert.Equal(id, again.ProposalId);
        Assert.True(Row(p, id).NeedsPasskey);
        Assert.Equal("run-b", Row(p, id).RunId);

        // Relaxed again: a later duplicate never loosens it.
        (status, _) = await Send(HttpMethod.Put, $"/api/tasks/{taskId}", p.Cookie, new
        {
            name = "Tidy up", prompt = "x", cron = "0 7 * * *", timeZone = "UTC", accountIds = new[] { p.Mailbox.Id, p.Admin.Id },
            allowedWrites = new[] { "block_senders" }, maxWrites = 5, model = "test/model",
            approvals = new { writes = new[] { "block_senders" }, requirePasskey = false },
        });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(id, (await ProposeAsync(p, taskId, "block_senders", BlockArgs, runId: "run-c")).ProposalId);
        Assert.True(Row(p, id).NeedsPasskey);
        Assert.Equal("run-c", Row(p, id).RunId);
    }

    [Fact]
    public async Task Outbound_mail_proposals_need_a_passkey()
    {
        var p = await CreateProfileAsync();
        var taskId = await CreateTaskAsync(p, allowed: ["send_email"], approval: ["send_email"]);
        var id = (await ProposeAsync(p, taskId, "send_email", new { to = "someone@example.net", subject = "Hi", body = "Hello" })).ProposalId!;
        Assert.True(Row(p, id).NeedsPasskey);
    }

    [Fact]
    public async Task A_run_that_throws_after_proposing_still_records_its_proposals()
    {
        var p = await CreateProfileAsync();
        var (keyStatus, _) = await Send(HttpMethod.Put, "/api/profile/task-key", p.Cookie, new { key = "sk-or-test" });
        Assert.Equal(HttpStatusCode.OK, keyStatus);
        var taskId = await CreateTaskAsync(p);
        _llm.ToolCalls(("block_senders", new { senders = "spam@evil.example" })).Throw();

        var scheduler = Service<TaskRunScheduler>();
        var tasks = Service<TaskStore>();
        Assert.True(scheduler.RunNow(tasks.Get(p.ProfileId, taskId)!, dryRun: false, out var runId));
        await scheduler.WhenIdleAsync();

        Assert.Equal("failed", tasks.Run(p.ProfileId, runId)!.Status);
        Assert.Single(Service<ProposalStore>().List(p.ProfileId, "pending", null, 50));
        Assert.Equal(1, Service<ProposalStore>().RunProposals(runId));
    }

    [Fact]
    public async Task Decided_proposals_are_pruned_by_age_and_count()
    {
        var p = await CreateProfileAsync();
        var taskId = await CreateTaskAsync(p, allowed: ["block_senders"], approval: ["block_senders"]);
        var store = Service<ProposalStore>();
        var ids = new List<string>();
        for (var i = 0; i < 4; i++)
            ids.Add((await ProposeAsync(p, taskId, "block_senders", new { senders = $"s{i}@evil.example" })).ProposalId!);
        foreach (var id in ids.Take(3))
            Assert.True(store.Deny(p.ProfileId, id));
        var now = DataStore.Now();
        var day = (long)TimeSpan.FromDays(1).TotalMilliseconds;
        // ids[0] was decided 40 days ago; the others are recent. ids[3] stays pending however old it is.
        Sql("UPDATE task_proposals SET decided_at = $t, created_at = $t WHERE id = $id", ("$t", now - 40 * day), ("$id", ids[0]));
        Sql("UPDATE task_proposals SET created_at = $t WHERE id = $id", ("$t", now - 50 * day), ("$id", ids[3]));
        Sql("UPDATE task_proposals SET created_at = $t WHERE id = $id", ("$t", now - 2000), ("$id", ids[1]));

        Assert.Equal(1, ProposalMaintenance.Prune(store, now));
        Assert.Null(store.Get(p.ProfileId, ids[0]));
        Assert.NotNull(store.Get(p.ProfileId, ids[1]));
        Assert.NotNull(store.Get(p.ProfileId, ids[3]));

        // Beyond the newest K decided ones of the profile: the oldest go (ids[1] is older than ids[2]).
        Assert.Equal(1, store.PruneDecided(now - 30 * day, keepPerProfile: 1));
        Assert.Null(store.Get(p.ProfileId, ids[1]));
        Assert.NotNull(store.Get(p.ProfileId, ids[2]));
        Assert.Equal(ProposalStore.Pending, store.Get(p.ProfileId, ids[3])!.Status);
        Assert.Equal(1, store.PruneDecided(now, keepPerProfile: 0));              // only ids[2]: pending is never pruned
        Assert.NotNull(store.Get(p.ProfileId, ids[3]));
    }

    [Fact]
    public async Task Terminal_states_erase_the_payload_and_a_row_flipped_back_cannot_run_again()
    {
        var p = await CreateProfileAsync();
        var taskId = await CreateTaskAsync(p);
        var store = Service<ProposalStore>();
        var executed = (await ProposeAsync(p, taskId, "block_senders", BlockArgs)).ProposalId!;
        var denied = (await ProposeAsync(p, taskId, "block_senders", new { senders = "b@evil.example" })).ProposalId!;
        var interrupted = (await ProposeAsync(p, taskId, "block_senders", new { senders = "c@evil.example" })).ProposalId!;

        Assert.Equal(HttpStatusCode.OK, (await Approve(p, executed, HashOf(p.Mailbox, "block_senders", BlockArgs))).Status);
        Assert.Equal(HttpStatusCode.NoContent, (await Send(HttpMethod.Post, $"/api/tasks/proposals/{denied}/deny", p.Cookie)).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await Send(HttpMethod.Post, $"/api/tasks/proposals/{denied}/deny", p.Cookie)).Status);

        // A process that died mid-execution: startup marks it unknown, never retried.
        Assert.True(store.Claim(p.ProfileId, interrupted, DataStore.Now()));
        Assert.Equal(1, store.AbandonExecuting());
        Assert.Equal((ProposalStore.Unknown, "INTERRUPTED"), (Row(p, interrupted).Status, Row(p, interrupted).ErrorCode));

        foreach (var id in new[] { executed, denied, interrupted })
            Assert.Null(Row(p, id).Payload);

        // Someone with write access to the database flips them back to pending: nothing to execute.
        Sql("UPDATE task_proposals SET status = 'pending', decided_at = NULL WHERE profile_id = $p", ("$p", p.ProfileId));
        var calls = _mail.Count("block-senders");
        var replay = await Service<ProposalExecutor>().ExecuteAsync(p.ProfileId, executed, HashOf(p.Mailbox, "block_senders", BlockArgs));
        Assert.Equal((ProposalExecutor.Kind.Failed, "PROPOSAL_UNREADABLE"), (replay.Kind, replay.ErrorCode));
        Assert.Equal(calls, _mail.Count("block-senders"));
    }

    [Fact]
    public async Task Deny_all_from_a_run_and_cascade_on_task_delete()
    {
        var p = await CreateProfileAsync();
        var taskId = await CreateTaskAsync(p);
        await ProposeAsync(p, taskId, "block_senders", BlockArgs, runId: "run-a");
        await ProposeAsync(p, taskId, "delete_domain", DeleteArgs, runId: "run-a");
        var kept = (await ProposeAsync(p, taskId, "block_senders", new { senders = "z@evil.example" }, runId: "run-b")).ProposalId!;

        var tasksList = (await Send(HttpMethod.Get, "/api/tasks", p.Cookie)).Body;
        Assert.Equal(3, tasksList.GetProperty("pending").GetInt32());

        var (status, body) = await Send(HttpMethod.Post, "/api/tasks/proposals/deny", p.Cookie, new { runId = "run-a" });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(2, body.GetProperty("denied").GetInt32());
        var pending = (await Send(HttpMethod.Get, "/api/tasks/proposals", p.Cookie)).Body;
        Assert.Equal(kept, Assert.Single(pending.EnumerateArray()).GetProperty("id").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await Send(HttpMethod.Delete, $"/api/tasks/{taskId}", p.Cookie)).Status);
        Assert.Empty(Service<ProposalStore>().List(p.ProfileId, "all", null, 50));
    }

    // ------------------------------------------------------------------ passkey step-up

    [Fact]
    public async Task Destructive_and_admin_proposals_need_a_fresh_passkey()
    {
        var p = await CreateProfileAsync();
        var taskId = await CreateTaskAsync(p);
        var id = (await ProposeAsync(p, taskId, "delete_domain", DeleteArgs)).ProposalId!;
        var hash = HashOf(p.Admin, "delete_domain", DeleteArgs);

        var (status, body) = await Approve(p, id, hash);
        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal("PASSKEY_REQUIRED", body.GetProperty("code").GetString());
        Assert.Equal(0, _mail.Count("domain-delete"));

        (status, body) = await ApproveWithPasskey(p, id, hash);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("executed", body.GetProperty("status").GetString());
        Assert.Equal(1, _mail.Count("domain-delete"));
    }

    [Fact]
    public async Task A_task_can_ask_for_a_passkey_on_every_approval()
    {
        var p = await CreateProfileAsync();
        var taskId = await CreateTaskAsync(p, allowed: ["block_senders"], approval: ["block_senders"], requirePasskey: true);
        var id = (await ProposeAsync(p, taskId, "block_senders", BlockArgs)).ProposalId!;
        Assert.True(Row(p, id).NeedsPasskey);
        var (status, body) = await Approve(p, id, HashOf(p.Mailbox, "block_senders", BlockArgs));
        Assert.Equal("PASSKEY_REQUIRED", body.GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, status);
    }

    [Fact]
    public async Task The_step_up_is_bound_to_session_proposal_hash_and_profile()
    {
        var p = await CreateProfileAsync();
        var taskId = await CreateTaskAsync(p);
        var id = (await ProposeAsync(p, taskId, "delete_domain", DeleteArgs)).ProposalId!;
        var other = (await ProposeAsync(p, taskId, "delete_domain", new { domain = "other.example", deleteFiles = false })).ProposalId!;
        var hash = HashOf(p.Admin, "delete_domain", DeleteArgs);

        async Task Refused(Task<(HttpStatusCode Status, JsonElement Body)> attempt)
        {
            var (status, body) = await attempt;
            Assert.Equal(HttpStatusCode.Unauthorized, status);
            Assert.Equal("PASSKEY_INVALID", body.GetProperty("code").GetString());
        }

        // Options for one hash, approval with another (a well-formed but different hash).
        await Refused(ApproveWithPasskey(p, id, HashOf(p.Admin, "delete_domain", new { domain = "x.example", deleteFiles = true }), optionsHash: hash));

        // A ceremony for this proposal presented for another.
        var (_, options) = await Send(HttpMethod.Post, $"/api/tasks/proposals/{id}/approve/options", p.Cookie, new { argsHash = hash });
        var credential = p.Passkey.Get(options.GetProperty("options"), "http://localhost");
        await Refused(Approve(p, other, hash, ceremonyId: options.GetProperty("ceremonyId").GetString(), credential: credential));

        // Another session of the same (unlocked) profile.
        var runtime = Service<ProfileRegistry>().AcquireSession(p.ProfileId);
        var second = Service<SessionStore>().CreateForProfile(runtime);
        (_, options) = await Send(HttpMethod.Post, $"/api/tasks/proposals/{id}/approve/options", p.Cookie, new { argsHash = hash });
        credential = p.Passkey.Get(options.GetProperty("options"), "http://localhost");
        await Refused(Approve(p, id, hash, cookie: second.Id, ceremonyId: options.GetProperty("ceremonyId").GetString(), credential: credential));

        // No user verification.
        await Refused(ApproveWithPasskey(p, id, hash, userVerified: false));

        // A passkey of another profile.
        var stranger = await CreateProfileAsync("bob@example.com");
        await Refused(ApproveWithPasskey(p, id, hash, passkey: stranger.Passkey));

        Assert.Equal(ProposalStore.Pending, Row(p, id).Status);
        Assert.Equal(0, _mail.Count("domain-delete"));

        // And the real thing still works afterwards.
        Assert.Equal(HttpStatusCode.OK, (await ApproveWithPasskey(p, id, hash)).Status);
    }

    [Fact]
    public async Task A_step_up_ceremony_never_signs_anyone_in()
    {
        var p = await CreateProfileAsync();
        var taskId = await CreateTaskAsync(p);
        var id = (await ProposeAsync(p, taskId, "delete_domain", DeleteArgs)).ProposalId!;
        var (_, options) = await Send(HttpMethod.Post, $"/api/tasks/proposals/{id}/approve/options", p.Cookie,
            new { argsHash = HashOf(p.Admin, "delete_domain", DeleteArgs) });
        var (status, _) = await Send(HttpMethod.Post, "/api/profile/login", body: new
        {
            ceremonyId = options.GetProperty("ceremonyId").GetString(),
            credential = p.Passkey.Get(options.GetProperty("options"), "http://localhost"),
        });
        Assert.NotEqual(HttpStatusCode.OK, status);
    }

    [Fact]
    public async Task An_mcp_token_can_never_list_approve_or_deny()
    {
        var p = await CreateProfileAsync();
        var taskId = await CreateTaskAsync(p);
        var id = (await ProposeAsync(p, taskId, "block_senders", BlockArgs)).ProposalId!;
        var (minted, token) = await Send(HttpMethod.Post, "/api/auth/token", p.Cookie);
        Assert.Equal(HttpStatusCode.OK, minted);
        var bearer = token.GetProperty("token").GetString()!;
        var hash = HashOf(p.Mailbox, "block_senders", BlockArgs);

        foreach (var (method, path, body) in new (HttpMethod, string, object?)[]
                 {
                     (HttpMethod.Get, "/api/tasks/proposals", null),
                     (HttpMethod.Get, $"/api/tasks/proposals/{id}", null),
                     (HttpMethod.Post, $"/api/tasks/proposals/{id}/approve/options", new { argsHash = hash }),
                     (HttpMethod.Post, $"/api/tasks/proposals/{id}/approve", new { argsHash = hash }),
                     (HttpMethod.Post, $"/api/tasks/proposals/{id}/deny", new { }),
                     (HttpMethod.Post, "/api/tasks/proposals/deny", new { runId = "run-1" }),
                     (HttpMethod.Post, $"/api/tasks/proposals/{id}/read", new { }),
                 })
        {
            var (status, _) = await Send(method, path, body: body, bearer: bearer);
            Assert.True(status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden, $"{method} {path}: {status}");
        }

        Assert.Equal(ProposalStore.Pending, Row(p, id).Status);
        Assert.Equal(0, _mail.Count("block-senders"));
    }

    // ------------------------------------------------------------------ contract

    [Fact]
    public async Task Task_validation_and_config()
    {
        var p = await CreateProfileAsync();
        var (status, body) = await Send(HttpMethod.Post, "/api/tasks", p.Cookie, new
        {
            name = "Bad", prompt = "x", cron = "0 7 * * *", timeZone = "UTC", accountIds = new[] { p.Mailbox.Id },
            allowedWrites = new[] { "block_senders" }, model = "m",
            approvals = new { writes = new[] { "send_email" }, ttlHours = 500, maxProposals = 99 },
        });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        var errors = string.Join(" ", body.GetProperty("errors").EnumerateArray().Select(e => e.GetString()));
        Assert.Contains("'send_email'", errors);
        Assert.Contains("7 days", errors);
        Assert.Contains("approval limit", errors);

        var taskId = await CreateTaskAsync(p);
        var view = (await Send(HttpMethod.Get, "/api/tasks", p.Cookie)).Body.GetProperty("tasks")[0].GetProperty("definition");
        Assert.Equal(taskId, (await Send(HttpMethod.Get, "/api/tasks", p.Cookie)).Body.GetProperty("tasks")[0].GetProperty("id").GetString());
        Assert.Equal(2, view.GetProperty("approvals").GetProperty("writes").GetArrayLength());
        Assert.Equal(72, view.GetProperty("approvals").GetProperty("ttlHours").GetInt32());

        var config = (await Send(HttpMethod.Get, "/api/config")).Body.GetProperty("tasks").GetProperty("approvals");
        Assert.Equal(72, config.GetProperty("ttlHours").GetInt32());
        Assert.Equal(168, config.GetProperty("maxTtlHours").GetInt32());
        Assert.Equal(5, config.GetProperty("maxPending").GetInt32());
        Assert.Equal(50, config.GetProperty("maxProposalsPerRun").GetInt32());

        var (notFound, nf) = await Send(HttpMethod.Get, "/api/tasks/proposals/nope", p.Cookie);
        Assert.Equal(HttpStatusCode.NotFound, notFound);
        Assert.Equal("PROPOSAL_NOT_FOUND", nf.GetProperty("code").GetString());
    }

    private void Sql(string sql, params (string, object?)[] parameters)
    {
        using var c = Service<DataStore>().Open();
        using var cmd = c.Command(sql, parameters);
        cmd.ExecuteNonQuery();
    }

    private byte[] DatabaseBytes()
    {
        var path = Service<ServerOptions>().DatabasePath;
        var bytes = File.ReadAllBytes(path);
        return File.Exists(path + "-wal") ? bytes.Concat(File.ReadAllBytes(path + "-wal")).ToArray() : bytes;
    }
}

/// <summary>Server mode with scheduled tasks off: the task, approval and probe endpoints answer their documented 404 code.</summary>
public sealed class TasksDisabledTests
{
    [Theory]
    [InlineData("TASKS_ENABLED", "false")]
    [InlineData(null, null)]                          // no DATA_KEY
    public async Task Task_endpoints_answer_404_not_500(string? key, string? value)
    {
        using var app = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("DATA_DIR", TestEnvironment.NewDataDir());
            if (key is not null)
            {
                builder.UseSetting("DATA_KEY", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
                builder.UseSetting(key, value);
            }
        });
        var account = ResumeFixtures.NewAccount(new StubSmarterMail().Auth(), readOnly: false);
        var session = app.Services.GetRequiredService<SessionStore>().Create(account);
        using var client = app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        foreach (var (method, path, code) in new[]
                 {
                     (HttpMethod.Get, "/api/tasks", "TASKS_DISABLED"),
                     (HttpMethod.Post, "/api/tasks/abc/run", "TASKS_DISABLED"),
                     (HttpMethod.Get, "/api/tasks/runs", "TASKS_DISABLED"),
                     (HttpMethod.Get, "/api/tasks/proposals", "TASKS_DISABLED"),
                     (HttpMethod.Post, "/api/tasks/proposals/abc/deny", "TASKS_DISABLED"),
                     (HttpMethod.Post, "/api/tasks/probe", "TRIGGERS_DISABLED"),
                 })
        {
            using var request = new HttpRequestMessage(method, path);
            request.Headers.Add("Cookie", $"{SessionStore.CookieName}={session.Id}");
            if (method == HttpMethod.Post)
                request.Content = JsonContent.Create(new { accountId = "x", tool = "get_emails" });
            using var response = await client.SendAsync(request);
            Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{method} {path}: {(int)response.StatusCode}");
            Assert.Equal(code, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }
    }
}

public sealed class ApprovalBrowserOnlyTests
{
    [Fact]
    public async Task Browser_only_mode_has_no_approval_endpoints()
    {
        using var app = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("BROWSER_ONLY_MODE", "true");
            builder.UseSetting("DATA_DIR", TestEnvironment.NewDataDir());
        });
        using var client = app.CreateClient();
        using var response = await client.GetAsync("/api/tasks/proposals");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("SERVER_MODE_DISABLED", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }
}
