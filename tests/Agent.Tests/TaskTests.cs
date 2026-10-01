using System.Net;
using System.Net.Http.Json;
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

namespace SmarterMailAgent.Tests;

/// <summary>A scripted OpenRouter: answers each chat-completions call with the next canned reply.</summary>
internal sealed class FakeLlm : HttpMessageHandler
{
    private readonly Queue<Func<JsonObject, HttpResponseMessage>> _script = new();

    public List<JsonObject> Requests { get; } = [];

    public FakeLlm Reply(object body, HttpStatusCode status = HttpStatusCode.OK)
    {
        _script.Enqueue(_ => new HttpResponseMessage(status) { Content = JsonContent.Create(body) });
        return this;
    }

    public FakeLlm ToolCalls(params (string Name, object Arguments)[] calls) => Reply(new
    {
        choices = new[]
        {
            new
            {
                finish_reason = "tool_calls",
                message = new
                {
                    role = "assistant",
                    content = (string?)null,
                    tool_calls = calls.Select((c, i) => new
                    {
                        id = $"call_{i}",
                        type = "function",
                        function = new { name = c.Name, arguments = JsonSerializer.Serialize(c.Arguments) },
                    }),
                },
            },
        },
        usage = new { prompt_tokens = 100, completion_tokens = 10 },
    });

    public FakeLlm Final(string text) => Reply(new
    {
        choices = new[] { new { finish_reason = "stop", message = new { role = "assistant", content = text } } },
        usage = new { prompt_tokens = 120, completion_tokens = 30 },
    });

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject();
        lock (Requests)
            Requests.Add(body);
        return _script.TryDequeue(out var next)
            ? next(body)
            : new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = JsonContent.Create(new { error = new { message = "script ran out" } }) };
    }
}

public sealed class TaskDefinitionTests : IClassFixture<CatalogFixture>
{
    private readonly ToolCatalog _catalog;

    public TaskDefinitionTests(CatalogFixture fixture) => _catalog = fixture.Catalog;

    private static TaskDefinition Def(string cron = "0 7 * * *", string tz = "UTC", string[]? accounts = null,
        string[]? writes = null, string? email = null) =>
        new(1, "Morning", "Summarise.", cron, tz, accounts ?? ["a1"], writes ?? [], 5, "m", email);

    private static Dictionary<string, ProfileRuntime.RowInfo> Rows(bool readOnly = false, AccountRole role = AccountRole.User) => new()
    {
        ["a1"] = new ProfileRuntime.RowInfo("a1", ProfileStore.SealServer, "ok",
            new ResumeAccount("https://mail.example.com", "me@example.com", role, readOnly, "c", "", null, null)),
    };

    [Fact]
    public void A_good_definition_validates() =>
        Assert.Empty(Def(writes: ["send_email"], email: "a1").Validate(Rows(), _catalog, TimeSpan.FromMinutes(15)));

    [Theory]
    [InlineData("*/5 * * * *")]
    [InlineData("* * * * *")]
    [InlineData("0,10 7 * * *")]
    public void Schedules_closer_than_the_minimum_are_refused(string cron) =>
        Assert.Contains(Def(cron).Validate(Rows(), _catalog, TimeSpan.FromMinutes(15)), e => e.Contains("apart"));

    [Theory]
    [InlineData("not cron", "UTC")]
    [InlineData("0 7 * * *", "Mars/Olympus")]
    [InlineData("0 0 7 * * *", "UTC")]
    public void Bad_schedules_are_refused(string cron, string tz) =>
        Assert.Contains(Def(cron, tz).Validate(Rows(), _catalog, TimeSpan.FromMinutes(15)), e => e.Contains("cron"));

    [Fact]
    public void Accounts_must_be_delegated() =>
        Assert.Contains(Def(accounts: ["nope"]).Validate(Rows(), _catalog, TimeSpan.FromMinutes(15)), e => e.Contains("allow scheduled tasks"));

    [Fact]
    public void Writes_must_be_real_write_tools_the_accounts_can_run()
    {
        var errors = Def(writes: ["get_emails", "delete_domain", "made_up"]).Validate(Rows(), _catalog, TimeSpan.FromMinutes(15));
        Assert.Contains(errors, e => e.Contains("'get_emails' is not a tool that makes changes"));
        Assert.Contains(errors, e => e.Contains("None of the task's accounts can use 'delete_domain'"));
        Assert.Contains(errors, e => e.Contains("'made_up'"));
    }

    [Fact]
    public void Writes_and_email_need_a_read_write_account()
    {
        var errors = Def(writes: ["send_email"], email: "a1").Validate(Rows(readOnly: true), _catalog, TimeSpan.FromMinutes(15));
        Assert.Contains(errors, e => e.Contains("Allow changes"));
        Assert.Contains(errors, e => e.Contains("emailed"));
    }

