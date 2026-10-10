using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SmarterMailAgent.Auth;
using SmarterMailAgent.Mcp;
using SmarterMailAgent.Profiles;
using SmarterMailAgent.Server;
using SmarterMailAgent.Storage;
using SmarterMailAgent.Tasks;
using SmarterMailAgent.Tasks.Approvals;
using SmarterMailAgent.Tasks.Triggers;

namespace SmarterMailAgent.Controllers;

/// <summary>
/// Scheduled tasks of the current profile session. Needs server mode and <c>DATA_KEY</c>; every
/// endpoint is cookie-only and acts on the session's own profile.
/// </summary>
[ApiController]
[Route("api/tasks")]
[ServerModeOnly]
[Authorize(Policy = "SessionAccess")]
public sealed class TasksController(
    TaskStore tasks,
    ProfileStore profiles,
    ProfileRegistry registry,
    ToolCatalog catalog,
    ServerOptions options,
    IServiceProvider services,
    ILogger<TasksController> logger) : ControllerBase
{
    public sealed record TaskRequest(
        string? Name, string? Prompt, string? Cron, string? TimeZone, IReadOnlyList<string>? AccountIds,
        IReadOnlyList<string>? AllowedWrites, int? MaxWrites, string? Model, string? EmailAccountId, bool Enabled = true,
        TaskApprovals? Approvals = null, TaskTrigger? Trigger = null);

    public sealed record RunRequest(bool DryRun);

    public sealed record TaskView(
        string Id, bool Enabled, string Status, int ConsecutiveFailures, DateTimeOffset? NextRunAt, DateTimeOffset? LastRunAt,
        TaskDefinition? Definition, TriggerStatus? Trigger = null);

    public sealed record RunView(
        string Id, string TaskId, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt, string Status, bool DryRun, string Trigger,
        string? ErrorCode, string? ErrorMessage, int ToolCalls, int Writes, long? PromptTokens, long? CompletionTokens, bool Read,
        string? Transcript);

    [HttpGet]
    public IActionResult List()
    {
        if (Gate(out var profileId, out var sealer) is { } refusal)
            return refusal;

        var status = Triggers?.Status(profileId, DataStore.Now());
        var prober = services.GetService<TriggerProber>();
        var list = tasks.ForProfile(profileId)
            .Select(t => View(t, sealer, status?.GetValueOrDefault(t.Id) is { } s
                ? s with { Deferrals = prober?.Deferrals(t.Id) ?? 0 }
                : null))
            .ToList();
        return Ok(new { tasks = list, unread = tasks.UnreadCount(profileId), pending = services.GetRequiredService<ProposalStore>().PendingCount(profileId) });
    }

    [HttpPost]
    public IActionResult Create([FromBody] TaskRequest request)
    {
        if (Gate(out var profileId, out var sealer) is { } refusal)
            return refusal;
        if (TaskAccess.Refusal(this, options, profiles, profileId) is { } notInvited)
            return notInvited;
        if (tasks.CountForProfile(profileId) >= options.TasksPerProfile)
            return Conflict(new { error = $"A profile can have at most {options.TasksPerProfile} tasks.", code = "TASK_LIMIT" });

        var definition = Definition(request);
        if (Invalid(definition) is { } invalid)
            return invalid;
        if (TriggerLimit(profileId, null, definition) is { } limited)
            return limited;

        var id = ProfileCrypto.NewId(12);
        var now = DataStore.Now();
        tasks.Insert(new TaskRow(id, profileId, request.Enabled, definition.Seal(sealer, profileId, id),
            NextRun(definition, request.Enabled), "ok", 0, null, now, now));
        AfterSave(profileId, id, definition);
        logger.LogInformation("Task {Task} created.", id);
        return Ok(View(tasks.Get(profileId, id)!, sealer, Triggers?.Status(profileId, now).GetValueOrDefault(id)));
    }

    [HttpPut("{id}")]
    public IActionResult Update(string id, [FromBody] TaskRequest request)
    {
        if (Gate(out var profileId, out var sealer) is { } refusal)
            return refusal;
        if (TaskAccess.Refusal(this, options, profiles, profileId) is { } notInvited)
            return notInvited;
        if (tasks.Get(profileId, id) is null)
            return NotFound(new { error = "No such task.", code = "TASK_NOT_FOUND" });

        var definition = Definition(request);
        if (Invalid(definition) is { } invalid)
            return invalid;
        if (TriggerLimit(profileId, id, definition) is { } limited)
            return limited;

        tasks.Update(profileId, id, request.Enabled, definition.Seal(sealer, profileId, id), NextRun(definition, request.Enabled), "ok");
        AfterSave(profileId, id, definition);
        return Ok(View(tasks.Get(profileId, id)!, sealer, Triggers?.Status(profileId, DataStore.Now()).GetValueOrDefault(id)));
    }

    [HttpDelete("{id}")]
    public IActionResult Delete(string id)
    {
        if (Gate(out var profileId, out _) is { } refusal)
            return refusal;
        return tasks.Delete(profileId, id) ? NoContent() : NotFound(new { error = "No such task.", code = "TASK_NOT_FOUND" });
    }

    /// <summary>Starts a run now (in the background). <c>dryRun</c>: writes are simulated, nothing is mailed.</summary>
    [HttpPost("{id}/run")]
    [EnableRateLimiting("api")]
    public async Task<IActionResult> Run(string id, [FromBody] RunRequest? request)
    {
        if (Gate(out var profileId, out var sealer) is { } refusal)
            return refusal;
        if (TaskAccess.Refusal(this, options, profiles, profileId) is { } notInvited)
            return notInvited;
        if (tasks.Get(profileId, id) is not { } task)
            return NotFound(new { error = "No such task.", code = "TASK_NOT_FOUND" });

        // A condition task: probe once, then run (or alert) with that result, whether it holds or not.
        if (TaskDefinition.Open(sealer, profileId, id, task.Definition) is { Trigger: not null } triggered)
            return await TriggerEndpoints.RunNowAsync(this, services, task, triggered, request?.DryRun ?? false, HttpContext.RequestAborted);

        var scheduler = services.GetRequiredService<TaskRunScheduler>();
        if (!scheduler.RunNow(task, request?.DryRun ?? false, out var runId))
            return Conflict(new { error = "That task is running right now.", code = "TASK_RUNNING" });
        return Accepted(new { runId });
    }

    [HttpGet("runs")]
    public IActionResult Runs([FromQuery] string? taskId, [FromQuery] int limit = 50)
    {
        if (Gate(out var profileId, out _) is { } refusal)
            return refusal;
        return Ok(tasks.Runs(profileId, taskId, Math.Clamp(limit, 1, 200)).Select(r => RunOf(r, false)).ToList());
    }

    /// <summary>One run with its transcript, still sealed to the profile's public key: the browser opens it.</summary>
    [HttpGet("runs/{runId}")]
    public IActionResult GetRun(string runId)
    {
        if (Gate(out var profileId, out _) is { } refusal)
            return refusal;
        return tasks.Run(profileId, runId) is { } run
            ? Ok(RunOf(run, true))
            : NotFound(new { error = "No such run.", code = "RUN_NOT_FOUND" });
    }

    [HttpPost("runs/{runId}/read")]
    public IActionResult MarkRead(string runId)
    {
        if (Gate(out var profileId, out _) is { } refusal)
            return refusal;
        tasks.MarkRead(profileId, runId);
        return NoContent();
    }

    // ------------------------------------------------------------------ helpers

    private IActionResult? Gate(out string profileId, out Sealer sealer)
    {
        profileId = "";
        sealer = null!;
        if (!options.TasksEnabled || registry.ServerSealer is null)
            return NotFound(new { error = "Scheduled tasks are not enabled on this server.", code = "TASKS_DISABLED" });
        if (!HttpContext.IsCookieAuthenticated())
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "Use the browser for this.", code = "COOKIE_REQUIRED" });
        if (HttpContext.RequireSession().Profile is not { } profile)
            return NotFound(new { error = "Save this chat to a profile first.", code = "NO_PROFILE" });
        if (profiles.GetProfile(profile.ProfileId) is null)
            return NotFound(new { error = "That profile no longer exists.", code = "PROFILE_GONE" });

        profileId = profile.ProfileId;
        sealer = registry.ServerSealer;
        return null;
    }

    private TriggerStore? Triggers => services.GetService<TriggerStore>();

    /// <summary>409 TRIGGER_LIMIT when this would be one condition task too many for the profile.</summary>
    private IActionResult? TriggerLimit(string profileId, string? id, TaskDefinition definition) =>
        TriggerEndpoints.Limit(this, services, Triggers, profileId, id, definition);

    /// <summary>A condition task gets its first probe and a fresh state; a cron task none.</summary>
    private void AfterSave(string profileId, string id, TaskDefinition definition) =>
        TriggerEndpoints.AfterSave(services, Triggers, profileId, id, definition);

    private static TaskDefinition Definition(TaskRequest r) => new(
        1,
        r.Name?.Trim() ?? "",
        r.Prompt?.Trim() ?? "",
        r.Trigger is null ? r.Cron?.Trim() ?? "" : "",
        string.IsNullOrWhiteSpace(r.TimeZone) ? "UTC" : r.TimeZone.Trim(),
        (r.AccountIds ?? []).Distinct(StringComparer.Ordinal).ToList(),
        (r.AllowedWrites ?? []).Distinct(StringComparer.Ordinal).ToList(),
        r.MaxWrites ?? 5,
        r.Model?.Trim() ?? "",
        string.IsNullOrWhiteSpace(r.EmailAccountId) ? null : r.EmailAccountId,
        TaskApprovals.Normalize(r.Approvals),
        r.Trigger?.Normalized());

    /// <summary>Validated against the profile's delegated accounts, as this session's profile knows them.</summary>
    private IActionResult? Invalid(TaskDefinition definition)
    {
        var runtime = HttpContext.RequireSession().Profile!;
        if (!runtime.IsUnlocked)
            return Conflict(new { error = "Unlock your profile with your passkey first.", code = "PROFILE_LOCKED" });

        var delegated = runtime.Rows
            .Where(r => r.Seal == ProfileStore.SealServer)
            .ToDictionary(r => r.Id, StringComparer.Ordinal);
        var errors = definition.Validate(delegated, catalog, options.TaskMinInterval, services.GetService<TriggerOptions>())
            .Concat(definition.Approvals?.Validate(definition.AllowedWrites, options.TaskMaxProposals) ?? []).ToList();
        return errors.Count == 0
            ? null
            : BadRequest(new { error = string.Join(" ", errors), errors, code = "TASK_INVALID" });
    }

    private static long? NextRun(TaskDefinition definition, bool enabled) =>
        enabled ? TaskDefinition.NextRun(definition.Cron, definition.TimeZone, DateTimeOffset.UtcNow)?.ToUnixTimeMilliseconds() : null;

    private static TaskView View(TaskRow t, Sealer sealer, TriggerStatus? trigger = null) => new(
        t.Id, t.Enabled, t.Status, t.ConsecutiveFailures, Time(t.NextRunAt), Time(t.LastRunAt),
        TaskDefinition.Open(sealer, t.ProfileId, t.Id, t.Definition), trigger);

    private static RunView RunOf(TaskRunRow r, bool withTranscript) => new(
        r.Id, r.TaskId, DateTimeOffset.FromUnixTimeMilliseconds(r.StartedAt), Time(r.FinishedAt), r.Status, r.DryRun, r.Trigger,
        r.ErrorCode, r.ErrorCode is { } code && code != "MAX_ROUNDS" && code != "LENGTH" && code != "CONTENT_FILTER" ? TaskRunner.Explain(code) : null,
        r.ToolCalls, r.Writes, r.PromptTokens, r.CompletionTokens, r.Read, withTranscript ? r.Transcript : null);

    private static DateTimeOffset? Time(long? ms) => ms is { } v ? DateTimeOffset.FromUnixTimeMilliseconds(v) : null;
}
