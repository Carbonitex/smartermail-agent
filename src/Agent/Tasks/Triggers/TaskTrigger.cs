using System.Text.Json;
using Cronos;
using SmarterMailAgent.Auth;
using SmarterMailAgent.Mcp;
using SmarterMailAgent.Profiles;

namespace SmarterMailAgent.Tasks.Triggers;

/// <summary>What a trigger reads: one read tool, as one of the task's delegated accounts.</summary>
/// <param name="Arguments">The tool's own arguments (a JSON object, at most <see cref="TaskTrigger.MaxArgumentsChars"/>), never <c>account</c>.</param>
public sealed record TriggerProbe(string? AccountId, string? Tool, JsonElement Arguments);

/// <summary>
/// The condition part of a task (<see cref="TaskDefinition.Trigger"/>), sealed with <c>DATA_KEY</c>
/// inside the definition. A task is either scheduled (cron) or triggered, never both.
/// </summary>
/// <param name="EveryMinutes">Probe interval (± 10 % jitter).</param>
/// <param name="When">The predicate (<see cref="Predicate"/>), kept as JSON and parsed on use.</param>
/// <param name="Fire"><c>edge</c>: once each time it becomes true; <c>level</c>: on every true probe (cooldown applies). Ignored when the predicate has a <c>new</c> node.</param>
/// <param name="HoldFor">Consecutive true probes needed before firing (1–10).</param>
/// <param name="CooldownMinutes">Minimum gap between two fires; 0 = <paramref name="EveryMinutes"/>.</param>
/// <param name="Action"><c>run</c>: the task's prompt, with the evidence; <c>alert</c>: an email, no model.</param>
/// <param name="ActiveHours">Optional five-field cron (task time zone): probe only in minutes it matches.</param>
public sealed record TaskTrigger(
    TriggerProbe? Probe,
    int EveryMinutes,
    JsonElement When,
    string? Fire = "edge",
    int HoldFor = 1,
    int CooldownMinutes = 0,
    string? Action = "run",
    string? ActiveHours = null)
{
    public const int MaxArgumentsChars = 4096;
    public const int MaxHoldFor = 10;
    public const int MaxCooldownMinutes = 7 * 24 * 60;

    public bool IsAlert => Action == "alert";

    /// <summary>Defaults filled in, so what is sealed is what runs.</summary>
    public TaskTrigger Normalized() => this with
    {
        Probe = Probe is null ? null : Probe with
        {
            AccountId = Probe.AccountId?.Trim(),
            Tool = Probe.Tool?.Trim(),
            Arguments = Probe.Arguments.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
                ? JsonSerializer.SerializeToElement(new { })
                : Probe.Arguments.Clone(),
        },
        When = When.ValueKind == JsonValueKind.Undefined ? JsonSerializer.SerializeToElement<object?>(null) : When.Clone(),
        Fire = string.IsNullOrWhiteSpace(Fire) ? "edge" : Fire.Trim().ToLowerInvariant(),
        Action = string.IsNullOrWhiteSpace(Action) ? "run" : Action.Trim().ToLowerInvariant(),
        CooldownMinutes = CooldownMinutes <= 0 ? EveryMinutes : CooldownMinutes,
        HoldFor = HoldFor <= 0 ? 1 : HoldFor,
        ActiveHours = string.IsNullOrWhiteSpace(ActiveHours) ? null : ActiveHours.Trim(),
    };

    /// <summary>Every reason this trigger cannot be saved for <paramref name="task"/>, or empty.</summary>
    public IReadOnlyList<string> Validate(
        TaskDefinition task, IReadOnlyDictionary<string, ProfileRuntime.RowInfo> delegatedRows, ToolCatalog catalog, TriggerOptions options)
    {
        var errors = new List<string>();
        if (!options.Enabled)
        {
            errors.Add("Condition-triggered tasks are switched off on this server.");
            return errors;
        }

        if (Probe is null || string.IsNullOrWhiteSpace(Probe.AccountId) || string.IsNullOrWhiteSpace(Probe.Tool))
        {
            errors.Add("Choose the account and the tool the condition checks.");
        }
        else
        {
            AccountRole? role = null;
            if (!(task.AccountIds ?? []).Contains(Probe.AccountId) ||
                !delegatedRows.TryGetValue(Probe.AccountId, out var row) || row.Entry is null)
                errors.Add("The account the condition checks must be one of the task's accounts, and allow scheduled tasks.");
            else
                role = row.Entry.Role;

            if (!catalog.TryGet(Probe.Tool, out var entry))
                errors.Add($"'{Probe.Tool}' is not a known tool.");
            else if (entry.Write)
                errors.Add($"'{Probe.Tool}' makes changes; a condition can only use a tool that reads.");
            else if (role is { } r && !ToolPolicy.RoleAllows(r, entry.Scope))
                errors.Add($"That account cannot use '{Probe.Tool}'.");
            else
                errors.AddRange(ArgumentErrors(entry, Probe.Arguments));
        }

        if (EveryMinutes < options.MinIntervalMinutes || EveryMinutes > TriggerOptions.MaxIntervalMinutes)
            errors.Add($"Check every {options.MinIntervalMinutes} to {TriggerOptions.MaxIntervalMinutes} minutes.");
        if (CooldownMinutes < EveryMinutes || CooldownMinutes > MaxCooldownMinutes)
            errors.Add($"The pause between two firings must be at least the check interval and at most {MaxCooldownMinutes} minutes.");
        if (HoldFor is < 1 or > MaxHoldFor)
            errors.Add($"\"Hold for\" must be 1 to {MaxHoldFor} checks.");
        if (Fire is not ("edge" or "level"))
            errors.Add("Fire must be \"edge\" (once when it becomes true) or \"level\" (every time).");
        if (Action is not ("run" or "alert"))
            errors.Add("Then must be \"run\" (run the prompt) or \"alert\" (just email me).");
        if (!TriggerSchedule.IsKnownZone(task.TimeZone))
            errors.Add("The time zone is not one this server knows.");
        if (ActiveHours is not null && !TriggerSchedule.TryParseWindow(ActiveHours, task.TimeZone, out _, out _))
            errors.Add("\"Only between\" must be a five-field cron expression in the task's time zone.");

        if (Predicate.Parse(When, out var predicateErrors) is null)
            errors.AddRange(predicateErrors.Select(e => "Condition: " + e));

        if (IsAlert && task.EmailAccountId is null)
            errors.Add("\"Just email me\" needs an account to send the email from.");

        return errors;
    }

    /// <summary>
    /// The probe's arguments against the tool's input schema: an object of at most
    /// <see cref="MaxArgumentsChars"/>, every required property present, and no reserved name
    /// (<c>account</c>, <c>approvalNote</c>).
    /// </summary>
    public static IReadOnlyList<string> ArgumentErrors(ToolEntry entry, JsonElement arguments)
    {
        var errors = new List<string>();
        if (arguments.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            arguments = JsonSerializer.SerializeToElement(new { });
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            errors.Add("The tool's arguments must be a JSON object.");
            return errors;
        }
        if (arguments.GetRawText().Length > MaxArgumentsChars)
            errors.Add($"The tool's arguments are longer than {MaxArgumentsChars} characters.");

        foreach (var reserved in new[] { ToolPolicy.AccountProperty, "approvalNote" })
        {
            if (arguments.TryGetProperty(reserved, out _))
                errors.Add($"'{reserved}' is not one of the tool's own arguments.");
        }

        var schema = entry.Tool.ProtocolTool.InputSchema;
        if (schema.ValueKind == JsonValueKind.Object && schema.TryGetProperty("required", out var required) &&
            required.ValueKind == JsonValueKind.Array)
        {
            foreach (var name in required.EnumerateArray().Select(r => r.GetString()).OfType<string>())
            {
                if (name != ToolPolicy.AccountProperty && !arguments.TryGetProperty(name, out _))
                    errors.Add($"'{entry.Name}' needs the argument '{name}'.");
            }
        }

        return errors;
    }
}