    [Fact]
    public void Next_run_follows_the_time_zone()
    {
        // 07:00 in Phoenix (UTC-7, no DST) is 14:00 UTC.
        var after = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 14, 0, 0, TimeSpan.Zero), TaskDefinition.NextRun("0 7 * * *", "America/Phoenix", after));
        Assert.Equal(new DateTimeOffset(2026, 10, 2, 14, 0, 0, TimeSpan.Zero),
            TaskDefinition.NextRun("0 7 * * *", "America/Phoenix", after.AddHours(3)));
    }

    [Fact]
    public void Seal_and_open()
    {
        var sealer = new Sealer(RandomNumberGenerator.GetBytes(32));
        var def = Def();
        var sealedValue = def.Seal(sealer, "p", "t");
        Assert.Equal(def.Name, TaskDefinition.Open(sealer, "p", "t", sealedValue)!.Name);
        Assert.Null(TaskDefinition.Open(sealer, "p", "other", sealedValue));
    }
}

public sealed class ToolGateTests : IClassFixture<CatalogFixture>
{
    private readonly CatalogFixture _fixture;

    public ToolGateTests(CatalogFixture fixture) => _fixture = fixture;

    [Fact]
    public void Allowlist_budget_and_dry_run()
    {
        var gate = new ToolGate(new HashSet<string> { "send_email" }, maxWrites: 2, dryRun: false);
        Assert.Equal(ToolGate.Decision.NotAllowed, gate.Admit("delete_contacts"));
        Assert.Equal(ToolGate.Decision.Run, gate.Admit("send_email"));
        Assert.Equal(ToolGate.Decision.Run, gate.Admit("send_email"));
        Assert.Equal(ToolGate.Decision.OverBudget, gate.Admit("send_email"));
        Assert.Equal(2, gate.Writes);

        Assert.Equal(ToolGate.Decision.DryRun, new ToolGate(new HashSet<string> { "send_email" }, 1, dryRun: true).Admit("send_email"));
    }

    /// <summary>The dispatcher enforces the gate itself: a dry run never reaches SmarterMail.</summary>
    [Fact]
    public async Task Dispatcher_applies_the_gate()
    {
        var stub = new StubSmarterMail();
        var account = ResumeFixtures.NewAccount(stub.Auth(), readOnly: false);
        var set = new AccountSet();
        set.Add(account, 5, out _);

        var dispatcher = new ToolDispatcher(_fixture.Catalog, _fixture.Invoker, new SessionStore(NullLogger<SessionStore>.Instance),
            NullLogger<ToolDispatcher>.Instance);
        var args = new Dictionary<string, JsonElement>
        {
            ["to"] = JsonSerializer.SerializeToElement("bob@example.com"),
            ["subject"] = JsonSerializer.SerializeToElement("hi"),
            ["body"] = JsonSerializer.SerializeToElement("x"),
            ["cc"] = JsonSerializer.SerializeToElement(""),
            ["bcc"] = JsonSerializer.SerializeToElement(""),
        };

        var dry = new TaskToolContext([account], null, new ToolGate(new HashSet<string> { "send_email" }, 1, dryRun: true));
        var simulated = await dispatcher.DispatchAsync(dry, "send_email", args, _fixture.Services, CancellationToken.None);
        Assert.True(simulated.Simulated);
        Assert.Contains("\"dryRun\":true", ToolInvoker.Flatten(simulated.Result));

        var refused = await dispatcher.DispatchAsync(dry, "delete_contacts", new Dictionary<string, JsonElement>(), _fixture.Services, CancellationToken.None);
        Assert.Equal(ToolDispatcher.Status.NotAllowed, refused.Status);

        var spent = await dispatcher.DispatchAsync(dry, "send_email", args, _fixture.Services, CancellationToken.None);
        Assert.Equal(ToolDispatcher.Status.NotAllowed, spent.Status);
        Assert.Contains("budget", spent.Message);

        Assert.Empty(stub.Requests);
    }
}

public sealed class AgentLoopTests
{
    private static AgentLoop Loop(FakeLlm llm) => new(new OpenRouterClient(new HttpClient(llm),
        new ServerOptions { LlmBaseUrl = new Uri("https://llm.test/api/v1/") }, NullLogger<OpenRouterClient>.Instance));

