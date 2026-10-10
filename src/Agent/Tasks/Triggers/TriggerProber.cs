using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using SmarterMailAgent.Auth;
using SmarterMailAgent.Llm;
using SmarterMailAgent.Mcp;
using SmarterMailAgent.Profiles;
using SmarterMailAgent.Server;
using SmarterMailAgent.Storage;

namespace SmarterMailAgent.Tasks.Triggers;

/// <summary>
/// Runs the probes of condition tasks. Every 30 seconds it claims the enabled condition tasks whose
/// <c>next_probe_at</c> has passed (moving it on first, so a probe never runs twice), and for each one,
/// at most <c>TRIGGER_CONCURRENCY</c> at once:
/// <list type="number">
///   <item>outside its "only between" window: moves to the window's next start, no probe;</item>
///   <item>takes a token from that mail server's bucket (none: retry at the next tick, not a failure);</item>
///   <item>borrows the probe account (restoring it if nobody holds it) and calls the read tool through
///   the dispatcher with a gate that admits no write;</item>
///   <item>evaluates the predicate, steps the trigger state, and fires: starts the LLM run with the
///   evidence as its seed (<see cref="TaskRunScheduler.StartTriggered"/>), or sends the alert email.</item>
/// </list>
/// Probe failures do not create run rows; five in a row (or one hard failure) pause the task and write
/// one failed run that says why. Triggers probing more often than every 15 minutes keep a task lease
/// on their profile, so the delegated accounts stay live between probes instead of being restored
/// (one token refresh) each time. Logs task ids, outcomes, durations and reasons only — never
/// arguments, results, evidence or predicate values. Single instance, like the run scheduler.
/// </summary>
public sealed class TriggerProber(
    TaskStore tasks,
    TriggerStore triggers,
    ProfileStore profiles,
    ProfileRegistry registry,
    AccountRestorer restorer,
    ProbeRunner probes,
    TaskRunScheduler scheduler,
    ToolDispatcher dispatcher,
    ProbeHostBucket bucket,
    InteractiveProbeLimiter interactive,
    TriggerOptions options,
    ServerOptions server,
    IServiceProvider services,
    ILogger<TriggerProber> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly SemaphoreSlim _slots = new(Math.Max(1, options.Concurrency));
    private readonly ConcurrentDictionary<string, Task> _inFlight = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _holdFor = new(StringComparer.Ordinal);   // task id -> profile id
    private readonly Dictionary<string, ProfileRuntime> _held = new(StringComparer.Ordinal);       // profile id -> lease
    private readonly ConcurrentDictionary<string, int> _deferrals = new(StringComparer.Ordinal);    // task id -> deferrals in a row
    private CancellationToken _stopping = CancellationToken.None;

    /// <summary>Scheduled probes of <paramref name="taskId"/> deferred in a row for the mail server's probe budget (in memory).</summary>
    public int Deferrals(string taskId) => _deferrals.GetValueOrDefault(taskId);

    /// <summary>The outcome of "Run now" on a condition task.</summary>
    public sealed record RunNowResult(string? RunId, string? Code, string? Message);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stopping = stoppingToken;
        using var timer = new PeriodicTimer(TaskRunScheduler.Tick);
        do
        {
            try
            {
                ProbeDue(DateTimeOffset.UtcNow);
                await ReconcileLeasesAsync();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Trigger probing pass failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        List<ProfileRuntime> held;
        lock (_held)
        {
            held = [.. _held.Values];
            _held.Clear();
        }
        foreach (var runtime in held)
            await runtime.ReleaseTaskAsync();
    }

    /// <summary>One pass: claim and start the probes due at <paramref name="now"/>. Returns how many started.</summary>
    internal int ProbeDue(DateTimeOffset now)
    {
        if (!options.Enabled || registry.ServerSealer is not { } sealer)
            return 0;

        var nowMs = now.ToUnixTimeMilliseconds();
        var started = 0;
        foreach (var row in triggers.Due(nowMs, limit: 50))
        {
            var task = row.Task;
            if (_inFlight.ContainsKey(task.Id))
                continue;

            var definition = TaskDefinition.Open(sealer, task.ProfileId, task.Id, task.Definition);
            if (definition?.Trigger is not { } trigger)
            {
                if (!triggers.Claim(task.Id, row.NextProbeAt, nowMs + (long)TriggerStore.Day.TotalMilliseconds))
                    continue;
                if (definition is null)
                    Fail(task, null, "DEFINITION_UNREADABLE", nowMs);
                else
                    triggers.Reset(task.ProfileId, task.Id, null);   // a cron task: not ours
                continue;
            }

            if (!TriggerSchedule.InWindow(trigger.ActiveHours, definition.TimeZone, now))
            {
                var opens = TriggerSchedule.NextWindowStart(trigger.ActiveHours!, definition.TimeZone, now) ?? now.Add(TriggerStore.Day);
                triggers.Claim(task.Id, row.NextProbeAt, opens.ToUnixTimeMilliseconds());
                continue;
            }

            var next = now.AddMinutes(trigger.EveryMinutes * (0.9 + 0.2 * Random.Shared.NextDouble()));
            if (!triggers.Claim(task.Id, row.NextProbeAt, next.ToUnixTimeMilliseconds()))
                continue;

            if (Launch(task.Id, () => ProbeAsync(row, definition, trigger, now)))
                started++;
        }
        return started;
    }

    /// <summary>Waits for the probes in flight (tests, and a graceful shutdown).</summary>
    internal Task WhenIdleAsync() => Task.WhenAll(_inFlight.Values);

    /// <summary>Whether this prober holds a lease on the profile's runtime between probes.</summary>
    internal bool IsHolding(string profileId)
    {
        lock (_held)
            return _held.ContainsKey(profileId);
    }

    /// <summary>The task was saved (its interval may have gone up, or it was disabled): the next reconcile decides about its lease.</summary>
    public void Forget(string taskId)
    {
        _holdFor.TryRemove(taskId, out _);
        _deferrals.TryRemove(taskId, out _);
    }

    /// <summary>Releases the leases no enabled sub-15-minute trigger needs any more.</summary>
    internal async Task ReconcileLeasesAsync()
    {
        var enabled = triggers.Enabled().Select(e => e.TaskId).ToHashSet(StringComparer.Ordinal);
        foreach (var taskId in _holdFor.Keys.Where(id => !enabled.Contains(id)).ToList())
            _holdFor.TryRemove(taskId, out _);

        var wanted = _holdFor.Values.ToHashSet(StringComparer.Ordinal);
        var release = new List<ProfileRuntime>();
        lock (_held)
        {
            foreach (var profileId in _held.Keys.Where(p => !wanted.Contains(p)).ToList())
            {
                release.Add(_held[profileId]);
                _held.Remove(profileId);
            }
        }
        foreach (var runtime in release)
            await runtime.ReleaseTaskAsync();
        if (release.Count > 0)
            logger.LogInformation("Released {Count} trigger lease(s).", release.Count);
    }

    private void Hold(string taskId, string profileId)
    {
        _holdFor[taskId] = profileId;
        lock (_held)
        {
            if (!_held.ContainsKey(profileId))
                _held[profileId] = registry.AcquireTask(profileId);
        }
    }

    private bool Launch(string taskId, Func<Task> work)
    {
        var done = new TaskCompletionSource();
        if (!_inFlight.TryAdd(taskId, done.Task))
            return false;

        _ = Task.Run(async () =>
        {
            var slot = false;
            try
            {
                await _slots.WaitAsync(_stopping);
                slot = true;
                await work();
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
            {
                // Shutting down.
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Task {Task} probe crashed.", taskId);
            }
            finally
            {
                if (slot)
                    _slots.Release();
                _inFlight.TryRemove(taskId, out _);
                done.TrySetResult();
            }
        }, CancellationToken.None);
        return true;
    }

    // ------------------------------------------------------------------ one probe

    private async Task ProbeAsync(ProbeRow row, TaskDefinition definition, TaskTrigger trigger, DateTimeOffset now)
    {
        var task = row.Task;
        var nowMs = now.ToUnixTimeMilliseconds();
        var sealer = registry.ServerSealer!;
        var started = DateTimeOffset.UtcNow;
        var accountId = trigger.Probe?.AccountId ?? "";

        if (TriggerAccounts.BaseUrlOf(registry, profiles, task.ProfileId, accountId) is { } baseUrl && !bucket.TryTake(baseUrl))
        {
            triggers.Defer(task.Id, nowMs + (long)TaskRunScheduler.Tick.TotalMilliseconds);
            var inARow = _deferrals.AddOrUpdate(task.Id, 1, (_, n) => n + 1);
            logger.LogInformation("Task {Task} probe deferred ({Count} in a row): mail server probe budget spent.", task.Id, inARow);
            return;
        }
        _deferrals.TryRemove(task.Id, out _);

        var runtime = registry.AcquireTask(task.ProfileId);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stopping);
            timeout.CancelAfter(options.ProbeTimeout);

            var (account, accountFailure) = await TriggerAccounts.ResolveAsync(runtime, restorer, accountId, timeout.Token);
            if (accountFailure is not null)
            {
                Fail(task, definition, accountFailure, nowMs);
                return;
            }

            using var result = await probes.RunAsync(account!, runtime, trigger.Probe!.Tool!, trigger.Probe.Arguments, timeout.Token);
            if (result.Code is not null)
            {
                Fail(task, definition, result.Code, nowMs);
                return;
            }

            if (Predicate.Parse(trigger.When, out _) is not { } predicate)
            {
                Fail(task, definition, TriggerCodes.PredicateUnreadable, nowMs);
                return;
            }

            var state = TriggerState.Open(sealer, task.ProfileId, task.Id, row.State, out var unreadable) ?? TriggerState.Initial;
            if (unreadable)
                logger.LogWarning("Task {Task}: {Code}; taking a new baseline.", task.Id, TriggerCodes.StateUnreadable);

            var evaluation = predicate.Evaluate(result.Json!.RootElement, state.Seen.ToHashSet(StringComparer.Ordinal), now);
            if (evaluation.Failed)
            {
                Fail(task, definition, TriggerCodes.PredicateLimit, nowMs);
                return;
            }
            if (evaluation.Errors.Count > 0)
                logger.LogInformation("Task {Task} probe: {Count} evaluation warning(s) (e.g. a regex timeout).", task.Id, evaluation.Errors.Count);

            var step = TriggerLogic.Decide(trigger, predicate.UsesNew, state, evaluation, now);
            var next = step.State;
            var outcome = step.Reason ?? "fired";
            if (step.WantsFire)
            {
                if (triggers.FiresSince(task.Id, nowMs - (long)TriggerStore.Day.TotalMilliseconds) >= options.MaxRunsPerDay)
                {
                    next = step.IfSkipped;
                    outcome = "skipped: daily cap";
                }
                else
                {
                    var fired = await FireAsync(task, definition, trigger, predicate, evaluation, account!, runtime,
                        manual: false, dryRun: false, state.SkippedFires, _stopping);
                    next = fired.RunId is not null ? step.IfFired : step.IfDeferred;
                    outcome = fired.RunId is not null ? "fired" : "skipped: task running";
                }
            }

            triggers.RecordProbe(task.Id, evaluation.Value, next.Seal(sealer, task.ProfileId, task.Id), nowMs);
            if (trigger.EveryMinutes < options.LeaseBelowMinutes)
                Hold(task.Id, task.ProfileId);   // before this probe's own lease goes, so the accounts stay live

            logger.LogInformation("Task {Task} probe: {Value}, {Outcome}, {Ms}ms.",
                task.Id, evaluation.Value ? "true" : "false", outcome, (int)(DateTimeOffset.UtcNow - started).TotalMilliseconds);
        }
        catch (OperationCanceledException) when (!_stopping.IsCancellationRequested)
        {
            Fail(task, definition, TriggerCodes.ProbeFailed, nowMs);   // the probe hit its time limit
        }
        finally
        {
            await runtime.ReleaseTaskAsync();
        }
    }

    /// <summary>A failed probe: counted; a hard code or five in a row pause the task with one failed run that says why.</summary>
    private void Fail(TaskRow task, TaskDefinition? definition, string code, long nowMs)
    {
        var hard = TriggerCodes.IsHard(code);
        var paused = triggers.RecordFailure(task.Id, code, hard, options.PauseAfterProbeFailures, nowMs);
        logger.LogWarning("Task {Task} probe failed: {Code}{Paused}.", task.Id, code, paused ? "; task paused" : "");
        if (!paused)
            return;

        var runId = ProfileCrypto.NewId();
        tasks.StartRun(new TaskRunRow(runId, task.Id, task.ProfileId, nowMs, null, "running", false, "condition", null, 0, 0, null, null, null, false));
        string? transcript = null;
        if (definition is not null && profiles.GetProfile(task.ProfileId) is { } profile)
        {
            transcript = SealTranscript(profile, runId, definition, false, DateTimeOffset.FromUnixTimeMilliseconds(nowMs), "error", null,
                [new AgentLoop.Step("notice", (TriggerCodes.Explain(code) ?? TaskRunner.Explain(code)) + " The task was paused.")],
                TaskRunner.Explain(code), false);
        }
        tasks.FinishRun(runId, "failed", code, 0, 0, null, null, transcript);
        tasks.Prune(task.Id, server.TaskRunRetention);
    }

    // ------------------------------------------------------------------ firing

    /// <summary>Starts the LLM run with the evidence as its seed, or sends the alert. RunId null: not accepted (the task is running).</summary>
    private async Task<RunNowResult> FireAsync(
        TaskRow task, TaskDefinition definition, TaskTrigger trigger, Predicate predicate, Predicate.Evaluation evaluation,
        Account probeAccount, ProfileRuntime runtime, bool manual, bool dryRun, int skippedFires, CancellationToken ct)
    {
        if (trigger.IsAlert)
        {
            var runId = await AlertAsync(task, definition, trigger, predicate, evaluation, probeAccount, runtime, manual, dryRun, ct);
            return new RunNowResult(runId, null, null);
        }

        var seed = new TriggerSeed(trigger.Probe!.Tool!, SeedArguments(definition, trigger, probeAccount),
            TriggerSeed.EvidenceJson(evaluation, manual), probeAccount.Handle, predicate.Describe(), manual, evaluation.Value, skippedFires);
        return scheduler.StartTriggered(task, manual ? "manual" : "condition", dryRun, seed, out var started)
            ? new RunNowResult(started, null, null)
            : new RunNowResult(null, "TASK_RUNNING", "That task is running right now.");
    }

    /// <summary>The probe's arguments as the model would have passed them: with <c>account</c> when the run has several accounts.</summary>
    private static string SeedArguments(TaskDefinition definition, TaskTrigger trigger, Account account)
    {
        var args = JsonNode.Parse(trigger.Probe!.Arguments.ValueKind == JsonValueKind.Object ? trigger.Probe.Arguments.GetRawText() : "{}")!.AsObject();
        if (definition.AccountIds.Count > 1)
            args[ToolPolicy.AccountProperty] = account.Handle;
        return args.ToJsonString();
    }

    /// <summary>
    /// An alert: no model. Mails the evidence from the delivery account to its own address (unless a
    /// test run), and records a run whose transcript (sealed to the profile's public key) holds the full
    /// evidence. Email failure is <c>EMAIL_FAILED</c>; it counts toward pausing.
    /// </summary>
    private async Task<string> AlertAsync(
        TaskRow task, TaskDefinition definition, TaskTrigger trigger, Predicate predicate, Predicate.Evaluation evaluation,
        Account probeAccount, ProfileRuntime runtime, bool manual, bool dryRun, CancellationToken ct)
    {
        var runId = ProfileCrypto.NewId();
        var started = DateTimeOffset.UtcNow;
        var triggerName = manual ? "manual" : "condition";
        tasks.StartRun(new TaskRunRow(runId, task.Id, task.ProfileId, started.ToUnixTimeMilliseconds(), null, "running", dryRun, triggerName,
            null, 0, 0, null, null, null, false));

        var code = "ERROR";
        var emailed = false;
        var calls = 1;
        var zone = TriggerSchedule.ZoneOf(definition.TimeZone);
        var subject = TriggerAlert.Subject(definition.Name, evaluation.Value);
        var body = TriggerAlert.Body(definition.Name, predicate.Describe(), trigger.Probe!.Tool!, started, zone, evaluation, manual);
        try
        {
            if (dryRun)
            {
                code = "ok";
            }
            else if (definition.EmailAccountId is not { } emailId)
            {
                code = TriggerCodes.EmailFailed;
            }
            else
            {
                var (mailbox, accountFailure) = await TriggerAccounts.ResolveAsync(runtime, restorer, emailId, ct);
                if (accountFailure is not null)
                {
                    code = accountFailure;
                }
                else
                {
                    calls++;
                    emailed = await SendAsync(mailbox!, runtime, subject, body, ct);
                    code = emailed ? "ok" : TriggerCodes.EmailFailed;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !_stopping.IsCancellationRequested)
        {
            logger.LogError(ex, "Task {Task} alert failed unexpectedly.", task.Id);
            code = TriggerCodes.EmailFailed;
        }
        finally
        {
            var ok = code == "ok";
            string? transcript = null;
            if (profiles.GetProfile(task.ProfileId) is { } profile)
            {
                var steps = new List<AgentLoop.Step>
                {
                    new("tool", TriggerSeed.EvidenceJson(evaluation, manual), trigger.Probe.Tool, SeedArguments(definition, trigger, probeAccount),
                        probeAccount.Handle) { Seed = true },
                };
                if (!ok)
                    steps.Add(new AgentLoop.Step("notice", TriggerCodes.Explain(code) ?? TaskRunner.Explain(code)));
                var final = dryRun ? $"**Test run: nothing was emailed.** This is the alert it would send:\n\n{subject}\n\n{body}" : $"{subject}\n\n{body}";
                transcript = SealTranscript(profile, runId, definition, dryRun, started, ok ? "alert" : "error", final, steps,
                    ok ? null : TriggerCodes.Explain(code) ?? TaskRunner.Explain(code), emailed);
            }
            tasks.FinishRun(runId, ok ? "ok" : "failed", ok ? null : code, calls, emailed ? 1 : 0, null, null, transcript);
            if (!manual)
                tasks.RecordOutcome(task.Id, ok, ok ? "ok" : code, TaskRunner.PauseAfterFailures);
            tasks.Prune(task.Id, server.TaskRunRetention);
            logger.LogInformation("Task {Task} alert{Dry}: {Outcome}.", task.Id, dryRun ? " (test)" : "", ok ? (emailed ? "emailed" : "recorded") : code);
        }
        return runId;
    }

    /// <summary>The alert, sent by the server (not a model) from the delivery account to its own address, through the dispatcher.</summary>
    private async Task<bool> SendAsync(Account mailbox, ProfileRuntime runtime, string subject, string body, CancellationToken ct)
    {
        var arguments = new Dictionary<string, JsonElement>
        {
            ["to"] = JsonSerializer.SerializeToElement(mailbox.EmailAddress),
            ["subject"] = JsonSerializer.SerializeToElement(subject),
            ["body"] = JsonSerializer.SerializeToElement(body),
            ["cc"] = JsonSerializer.SerializeToElement(""),
            ["bcc"] = JsonSerializer.SerializeToElement(""),
        };
        var outcome = await dispatcher.DispatchAsync(new TaskToolContext([mailbox], runtime, null), "send_email", arguments, services, ct);
        return outcome.Status == ToolDispatcher.Status.Ok && !(outcome.Result.IsError ?? false) &&
               !ToolInvoker.PayloadIndicatesFailure(ToolInvoker.Flatten(outcome.Result));
    }

    private static string SealTranscript(
        ProfileRow profile, string runId, TaskDefinition definition, bool dryRun, DateTimeOffset started, string stop, string? final,
        IReadOnlyList<AgentLoop.Step> steps, string? error, bool emailed)
    {
        var transcript = new TaskRunner.Transcript(1, definition.Name, definition.Prompt, definition.Model, dryRun, started, stop, final,
            steps, error, emailed);
        return ProfileCrypto.SealToPublicKey(JsonSerializer.SerializeToUtf8Bytes(transcript, Json), profile.PublicKey,
            TaskRunner.RunContext(profile.Id, runId));
    }

    // ------------------------------------------------------------------ run now

    /// <summary>
    /// "Run now" / "Test run" on a condition task: probe once, then run the prompt with that result as
    /// the seed (or send / record the alert), whether the condition holds or not. The trigger state is
    /// read for "new" but never advanced.
    /// </summary>
    public async Task<RunNowResult> RunNowAsync(TaskRow task, TaskDefinition definition, bool dryRun, CancellationToken ct)
    {
        if (!options.Enabled || registry.ServerSealer is not { } sealer || definition.Trigger is not { } trigger)
            return new RunNowResult(null, "TRIGGERS_DISABLED", "Condition-triggered tasks are switched off on this server.");
        if (scheduler.IsRunning(task.Id))
            return new RunNowResult(null, "TASK_RUNNING", "That task is running right now.");

        var done = new TaskCompletionSource();
        if (!_inFlight.TryAdd(task.Id, done.Task))
            return new RunNowResult(null, "TASK_RUNNING", "That task is being checked right now.");
        try
        {
            var accountId = trigger.Probe?.AccountId ?? "";
            // A person's check: the interactive buckets, never the scheduled probes' one.
            if (interactive.TryTake(task.ProfileId, TriggerAccounts.BaseUrlOf(registry, profiles, task.ProfileId, accountId)) is { } throttled)
                return new RunNowResult(null, "PROBE_THROTTLED", throttled);

            var runtime = registry.AcquireTask(task.ProfileId);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _stopping);
                timeout.CancelAfter(options.ProbeTimeout);
                var (account, accountFailure) = await TriggerAccounts.ResolveAsync(runtime, restorer, accountId, timeout.Token);
                if (accountFailure is not null)
                    return new RunNowResult(null, accountFailure, TaskRunner.Explain(accountFailure));

                using var result = await probes.RunAsync(account!, runtime, trigger.Probe!.Tool!, trigger.Probe.Arguments, timeout.Token);
                if (result.Code is not null)
                    return new RunNowResult(null, result.Code, TriggerCodes.Explain(result.Code));
                if (Predicate.Parse(trigger.When, out _) is not { } predicate)
                    return new RunNowResult(null, TriggerCodes.PredicateUnreadable, TriggerCodes.Explain(TriggerCodes.PredicateUnreadable));

                var stored = triggers.Get(task.ProfileId, task.Id);
                var state = TriggerState.Open(sealer, task.ProfileId, task.Id, stored?.State, out _);
                var evaluation = predicate.Evaluate(result.Json!.RootElement, state?.Seen.ToHashSet(StringComparer.Ordinal), DateTimeOffset.UtcNow);
                if (evaluation.Failed)
                    return new RunNowResult(null, TriggerCodes.PredicateLimit, TriggerCodes.Explain(TriggerCodes.PredicateLimit));

                logger.LogInformation("Task {Task} checked by hand: {Value}.", task.Id, evaluation.Value ? "true" : "false");
                return await FireAsync(task, definition, trigger, predicate, evaluation, account!, runtime, manual: true, dryRun,
                    state?.SkippedFires ?? 0, ct);
            }
            finally
            {
                await runtime.ReleaseTaskAsync();
            }
        }
        finally
        {
            _inFlight.TryRemove(task.Id, out _);
            done.TrySetResult();
        }
    }
}

public static class TriggerProberExtensions
{
    /// <summary>The prober is a singleton ("Run now" and the probe endpoint need it) and a hosted service. Needs <see cref="TriggerStore"/>.</summary>
    public static IServiceCollection AddTriggerProber(this IServiceCollection services)
    {
        services.AddSingleton<ProbeRunner>();
        services.AddSingleton(sp => new ProbeHostBucket(sp.GetRequiredService<TriggerOptions>()));
        services.AddSingleton(sp => new InteractiveProbeLimiter(sp.GetRequiredService<TriggerOptions>()));
        services.AddSingleton<TriggerProber>();
        services.AddHostedService(sp => sp.GetRequiredService<TriggerProber>());
        return services;
    }
}