/// <summary>Time helpers for triggers: the task's zone and the optional "only between" window.</summary>
public static class TriggerSchedule
{
    public static TimeZoneInfo ZoneOf(string? timeZone)
    {
        try
        {
            return string.IsNullOrWhiteSpace(timeZone) ? TimeZoneInfo.Utc : TimeZoneInfo.FindSystemTimeZoneById(timeZone.Trim());
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }

    public static bool IsKnownZone(string? timeZone)
    {
        try
        {
            return string.IsNullOrWhiteSpace(timeZone) || TimeZoneInfo.FindSystemTimeZoneById(timeZone.Trim()) is not null;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }
    }

    public static bool TryParseWindow(string? cron, string? timeZone, out CronExpression expression, out TimeZoneInfo zone) =>
        TaskDefinition.TryParseSchedule(cron, timeZone, out expression, out zone);

    /// <summary>Whether the minute containing <paramref name="now"/> matches the window (no window = always).</summary>
    public static bool InWindow(string? window, string? timeZone, DateTimeOffset now)
    {
        if (window is null)
            return true;
        if (!TryParseWindow(window, timeZone, out var expression, out var zone))
            return true;   // validated on save; a window that stopped parsing does not silence the trigger
        var minute = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, now.Offset).ToUniversalTime();
        return expression.GetNextOccurrence(minute.AddTicks(-1), zone) == minute;
    }

    /// <summary>The start of the window's next minute after <paramref name="now"/>, or null when it never matches again.</summary>
    public static DateTimeOffset? NextWindowStart(string window, string? timeZone, DateTimeOffset now) =>
        TryParseWindow(window, timeZone, out var expression, out var zone) ? expression.GetNextOccurrence(now, zone) : null;
}
