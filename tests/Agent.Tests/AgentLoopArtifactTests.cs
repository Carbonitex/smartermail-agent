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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SmarterMailAgent.Auth;
using SmarterMailAgent.Llm;
using SmarterMailAgent.Llm.Artifacts;
using SmarterMailAgent.Profiles;
using SmarterMailAgent.Server;
using SmarterMailAgent.Storage;
using SmarterMailAgent.Tasks;
using SmarterMailMcp.Core.Models;

namespace SmarterMailAgent.Tests;

/// <summary>Keeps every formatted log line, for "never logged" assertions.</summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    public List<string> Lines { get; } = [];

    public ILogger CreateLogger(string categoryName) => new Logger(this);

    public void Dispose() { }

    private sealed class Logger(CapturingLoggerProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (owner.Lines)
                owner.Lines.Add(formatter(state, exception) + (exception is null ? "" : " " + exception));
        }
    }
}

public sealed class AgentLoopArtifactTests
{
    private const string Question = "How many lines carry a rsp: 550 marker-question-xyz?";

    private static (AgentLoop Loop, CapturingLoggerProvider Logs) Loop(FakeLlm llm)
    {
        var logs = new CapturingLoggerProvider();
        var factory = LoggerFactory.Create(b => b.AddProvider(logs));
        var client = new OpenRouterClient(new HttpClient(llm), new ServerOptions { LlmBaseUrl = new Uri("https://llm.test/api/v1/") },
            NullLogger<OpenRouterClient>.Instance);
        return (new AgentLoop(client, factory.CreateLogger<AgentLoop>()), logs);
    }

    /// <summary>~3 MB of SMTP log, wrapped the way search_log_files returns it.</summary>
    private static string BigLog()
    {
        var sb = new StringBuilder();
        for (var i = 0; i < 40_000; i++)
            sb.Append($"00:{i / 60 % 60:00}:{i % 60:00}.000 [{i % 900}] rsp: {(i % 13 == 0 ? 550 : 250)} for user{i}@example.com marker-content-abc padding padding\r\n");
        return JsonSerializer.Serialize(new { success = true, logType = "smtpLog", totalChars = sb.Length, content = sb.ToString() });
    }

    private static JsonArray Tools() =>
    [
        new JsonObject { ["type"] = "function", ["function"] = new JsonObject { ["name"] = "search_log_files", ["description"] = "d", ["parameters"] = new JsonObject { ["type"] = "object" } } },
    ];