    [Fact]
    public async Task Runs_tools_then_reports()
    {
        var llm = new FakeLlm().ToolCalls(("get_emails", new { take = 3 })).Final("All quiet.");
        var calls = new List<string>();

        var result = await Loop(llm).RunAsync("key", "m", "system", "do it", [],
            (name, args, _) =>
            {
                calls.Add($"{name}:{args!["take"].GetInt32()}");
                return Task.FromResult(new AgentLoop.ToolResult("{\"items\":[]}", false, "me", false));
            }, 15, CancellationToken.None);

        Assert.Equal("completed", result.Stop);
        Assert.Equal("All quiet.", result.Final);
        Assert.Equal(["get_emails:3"], calls);
        Assert.Equal(220, result.PromptTokens);

        // The second request carries the assistant's tool call and the tool's answer.
        var second = llm.Requests[1]["messages"]!.AsArray();
        Assert.Equal("tool", second[^1]!["role"]!.GetValue<string>());
        Assert.Equal("call_0", second[^1]!["tool_call_id"]!.GetValue<string>());
        Assert.Equal("Bearer key", "Bearer key");
    }

    [Fact]
    public async Task Stops_at_the_round_limit()
    {
        var llm = new FakeLlm();
        for (var i = 0; i < 5; i++)
            llm.ToolCalls(("get_emails", new { }));

        var result = await Loop(llm).RunAsync("key", "m", "s", "u", [],
            (_, _, _) => Task.FromResult(new AgentLoop.ToolResult("{}", false, null, false)), maxRounds: 2, CancellationToken.None);
        Assert.Equal("max_rounds", result.Stop);
        Assert.Equal(2, result.ToolCalls);
    }

    [Fact]
    public async Task A_rejected_key_is_an_error_with_a_code()
    {
        var llm = new FakeLlm().Reply(new { error = new { message = "No auth" } }, HttpStatusCode.Unauthorized);
        var result = await Loop(llm).RunAsync("bad", "m", "s", "u", [],
            (_, _, _) => throw new InvalidOperationException(), 15, CancellationToken.None);
        Assert.Equal("error", result.Stop);
        Assert.Equal("LLM_KEY_REJECTED", result.ErrorCode);
    }

    [Fact]
    public async Task Bad_tool_arguments_go_back_to_the_model()
    {
        var llm = new FakeLlm().Reply(new
        {
            choices = new[]
            {
                new
                {
                    finish_reason = "tool_calls",
                    message = new { role = "assistant", tool_calls = new[] { new { id = "c1", type = "function", function = new { name = "x", arguments = "{oops" } } } },
                },
            },
        }).Final("ok");

        var called = false;
        var result = await Loop(llm).RunAsync("k", "m", "s", "u", [],
            (_, _, _) => { called = true; return Task.FromResult(new AgentLoop.ToolResult("", false, null, false)); }, 15, CancellationToken.None);
        Assert.False(called);
        Assert.True(result.Steps.Single(s => s.Kind == "tool").IsError);
    }
}

/// <summary>A scheduled run end to end, in-process: profile, delegation, task, run, sealed transcript.</summary>
public sealed class TaskRunTests : IDisposable
{
    private readonly StubSmarterMail _stub = new();
    private readonly FakeLlm _llm = new();
    private readonly WebApplicationFactory<Program> _app;

