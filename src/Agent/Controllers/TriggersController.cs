using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SmarterMailAgent.Auth;
using SmarterMailAgent.Llm;
using SmarterMailAgent.Mcp;
using SmarterMailAgent.Profiles;
using SmarterMailAgent.Server;
using SmarterMailAgent.Storage;
using SmarterMailAgent.Tasks;
using SmarterMailAgent.Tasks.Triggers;

namespace SmarterMailAgent.Controllers;

/// <summary>
/// <c>POST /api/tasks/probe</c>: the editor's "Test probe". Calls a read tool now, as one of the
/// session's own live accounts (delegated or not), and evaluates an optional condition against the
/// result. Reading is what this cookie can already do through <c>/api/tools/call</c>; nothing is stored.
/// Cookie only, unlocked profile session, <c>api</c> limiter, and the interactive probe buckets
/// (<see cref="InteractiveProbeLimiter"/>: per profile and per mail server, apart from scheduled probes).
/// </summary>
[ApiController]
[Route("api/tasks/probe")]
[ServerModeOnly]
[Authorize(Policy = "SessionAccess")]
public sealed class TriggersController(
    ProfileStore profiles,
    ProfileRegistry registry,
    ToolCatalog catalog,
    ServerOptions options,
    TriggerOptions triggerOptions,
    IServiceProvider services,
    ILogger<TriggersController> logger) : ControllerBase
{
    public const int MaxResultChars = AgentLoop.MaxToolResultChars;

    public sealed record ProbeRequest(string? AccountId, string? Tool, JsonElement Arguments, JsonElement When);

    [HttpPost]
    [EnableRateLimiting("api")]
    public async Task<IActionResult> Probe([FromBody] ProbeRequest request)
    {
        if (!options.TasksEnabled || !triggerOptions.Enabled || registry.ServerSealer is null)
            return NotFound(new { error = "Condition-triggered tasks are not enabled on this server.", code = "TRIGGERS_DISABLED" });
        if (!HttpContext.IsCookieAuthenticated())
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "Use the browser for this.", code = "COOKIE_REQUIRED" });
        var session = HttpContext.RequireSession();
        if (session.Profile is not { } runtime)
            return NotFound(new { error = "Save this chat to a profile first.", code = "NO_PROFILE" });
        if (profiles.GetProfile(runtime.ProfileId) is not { } profileRow)
            return NotFound(new { error = "That profile no longer exists.", code = "PROFILE_GONE" });
        if (!options.AllowsTasks(profileRow))
            return TaskAccess.NotInvited(this);
        if (!runtime.IsUnlocked)
            return Conflict(new { error = "Unlock your profile with your passkey first.", code = "PROFILE_LOCKED" });

        if (session.Accounts.FirstOrDefault(a => a.Id == request.AccountId) is not { } account)
            return NotFound(new { error = "That account is not signed in in this chat.", code = "ACCOUNT_NOT_LIVE" });

        var tool = request.Tool?.Trim() ?? "";
        if (!catalog.TryGet(tool, out var entry) || entry.Write)
            return Invalid([$"'{tool}' is not a tool that only reads."]);
        if (!ToolPolicy.RoleAllows(account.Role, entry.Scope))
            return Invalid([$"That account cannot use '{tool}'."]);
        var argumentErrors = TaskTrigger.ArgumentErrors(entry, request.Arguments);
        if (argumentErrors.Count > 0)
            return Invalid(argumentErrors);

        // Interactive probes have their own buckets (per profile, and a per-server share), so they never
        // spend what the scheduled probes need.
        if (services.GetService<InteractiveProbeLimiter>()?.TryTake(runtime.ProfileId, account.BaseUrl) is { } throttled)
            return StatusCode(StatusCodes.Status429TooManyRequests, new { error = throttled, code = "PROBE_THROTTLED" });

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
        timeout.CancelAfter(triggerOptions.ProbeTimeout);
        var probes = services.GetRequiredService<ProbeRunner>();
        using var result = await probes.RunAsync(account, runtime, tool, request.Arguments, timeout.Token);

        object? evaluation = null;
        if (result.Json is not null && request.When.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
        {
            var predicate = Predicate.Parse(request.When, out var errors);
            if (predicate is null)
            {
                evaluation = new { value = false, matched = Array.Empty<object>(), truncated = false, errors, description = (string?)null };
            }
            else
            {
                // No trigger state here: every item counts as new.
                var e = predicate.Evaluate(result.Json.RootElement, null, DateTimeOffset.UtcNow);
                evaluation = new { value = e.Value, matched = e.Matched, truncated = e.Truncated, errors = e.Errors, description = predicate.Describe() };
            }
        }

        logger.LogInformation("Test probe {Tool} ({Role}): {Outcome}.", tool, account.Role, result.Code ?? "ok");
        return Ok(new
        {
            result = result.Content.Length > MaxResultChars ? result.Content[..MaxResultChars] : result.Content,
            json = result.Json is not null,
            truncated = result.Content.Length > MaxResultChars,
            isError = result.Code == TriggerCodes.ProbeFailed,
            code = result.Code,
            evaluation,
        });
    }

    private BadRequestObjectResult Invalid(IReadOnlyList<string> errors) =>
        BadRequest(new { error = string.Join(" ", errors), errors, code = "PROBE_INVALID" });
}

/// <summary>The condition-task parts of <see cref="TasksController"/>, kept here so that controller only gains a few calls.</summary>
public static class TriggerEndpoints
{
    public static IActionResult? Limit(
        ControllerBase controller, IServiceProvider services, TriggerStore? store, string profileId, string? id, TaskDefinition definition)
    {
        if (definition.Trigger is null || store is null)
            return null;
        var max = services.GetRequiredService<TriggerOptions>().PerProfile;
        return store.CountForProfile(profileId, id) >= max
            ? controller.Conflict(new { error = $"A profile can have at most {max} condition-triggered tasks.", code = "TRIGGER_LIMIT" })
            : null;
    }

    /// <summary>A saved condition task is probed at the next tick, and that first probe only takes a baseline.</summary>
    public static void AfterSave(IServiceProvider services, TriggerStore? store, string profileId, string id, TaskDefinition definition)
    {
        store?.Reset(profileId, id, definition.Trigger is null ? null : DataStore.Now());
        services.GetService<TriggerProber>()?.Forget(id);
    }

    public static async Task<IActionResult> RunNowAsync(
        ControllerBase controller, IServiceProvider services, TaskRow task, TaskDefinition definition, bool dryRun, CancellationToken ct)
    {
        if (services.GetService<TriggerProber>() is not { } prober)
            return controller.NotFound(new { error = "Condition-triggered tasks are not enabled on this server.", code = "TRIGGERS_DISABLED" });

        var result = await prober.RunNowAsync(task, definition, dryRun, ct);
        if (result.Code is null)
            return controller.Accepted(new { runId = result.RunId });

        var body = new { error = result.Message ?? TaskRunner.Explain(result.Code), code = result.Code };
        return result.Code switch
        {
            "TASK_RUNNING" => controller.Conflict(body),
            "PROBE_THROTTLED" => controller.StatusCode(StatusCodes.Status429TooManyRequests, body),
            "TRIGGERS_DISABLED" => controller.NotFound(body),
            TriggerCodes.ProbeFailed or TriggerCodes.ProbeNotJson or "ACCOUNT_UNAVAILABLE" => controller.StatusCode(StatusCodes.Status502BadGateway, body),
            _ => controller.Conflict(body),
        };
    }
}