    [Fact]
    public async Task A_large_result_becomes_a_stub_and_analyze_result_runs_a_nested_loop()
    {
        var big = BigLog();
        Assert.True(big.Length > 3_000_000);
        var llm = new FakeLlm()
            .ToolCalls(("search_log_files", new { type = "smtpLog", account = "sysadmin:admin@mail.example.com" }))
            .ToolCalls(("analyze_result", new { artifact = "r1", question = Question }))
            .ToolCalls(("artifact_count", new { pattern = "rsp: 550" }))     // the sub-agent
            .Final("3077 lines carry a 550.")                                 // the sub-agent's answer
            .Final("There were 3,077 failures.");
        var (loop, logs) = Loop(llm);
        var tools = Tools();

        var result = await loop.RunAsync("key", "main/model", "system", "check the log", tools,
            (_, _, _) => Task.FromResult(new AgentLoop.ToolResult(big, false, "admin", false)), 15, CancellationToken.None,
            sessionId: "sma-task-run-1", analysis: new ArtifactAnalysis("test/analysis"));

        Assert.Equal("completed", result.Stop);
        Assert.Equal("There were 3,077 failures.", result.Final);
        Assert.Single(tools);   // the caller's tool list is not modified

        // Main requests: analyze_result appended last, same tools and a growing prefix every round.
        var main = new[] { llm.Requests[0], llm.Requests[1], llm.Requests[4] };
        foreach (var body in main)
        {
            Assert.Equal("main/model", body["model"]!.GetValue<string>());
            Assert.Equal("analyze_result", body["tools"]!.AsArray()[^1]!["function"]!["name"]!.GetValue<string>());
            Assert.Null(body["reasoning"]);
            Assert.True(body.ToJsonString().Length < 30_000, "the raw log never reaches the main model");
        }
        for (var k = 1; k < main.Length; k++)
        {
            Assert.Equal(main[k - 1]["tools"]!.ToJsonString(), main[k]["tools"]!.ToJsonString());
            var prev = main[k - 1]["messages"]!.AsArray();
            var cur = main[k]["messages"]!.AsArray();
            for (var i = 0; i < prev.Count; i++)
                Assert.Equal(prev[i]!.ToJsonString(), cur[i]!.ToJsonString());
        }
        Assert.Contains("# Large results", main[0]["messages"]![0]!["content"]!.GetValue<string>());
        var stub = main[1]["messages"]!.AsArray()[^1]!["content"]!.GetValue<string>();
        Assert.StartsWith("{\"artifact\":\"r1\",\"tool\":\"search_log_files\"", stub);
        Assert.True(stub.Length < 5000);

        // The nested loop: the analysis model, low reasoning, only the operators, its own session id.
        var sub = llm.Requests[2];
        Assert.Equal("test/analysis", sub["model"]!.GetValue<string>());
        Assert.Equal("low", sub["reasoning"]!["effort"]!.GetValue<string>());
        Assert.Equal(ArtifactOperators.Names, sub["tools"]!.AsArray().Select(t => t!["function"]!["name"]!.GetValue<string>()));
        Assert.Equal("sma-task-run-1-analysis", sub["session_id"]!.GetValue<string>());
        var counted = llm.Requests[3]["messages"]!.AsArray()[^1]!["content"]!.GetValue<string>();
        Assert.StartsWith("3077 matching lines of 40000", counted);

        // The answer goes back to the main model with how it was found.
        var answer = main[2]["messages"]!.AsArray()[^1]!["content"]!.GetValue<string>();
        Assert.StartsWith("3077 lines carry a 550.", answer);
        Assert.Contains("[analysis of artifact r1 by test/analysis: 1 round, 1 operator call]", answer);

        // Tokens include the sub-agent's; the transcript holds the stub, never the log.
        Assert.Equal(100 + 100 + 100 + 120 + 120, result.PromptTokens);
        Assert.Equal(2, result.ToolCalls);
        var steps = result.Steps.Where(s => s.Kind == "tool").ToList();
        Assert.Equal(["search_log_files", "analyze_result: artifact_count", "analyze_result"], steps.Select(s => s.Tool));
        Assert.True(JsonSerializer.Serialize(result.Steps).Length < 20_000);
        Assert.DoesNotContain("marker-content-abc padding padding\\r\\n00", JsonSerializer.Serialize(result.Steps));

        // Logs: sizes and counts, never the question, the pattern or any content.
        Assert.Contains(logs.Lines, l => l.Contains("analyze_result on a") && l.Contains("1 round(s)"));
        Assert.DoesNotContain(logs.Lines, l => l.Contains("marker-question-xyz") || l.Contains("rsp: 550") || l.Contains("marker-content"));
    }

