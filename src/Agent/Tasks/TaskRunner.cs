using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SmarterMailAgent.Auth;
using SmarterMailAgent.Llm;
using SmarterMailAgent.Mcp;
using SmarterMailAgent.Profiles;
using SmarterMailAgent.Server;
using SmarterMailAgent.Storage;

namespace SmarterMailAgent.Tasks;

/// <summary>
/// Runs one scheduled task, start to finish: open its definition, borrow its delegated accounts from
/// the profile, run the tool loop under the task's gate, seal the transcript to the profile's public
/// key, optionally mail the report, and record the outcome. Logs task id, outcome and counts only —
/// never prompts, tool arguments, results or accounts.
/// </summary>
public sealed class TaskRunner(
    TaskStore tasks,
    ProfileStore profiles,
    ProfileRegistry registry,
    AccountRestorer restorer,
    ToolCatalog catalog,
    ToolDispatcher dispatcher,
    AgentLoop loop,
    ServerOptions options,
    IServiceProvider services,
    ILogger<TaskRunner> logger)
{
    /// <summary>Consecutive failures that pause a task.</summary>
    public const int PauseAfterFailures = 3;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public sealed record Transcript(
        int Version, string TaskName, string Prompt, string Model, bool DryRun, DateTimeOffset StartedAt,
        string Stop, string? Final, IReadOnlyList<AgentLoop.Step> Steps, string? Error, bool Emailed);

    /// <summary>Starts a run row and runs it. <paramref name="runId"/> is the row the caller already reported.</summary>
    public async Task RunAsync(TaskRow task, string runId, string trigger, bool dryRun, CancellationToken stopping)
    {
        var started = DateTimeOffset.UtcNow;
        // Looked up before this run's own row exists; test runs never count as "the previous run".
        var (lastOk, latest) = tasks.PreviousRuns(task.Id, runId);
        tasks.StartRun(new TaskRunRow(runId, task.Id, task.ProfileId, started.ToUnixTimeMilliseconds(), null, "running",
            dryRun, trigger, null, 0, 0, null, null, null, false));

        string status = "failed", code = "ERROR";
        AgentLoop.Result? result = null;
        TaskDefinition? definition = null;
        ProfileRow? profile = null;
        var emailed = false;

        try
        {
            (definition, profile, var failure) = Open(task);
            if (failure is not null)
            {
                code = failure;
                return;
            }

            var runtime = registry.AcquireTask(task.ProfileId);
            try
            {
                // The key first: without one there is no point rotating any account's token.
                var key = OpenLlmKey(profile!);
                if (key is null)
                {
                    code = "NO_TASK_KEY";
                    return;
                }

                var (accounts, accountFailure) = await AccountsAsync(runtime, definition!, stopping);
                if (accountFailure is not null)
                {
                    code = accountFailure;
                    return;
                }

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping);
                timeout.CancelAfter(options.TaskTimeout);

                var gate = new ToolGate(definition!.AllowedWrites.ToHashSet(StringComparer.Ordinal), definition.MaxWrites, dryRun);
                var context = new TaskToolContext(accounts, runtime, gate);
                var zone = TaskDefinition.TryParseSchedule(definition.Cron, definition.TimeZone, out _, out var z) ? z : TimeZoneInfo.Utc;
                var prompt = TaskPrompt.Build(definition.Name, accounts, definition.AllowedWrites, definition.MaxWrites, dryRun,
                    DateTimeOffset.UtcNow, zone, new TaskPrompt.History(
                        lastOk is null ? null : DateTimeOffset.FromUnixTimeMilliseconds(lastOk.StartedAt),
                        latest is not null && latest.Status != "ok" && (lastOk is null || latest.StartedAt > lastOk.StartedAt)
                            ? DateTimeOffset.FromUnixTimeMilliseconds(latest.StartedAt) : null));

                result = await loop.RunAsync(key, definition.Model, prompt, definition.Prompt, ToolsFor(accounts, gate.AllowedWrites),
                    async (name, arguments, ct) =>
                    {
                        var outcome = await dispatcher.DispatchAsync(context, name, arguments, services, ct);
                        return new AgentLoop.ToolResult(ToolInvoker.Flatten(outcome.Result),
                            outcome.Status != ToolDispatcher.Status.Ok || (outcome.Result.IsError ?? false), outcome.Account, outcome.Simulated);
                    },
                    options.TaskMaxToolRounds, timeout.Token, sessionId: $"sma-task-run-{runId}");

                (status, code) = result.Stop switch
                {
                    "completed" => ("ok", "ok"),
                    "max_rounds" or "length" or "content_filter" => ("ok", result.Stop.ToUpperInvariant()),
                    _ => ("failed", result.ErrorCode ?? "ERROR"),
                };

                if (status == "ok" && !dryRun && definition.EmailAccountId is { } emailId &&
                    accounts.FirstOrDefault(a => a.Id == emailId) is { } mailbox)
                {
                    emailed = await EmailAsync(mailbox, definition.Name, result.Final, stopping);
                }

                tasks.FinishRun(runId, status, status == "ok" ? (code == "ok" ? null : code) : code, result.ToolCalls, gate.Writes,
                    result.PromptTokens, result.CompletionTokens,
                    SealTranscript(profile!, runId, definition, dryRun, started, result, emailed));
                return;
            }
            finally
            {
                await runtime.ReleaseTaskAsync();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !stopping.IsCancellationRequested)
        {
            logger.LogError(ex, "Task {Task} run failed unexpectedly.", task.Id);
            code = "ERROR";
        }
        finally
        {
            if (result is null)
            {
                var transcript = profile is not null && definition is not null
                    ? SealTranscript(profile, runId, definition, dryRun, started,
                        new AgentLoop.Result("error", null, [new AgentLoop.Step("notice", Explain(code))], 0, 0, 0, code, Explain(code)),
                        false)
                    : null;
                tasks.FinishRun(runId, "failed", code, 0, 0, null, null, transcript);
            }

            if (trigger == "schedule")
                tasks.RecordOutcome(task.Id, status == "ok", status == "ok" ? "ok" : code, PauseAfterFailures);
            tasks.Prune(task.Id, options.TaskRunRetention);

            logger.LogInformation(
                "Task {Task} {Trigger}{Dry} run finished: {Status} ({Code}), {Calls} tool call(s), {PromptTokens} prompt token(s), {CachedTokens} cached, {Seconds}s.",
                task.Id, trigger, dryRun ? " test" : "", status, code, result?.ToolCalls ?? 0,
                result?.PromptTokens ?? 0, result?.CachedTokens ?? 0,
                (int)(DateTimeOffset.UtcNow - started).TotalSeconds);
        }
    }

    private (TaskDefinition?, ProfileRow?, string?) Open(TaskRow task)
    {
        if (registry.ServerSealer is not { } sealer)
            return (null, null, "TASKS_DISABLED");
        var profile = profiles.GetProfile(task.ProfileId);
        if (profile is null)
            return (null, null, "PROFILE_GONE");
        var definition = TaskDefinition.Open(sealer, task.ProfileId, task.Id, task.Definition);
        return definition is null ? (null, profile, "DEFINITION_UNREADABLE") : (definition, profile, null);
    }

    /// <summary>The task's accounts, live: borrowed from an unlocked browser session, or restored from their server-sealed rows.</summary>
    private async Task<(IReadOnlyList<Account>, string?)> AccountsAsync(ProfileRuntime runtime, TaskDefinition definition, CancellationToken ct)
    {
        var wanted = definition.AccountIds.ToHashSet(StringComparer.Ordinal);
        var live = runtime.Accounts.Accounts.Where(a => wanted.Contains(a.Id)).Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        if (live.Count < wanted.Count)
            await runtime.RestoreAsync(restorer, SessionStore.MaxAccounts, ct, wanted.Except(live).ToHashSet(StringComparer.Ordinal));

        var accounts = new List<Account>();
        foreach (var id in definition.AccountIds)
        {
            var row = runtime.Row(id);
            if (row is null)
                return ([], "ACCOUNT_REMOVED");
            if (row.Seal != ProfileStore.SealServer)
                return ([], "ACCOUNT_NOT_DELEGATED");
            if (row.State == "rejected")
                return ([], "NEEDS_SIGN_IN");
            if (row.Entry is null)
                return ([], "ACCOUNT_UNREADABLE");
            if (runtime.Accounts.FindById(id) is not { } account)
                return ([], "ACCOUNT_UNAVAILABLE");
            accounts.Add(account);
        }

        return (accounts, null);
    }

    private string? OpenLlmKey(ProfileRow profile)
    {
        if (profile.TaskLlmKey is null || registry.ServerSealer is not { } sealer)
            return null;
        var plaintext = sealer.OpenString(profile.TaskLlmKey, ProfileCrypto.TaskLlmKeyLabel, profile.Id);
        return plaintext is null ? null : Encoding.UTF8.GetString(plaintext);
    }

    /// <summary>The function list the model sees: every read the accounts allow, plus the allowlisted writes.</summary>
    private JsonArray ToolsFor(IReadOnlyList<Account> accounts, IReadOnlySet<string> allowedWrites)
    {
        var tools = new JsonArray();
        foreach (var tool in catalog.List(accounts).Where(t => !t.Write || allowedWrites.Contains(t.Name)))
        {
            tools.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description.Length > 1024 ? tool.Description[..1024] : tool.Description,
                    ["parameters"] = tool.InputSchema.DeepClone(),
                },
            });
        }
        return tools;
    }

    /// <summary>
    /// The report, mailed by the server (not the model) from the delivery account to its own address.
    /// Goes through the dispatcher on a one-account context, so read-only and role rules still apply.
    /// </summary>
    private async Task<bool> EmailAsync(Account mailbox, string taskName, string? report, CancellationToken ct)
    {
        var body = $"{(string.IsNullOrWhiteSpace(report) ? "(The task finished without a report.)" : report)}\n\n" +
                   $"-- \nScheduled task \"{taskName}\", SmarterMail Agent.";
        var arguments = new Dictionary<string, JsonElement>
        {
            ["to"] = JsonSerializer.SerializeToElement(mailbox.EmailAddress),
            ["subject"] = JsonSerializer.SerializeToElement($"[SmarterMail Agent] {taskName}"),
            ["body"] = JsonSerializer.SerializeToElement(body),
            ["cc"] = JsonSerializer.SerializeToElement(""),
            ["bcc"] = JsonSerializer.SerializeToElement(""),
        };

        var outcome = await dispatcher.DispatchAsync(new TaskToolContext([mailbox], null, null), "send_email", arguments, services, ct);
        var ok = outcome.Status == ToolDispatcher.Status.Ok && !(outcome.Result.IsError ?? false) &&
                 !ToolInvoker.PayloadIndicatesFailure(ToolInvoker.Flatten(outcome.Result));
        if (!ok)
            logger.LogWarning("A task report could not be emailed ({Status}).", outcome.Status);
        return ok;
    }

    private static string SealTranscript(
        ProfileRow profile, string runId, TaskDefinition definition, bool dryRun, DateTimeOffset started, AgentLoop.Result result, bool emailed)
    {
        var transcript = new Transcript(1, definition.Name, definition.Prompt, definition.Model, dryRun, started,
            result.Stop, result.Final, result.Steps, result.ErrorMessage, emailed);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(transcript, Json);
        if (plaintext.Length > 256 * 1024)
        {
            // Keep the report; drop tool output until it fits.
            transcript = transcript with
            {
                Steps = transcript.Steps.Select(s => s with { Content = s.Kind == "tool" ? AgentLoop.Clamp(s.Content ?? "", 500) : s.Content }).ToList(),
            };
            plaintext = JsonSerializer.SerializeToUtf8Bytes(transcript, Json);
        }
        return ProfileCrypto.SealToPublicKey(plaintext, profile.PublicKey, RunContext(profile.Id, runId));
    }

    /// <summary>The associated data a run transcript is sealed with; the browser opens it with the same.</summary>
    public static string RunContext(string profileId, string runId) => $"task-run|{profileId}|{runId}";

    public static string Explain(string code) => code switch
    {
        "NO_TASK_KEY" => "No OpenRouter key is saved for scheduled tasks (Profile menu).",
        "NEEDS_SIGN_IN" => "SmarterMail no longer accepts the saved sign-in for one of the task's accounts. Sign in to it again.",
        "ACCOUNT_REMOVED" => "One of the task's accounts was removed from the profile.",
        "ACCOUNT_NOT_DELEGATED" => "One of the task's accounts no longer allows scheduled tasks.",
        "ACCOUNT_UNAVAILABLE" => "One of the task's mail servers did not answer.",
        "ACCOUNT_UNREADABLE" => "One of the task's accounts could not be opened with this server's key (DATA_KEY changed?).",
        "TASKS_DISABLED" => "Scheduled tasks are switched off on this server.",
        "DEFINITION_UNREADABLE" => "The task could not be opened with this server's key (DATA_KEY changed?).",
        "LLM_KEY_REJECTED" => "OpenRouter rejected the key saved for scheduled tasks.",
        "NO_CREDITS" => "The OpenRouter account for scheduled tasks is out of credits.",
        "TIMEOUT" => "The run hit its time limit.",
        _ => "The run failed.",
    };
}

/// <summary>A task run as a tool context: its own accounts only, lazy refresh, and its gate.</summary>
public sealed class TaskToolContext(IReadOnlyList<Account> accounts, ProfileRuntime? runtime, ToolGate? gate) : IToolContext
{
    public IReadOnlyList<Account> Accounts => accounts;
    public bool RefreshesLazily => true;
    public ToolGate? Gate => gate;

    public Task OnRejectedAsync(Account account) =>
        runtime?.MarkRejectedAsync(account) ?? Task.CompletedTask;
}