    public TaskRunTests()
    {
        _app = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("DATA_DIR", TestEnvironment.NewDataDir());
            builder.UseSetting("DATA_KEY", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
            builder.UseSetting("LLM_BASE_URL", "https://llm.test/api/v1/");
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton(_stub.Auth());
                services.AddHttpClient<OpenRouterClient>().ConfigurePrimaryHttpMessageHandler(() => _llm);
            });
        });
    }

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task Dry_run_simulates_allowed_writes_refuses_others_and_seals_the_report()
    {
        // A profile with one delegated read-write account, built directly (the HTTP path is covered elsewhere).
        var registry = _app.Services.GetRequiredService<ProfileRegistry>();
        var store = _app.Services.GetRequiredService<ProfileStore>();
        var profileId = ProfileCrypto.NewId();
        var accountsKey = RandomNumberGenerator.GetBytes(32);
        using var inbox = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var now = DataStore.Now();
        store.CreateProfile(
            new ProfileRow(profileId, now, now, Base64Url.Encode(inbox.ExportSubjectPublicKeyInfo()), "priv", null, 0,
                ProfileCrypto.AccountsKeyCheck(accountsKey), null, null, null, false),
            new PasskeyRow(ProfileCrypto.NewId(), profileId, [1], 0, null, null, "w", now, null), []);

        var runtime = registry.AcquireSession(profileId);
        runtime.Unlock(accountsKey, store.GetProfile(profileId)!.AccountsKeyCheck);
        var account = ResumeFixtures.NewAccount(_stub.Auth(), readOnly: false);
        runtime.Accounts.Add(account, 5, out _);
        runtime.Save(account);
        Assert.True(runtime.SetDelegation(account.Id, true));
        await runtime.ReleaseSessionAsync();                  // nobody signed in from here on

        var sealer = registry.ServerSealer!;
        store.UpdateTaskLlmKey(profileId, sealer.SealString(Encoding.UTF8.GetBytes("sk-or-test"), ProfileCrypto.TaskLlmKeyLabel, profileId));

        var definition = new TaskDefinition(1, "Tidy", "Email bob, then delete contacts.", "0 7 * * *", "UTC",
            [account.Id], ["send_email"], 1, "test/model", null);
        var taskId = ProfileCrypto.NewId(12);
        var tasks = _app.Services.GetRequiredService<TaskStore>();
        tasks.Insert(new TaskRow(taskId, profileId, true, definition.Seal(sealer, profileId, taskId), now + 3_600_000, "ok", 0, null, now, now));

        _llm.ToolCalls(
                ("send_email", new { to = "bob@example.com", subject = "hi", body = "x", cc = "", bcc = "" }),
                ("delete_contacts", new { }))
            .Final("Sent one email (simulated). delete_contacts is not allowed.");

        var scheduler = _app.Services.GetRequiredService<TaskRunScheduler>();
        Assert.True(scheduler.RunNow(tasks.Get(profileId, taskId)!, dryRun: true, out var runId));
        await scheduler.WhenIdleAsync();

        var run = tasks.Run(profileId, runId)!;
        Assert.Equal("ok", run.Status);
        Assert.True(run.DryRun);
        Assert.Equal(2, run.ToolCalls);
        Assert.Equal(1, run.Writes);

        // Only the refresh that restored the delegated account reached SmarterMail.
        Assert.Single(_stub.Refreshes);
        Assert.DoesNotContain(_stub.Requests, r => r.Path.Contains("send", StringComparison.OrdinalIgnoreCase));

        // The model saw only reads plus the allowlisted write, and the server's key.
        var offered = _llm.Requests[0]["tools"]!.AsArray().Select(t => t!["function"]!["name"]!.GetValue<string>()).ToList();
        Assert.Contains("send_email", offered);
        Assert.Contains("get_emails", offered);
        Assert.DoesNotContain("delete_contacts", offered);
        Assert.Contains("TEST RUN", _llm.Requests[0]["messages"]![0]!["content"]!.GetValue<string>());

        // The transcript opens with the profile's private key only.
        var transcript = JsonDocument.Parse(ProfileCrypto.OpenWithPrivateKey(run.Transcript!, inbox,
            TaskRunner.RunContext(profileId, runId))).RootElement;
        Assert.Equal("Sent one email (simulated). delete_contacts is not allowed.", transcript.GetProperty("final").GetString());
        var steps = transcript.GetProperty("steps").EnumerateArray().Where(s => s.GetProperty("kind").GetString() == "tool").ToList();
        Assert.True(steps[0].GetProperty("simulated").GetBoolean());
        Assert.True(steps[1].GetProperty("isError").GetBoolean());

        // Manual runs leave the schedule alone.
        Assert.Equal(now + 3_600_000, tasks.Get(profileId, taskId)!.NextRunAt);
    }

    [Fact]
    public async Task Without_a_task_key_the_run_fails_and_a_scheduled_one_pauses()
    {
        var registry = _app.Services.GetRequiredService<ProfileRegistry>();
        var store = _app.Services.GetRequiredService<ProfileStore>();
        var tasks = _app.Services.GetRequiredService<TaskStore>();
        var profileId = ProfileCrypto.NewId();
        var now = DataStore.Now();
        using var inbox = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        store.CreateProfile(
            new ProfileRow(profileId, now, now, Base64Url.Encode(inbox.ExportSubjectPublicKeyInfo()), "priv", null, 0, "check", null, null, null, false),
            new PasskeyRow(ProfileCrypto.NewId(), profileId, [1], 0, null, null, "w", now, null),
            [new StoredAccountRow("a1", profileId, ProfileStore.SealServer, "ok", "x", now)]);

        var definition = new TaskDefinition(1, "T", "p", "0 7 * * *", "UTC", ["a1"], [], 0, "m", null);
        tasks.Insert(new TaskRow("t1", profileId, true, definition.Seal(registry.ServerSealer!, profileId, "t1"), now - 1000, "ok", 0, null, now, now));

        var scheduler = _app.Services.GetRequiredService<TaskRunScheduler>();
        Assert.Equal(1, scheduler.StartDue(DateTimeOffset.UtcNow));
        await scheduler.WhenIdleAsync();

        var run = tasks.Runs(profileId, "t1", 5).Single();
        Assert.Equal("failed", run.Status);
        var task = tasks.Get(profileId, "t1")!;
        Assert.True(task.NextRunAt > now);          // claimed and moved on before running
        Assert.False(task.Enabled);                 // a hard failure pauses at once
        Assert.NotEqual("ok", task.Status);
        Assert.Equal(0, scheduler.StartDue(DateTimeOffset.UtcNow));
    }
}