    [Fact]
    public async Task The_round_cap_forces_an_answer_without_tools()
    {
        var llm = new FakeLlm()
            .ToolCalls(("search_log_files", new { }))
            .ToolCalls(("analyze_result", new { artifact = "r1", question = "count the 550s" }))
            .ToolCalls(("artifact_info", new { }))
            .ToolCalls(("artifact_info", new { }))
            .Final("Partial answer.")
            .Final("Done.");
        var (loop, _) = Loop(llm);

        var result = await loop.RunAsync("key", "m", "s", "u", Tools(),
            (_, _, _) => Task.FromResult(new AgentLoop.ToolResult(BigLog(), false, null, false)), 15, CancellationToken.None,
            analysis: new ArtifactAnalysis("a", MaxRounds: 2));

        Assert.Equal("completed", result.Stop);
        var final = llm.Requests[4];
        Assert.Equal("none", final["tool_choice"]!.GetValue<string>());
        Assert.Contains("round limit", final["messages"]!.AsArray()[^1]!["content"]!.GetValue<string>());
        Assert.Contains("partial: round limit reached", llm.Requests[5]["messages"]!.AsArray()[^1]!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task Unknown_or_spent_artifacts_are_errors_the_model_can_read()
    {
        var llm = new FakeLlm()
            .ToolCalls(("analyze_result", new { artifact = "r4", question = "q" }))
            .ToolCalls(("analyze_result", new { artifact = "r1" }))
            .Final("ok");
        var (loop, _) = Loop(llm);
        var result = await loop.RunAsync("key", "m", "s", "u", Tools(),
            (_, _, _) => throw new InvalidOperationException("analyze_result must not reach the dispatcher"), 15, CancellationToken.None,
            analysis: new ArtifactAnalysis("a"));

        var errors = result.Steps.Where(s => s.Kind == "tool").ToList();
        Assert.All(errors, s => Assert.True(s.IsError));
        Assert.Contains("There is no artifact r4", errors[0].Content);
        Assert.Contains("needs both", errors[1].Content);
        Assert.Equal(3, llm.Requests.Count);   // no analysis request was made
    }

    [Fact]
    public async Task Without_analysis_results_are_clamped_and_the_tools_are_unchanged()
    {
        var llm = new FakeLlm().ToolCalls(("search_log_files", new { })).Final("ok");
        var (loop, _) = Loop(llm);
        await loop.RunAsync("key", "m", "s", "u", Tools(),
            (_, _, _) => Task.FromResult(new AgentLoop.ToolResult(new string('x', 70_000), false, null, false)), 15, CancellationToken.None);
        Assert.Single(llm.Requests[0]["tools"]!.AsArray());
        Assert.Contains("[truncated: 10000 more characters]", llm.Requests[1]["messages"]!.AsArray()[^1]!["content"]!.GetValue<string>());
    }
}

/// <summary>A scheduled run end to end with a large result: the task key pays for the analysis model too.</summary>
public sealed class TaskAnalysisRunTests : IDisposable
{
    private readonly StubSmarterMail _stub = new();
    private readonly FakeLlm _llm = new();
    private readonly CapturingLoggerProvider _logs = new();
    private readonly WebApplicationFactory<Program> _app;

    public TaskAnalysisRunTests()
    {
        _app = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("DATA_DIR", TestEnvironment.NewDataDir());
            builder.UseSetting("DATA_KEY", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
            builder.UseSetting("LLM_BASE_URL", "https://llm.test/api/v1/");
            builder.UseSetting("TASK_ANALYSIS_MODEL", "test/analysis");
            builder.ConfigureLogging(l => l.AddProvider(_logs));
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton(_stub.Auth());
                services.AddHttpClient<OpenRouterClient>().ConfigurePrimaryHttpMessageHandler(() => _llm);
            });
        });
    }

    public void Dispose() => _app.Dispose();

    /// <summary>SmarterMail answering user-startup-data with a large payload (get_user_data passes it through).</summary>
    private sealed class BigUserData : HttpMessageHandler
    {
        public static readonly string Entries = string.Concat(Enumerable.Range(0, 3000).Select(i =>
            $"{i:000000} entry for user{i}@example.com {(i % 10 == 0 ? "status-failed" : "status-ok")} secret-content-marker\n"));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { name = "x", entries = Entries }) });
    }

    [Fact]
    public async Task A_run_keeps_the_large_result_as_an_artifact_and_asks_the_analysis_model()
    {
        var registry = _app.Services.GetRequiredService<ProfileRegistry>();
        var store = _app.Services.GetRequiredService<ProfileStore>();
        var profileId = ProfileCrypto.NewId();
        var accountsKey = RandomNumberGenerator.GetBytes(32);
        using var inbox = System.Security.Cryptography.ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var now = DataStore.Now();
        store.CreateProfile(
            new ProfileRow(profileId, now, now, Base64Url.Encode(inbox.ExportSubjectPublicKeyInfo()), "priv", null, 0,
                ProfileCrypto.AccountsKeyCheck(accountsKey), null, null, null, false),
            new PasskeyRow(ProfileCrypto.NewId(), profileId, [1], 0, null, null, "w", now, null), []);

        // A signed-in session keeps the account live, so the run borrows it (and its stubbed HTTP client).
        var runtime = registry.AcquireSession(profileId);
        runtime.Unlock(accountsKey, store.GetProfile(profileId)!.AccountsKeyCheck);
        var account = ResumeFixtures.NewAccount(_stub.Auth(), readOnly: true);
        typeof(UserContext).GetField("_httpClient", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(account.UserContext, new HttpClient(new BigUserData()) { BaseAddress = new Uri(account.BaseUrl) });
        runtime.Accounts.Add(account, 5, out _);
        runtime.Save(account);
        Assert.True(runtime.SetDelegation(account.Id, true));

        var sealer = registry.ServerSealer!;
        store.UpdateTaskLlmKey(profileId, sealer.SealString(Encoding.UTF8.GetBytes("sk-or-test"), ProfileCrypto.TaskLlmKeyLabel, profileId));
        var definition = new TaskDefinition(1, "Failures", "How many entries failed?", "0 7 * * *", "UTC",
            [account.Id], [], 0, "test/model", null);
        var taskId = ProfileCrypto.NewId(12);
        var tasks = _app.Services.GetRequiredService<TaskStore>();
        tasks.Insert(new TaskRow(taskId, profileId, true, definition.Seal(sealer, profileId, taskId), now + 3_600_000, "ok", 0, null, now, now));

        _llm.ToolCalls(("get_user_data", new { }))
            .ToolCalls(("analyze_result", new { artifact = "r1", question = "How many entries have status-failed?" }))
            .ToolCalls(("artifact_count", new { pattern = "status-failed" }))
            .Final("300 entries failed.")
            .Final("300 entries failed.");

        var scheduler = _app.Services.GetRequiredService<TaskRunScheduler>();
        Assert.True(scheduler.RunNow(tasks.Get(profileId, taskId)!, dryRun: false, out var runId));
        await scheduler.WhenIdleAsync();
        await runtime.ReleaseSessionAsync();

        var run = tasks.Run(profileId, runId)!;
        Assert.Equal("ok", run.Status);
        Assert.Equal(2, run.ToolCalls);
        Assert.Equal(5, _llm.Requests.Count);
        Assert.Equal("test/model", _llm.Requests[0]["model"]!.GetValue<string>());
        Assert.Equal("test/analysis", _llm.Requests[2]["model"]!.GetValue<string>());
        Assert.StartsWith("300 matching lines of 3000", _llm.Requests[3]["messages"]!.AsArray()[^1]!["content"]!.GetValue<string>());
        Assert.StartsWith("{\"artifact\":\"r1\",\"tool\":\"get_user_data\"",
            _llm.Requests[1]["messages"]!.AsArray()[^1]!["content"]!.GetValue<string>());

        var transcript = Encoding.UTF8.GetString(ProfileCrypto.OpenWithPrivateKey(run.Transcript!, inbox, TaskRunner.RunContext(profileId, runId)));
        Assert.Contains("analyze_result: artifact_count", transcript);
        Assert.True(transcript.Length < 30_000, $"transcript is {transcript.Length} chars");

        lock (_logs.Lines)
        {
            Assert.Contains(_logs.Lines, l => l.Contains("kept as an artifact"));
            Assert.DoesNotContain(_logs.Lines, l => l.Contains("status-failed") || l.Contains("secret-content-marker"));
        }
    }
}
