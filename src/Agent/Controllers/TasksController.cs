using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmarterMailAgent.Auth;
using SmarterMailAgent.Mcp;
using SmarterMailAgent.Profiles;
using SmarterMailAgent.Server;
using SmarterMailAgent.Storage;
using SmarterMailAgent.Tasks;
using SmarterMailAgent.Tasks.Approvals;

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
        TaskApprovals? Approvals = null);

    public sealed record RunRequest(bool DryRun);

    public sealed record TaskView(
        string Id, bool Enabled, string Status, int ConsecutiveFailures, DateTimeOffset? NextRunAt, DateTimeOffset? LastRunAt,
        TaskDefinition? Definition);

    public sealed record RunView(
        string Id, string TaskId, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt, string Status, bool DryRun, string Trigger,
        string? ErrorCode, string? ErrorMessage, int ToolCalls, int Writes, long? PromptTokens, long? CompletionTokens, bool Read,
        string? Transcript);

    [HttpGet]
    public IActionResult List()
    {
        if (Gate(out var profileId, out var sealer) is { } refusal)
            return refusal;

        var list = tasks.ForProfile(profileId).Select(t => View(t, sealer)).ToList();
        return Ok(new { tasks = list, unread = tasks.UnreadCount(profileId), pending = services.GetRequiredService<ProposalStore>().PendingCount(profileId) });
    }

    [HttpPost]
    public IActionResult Create([FromBody] TaskRequest request)
    {
        if (Gate(out var profileId, out var sealer) is { } refusal)
            return refusal;
        if (tasks.CountForProfile(profileId) >= options.TasksPerProfile)
            return Conflict(new { error = $"A profile can have at most {options.TasksPerProfile} tasks.", code = "TASK_LIMIT" });

        var definition = Definition(request);
        if (Invalid(definition) is { } invalid)
            return invalid;

        var id = ProfileCrypto.NewId(12);
        var now = DataStore.Now();
        tasks.Insert(new TaskRow(id, profileId, request.Enabled, definition.Seal(sealer, profileId, id),
            NextRun(definition, request.Enabled), "ok", 0, null, now, now));
        logger.LogInformation("Task {Task} created.", id);
        return Ok(View(tasks.Get(profileId, id)!, sealer));
    }

    [HttpPut("{id}")]
    public IActionResult Update(string id, [FromBody] TaskRequest request)
    {
        if (Gate(out var profileId, out var sealer) is { } refusal)
            return refusal;
        if (tasks.Get(profileId, id) is null)
            return NotFound(new { error = "No such task.", code = "TASK_NOT_FOUND" });

        var definition = Definition(request);
        if (Invalid(definition) is { } invalid)
            return invalid;

        tasks.Update(profileId, id, request.Enabled, definition.Seal(sealer, profileId, id), NextRun(definition, request.Enabled), "ok");
        return Ok(View(tasks.Get(profileId, id)!, sealer));
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
    public IActionResult Run(string id, [FromBody] RunRequest? request)
    {
        if (Gate(out var profileId, out _) is { } refusal)
            return refusal;
        if (tasks.Get(profileId, id) is not { } task)
            return NotFound(new { error = "No such task.", code = "TASK_NOT_FOUND" });

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

    private static TaskDefinition Definition(TaskRequest r) => new(
        1,
        r.Name?.Trim() ?? "",
        r.Prompt?.Trim() ?? "",
        r.Cron?.Trim() ?? "",
        string.IsNullOrWhiteSpace(r.TimeZone) ? "UTC" : r.TimeZone.Trim(),
        (r.AccountIds ?? []).Distinct(StringComparer.Ordinal).ToList(),
        (r.AllowedWrites ?? []).Distinct(StringComparer.Ordinal).ToList(),
        r.MaxWrites ?? 5,
        r.Model?.Trim() ?? "",
        string.IsNullOrWhiteSpace(r.EmailAccountId) ? null : r.EmailAccountId,
        TaskApprovals.Normalize(r.Approvals));

    /// <summary>Validated against the profile's delegated accounts, as this session's profile knows them.</summary>
    private IActionResult? Invalid(TaskDefinition definition)
    {
        var runtime = HttpContext.RequireSession().Profile!;
        if (!runtime.IsUnlocked)
            return Conflict(new { error = "Unlock your profile with your passkey first.", code = "PROFILE_LOCKED" });

        var delegated = runtime.Rows
            .Where(r => r.Seal == ProfileStore.SealServer)
            .ToDictionary(r => r.Id, StringComparer.Ordinal);
        var errors = definition.Validate(delegated, catalog, options.TaskMinInterval)
            .Concat(definition.Approvals?.Validate(definition.AllowedWrites, options.TaskMaxProposals) ?? []).ToList();
        return errors.Count == 0
            ? null
            : BadRequest(new { error = string.Join(" ", errors), errors, code = "TASK_INVALID" });
    }

    private static long? NextRun(TaskDefinition definition, bool enabled) =>
        enabled ? TaskDefinition.NextRun(definition.Cron, definition.TimeZone, DateTimeOffset.UtcNow)?.ToUnixTimeMilliseconds() : null;

    private static TaskView View(TaskRow t, Sealer sealer) => new(
        t.Id, t.Enabled, t.Status, t.ConsecutiveFailures, Time(t.NextRunAt), Time(t.LastRunAt),
        TaskDefinition.Open(sealer, t.ProfileId, t.Id, t.Definition));

    private static RunView RunOf(TaskRunRow r, bool withTranscript) => new(
        r.Id, r.TaskId, DateTimeOffset.FromUnixTimeMilliseconds(r.StartedAt), Time(r.FinishedAt), r.Status, r.DryRun, r.Trigger,
        r.ErrorCode, r.ErrorCode is { } code && code != "MAX_ROUNDS" && code != "LENGTH" && code != "CONTENT_FILTER" ? TaskRunner.Explain(code) : null,
        r.ToolCalls, r.Writes, r.PromptTokens, r.CompletionTokens, r.Read, withTranscript ? r.Transcript : null);

    private static DateTimeOffset? Time(long? ms) => ms is { } v ? DateTimeOffset.FromUnixTimeMilliseconds(v) : null;
}
