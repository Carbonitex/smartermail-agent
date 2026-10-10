using System.Security.Cryptography;
using System.Text.Json;
using Cronos;
using SmarterMailAgent.Auth;
using SmarterMailAgent.Mcp;
using SmarterMailAgent.Profiles;

namespace SmarterMailAgent.Tasks;

/// <summary>
/// What a scheduled task does, sealed with <c>DATA_KEY</c> in <c>tasks.definition</c>: the server
/// must read it while nobody is signed in.
/// </summary>
/// <param name="AccountIds">Profile account ids; each must be delegated (sealed with the server key).</param>
/// <param name="AllowedWrites">The only write tools the run may call, by name. Empty = a read-only task.</param>
/// <param name="EmailAccountId">Mail the result to this account's own address (one of <paramref name="AccountIds"/>, read-write, with a mailbox).</param>
/// <param name="Approvals">Which allowed writes are proposed for approval instead of run (null on older tasks: none).</param>
public sealed record TaskDefinition(
    int Version,
    string Name,
    string Prompt,
    string Cron,
    string TimeZone,
    IReadOnlyList<string> AccountIds,
    IReadOnlyList<string> AllowedWrites,
    int MaxWrites,
    string Model,
    string? EmailAccountId,
    global::SmarterMailAgent.Tasks.Approvals.TaskApprovals? Approvals = null)
{
    public const int MaxName = 80;
    public const int MaxPrompt = 4000;
    public const int MaxWritesLimit = 50;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string Seal(Sealer sealer, string profileId, string taskId)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(this, Json);
        try
        {
            return sealer.SealString(plaintext, ProfileCrypto.TaskDefinitionLabel, ProfileCrypto.Context(profileId, taskId));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public static TaskDefinition? Open(Sealer sealer, string profileId, string taskId, string sealedValue)
    {
        var plaintext = sealer.OpenString(sealedValue, ProfileCrypto.TaskDefinitionLabel, ProfileCrypto.Context(profileId, taskId));
        if (plaintext is null)
            return null;
        try
        {
            return JsonSerializer.Deserialize<TaskDefinition>(plaintext, Json);
        }
        catch (JsonException)
        {
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <summary>Five-field cron (minute hour day month weekday), in <paramref name="timeZone"/>.</summary>
    public static bool TryParseSchedule(string? cron, string? timeZone, out CronExpression expression, out TimeZoneInfo zone)
    {
        expression = null!;
        zone = TimeZoneInfo.Utc;
        if (string.IsNullOrWhiteSpace(cron) || cron.Length > 120)
            return false;
        try
        {
            expression = CronExpression.Parse(cron.Trim(), CronFormat.Standard);
            zone = string.IsNullOrWhiteSpace(timeZone) ? TimeZoneInfo.Utc : TimeZoneInfo.FindSystemTimeZoneById(timeZone.Trim());
            return true;
        }
        catch (Exception ex) when (ex is CronFormatException or TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }
    }

    /// <summary>
    /// The next run strictly after <paramref name="after"/>, or null when the expression never fires
    /// again. A server that was down catches up once: the scheduler runs a task that is due and then
    /// asks for the next occurrence after <i>now</i>, so missed occurrences collapse into one run.
    /// </summary>
    public static DateTimeOffset? NextRun(string cron, string timeZone, DateTimeOffset after) =>
        TryParseSchedule(cron, timeZone, out var expression, out var zone)
            ? expression.GetNextOccurrence(after, zone)
            : null;

    /// <summary>
    /// The shortest gap between runs over the next few weeks: one generous look ahead catches
    /// "every minute" and "*/5" as well as schedules that bunch up at certain hours.
    /// </summary>
    public static TimeSpan ShortestInterval(CronExpression expression, TimeZoneInfo zone, DateTimeOffset from)
    {
        var shortest = TimeSpan.MaxValue;
        var previous = expression.GetNextOccurrence(from, zone);
        for (var i = 0; i < 200 && previous is { } p; i++)
        {
            var next = expression.GetNextOccurrence(p, zone);
            if (next is not { } n)
                break;
            if (n - p < shortest)
                shortest = n - p;
            previous = n;
        }
        return shortest;
    }

    /// <summary>
    /// Every reason this definition cannot be saved for the given profile, or empty. Tools are checked
    /// against what the task's accounts could actually run, so the allowlist cannot name a tool the
    /// accounts' roles never see.
    /// </summary>
    public IReadOnlyList<string> Validate(
        IReadOnlyDictionary<string, ProfileRuntime.RowInfo> delegatedRows, ToolCatalog catalog, TimeSpan minInterval)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > MaxName)
            errors.Add($"Give the task a name (at most {MaxName} characters).");
        if (string.IsNullOrWhiteSpace(Prompt) || Prompt.Length > MaxPrompt)
            errors.Add($"Describe what the task should do (at most {MaxPrompt} characters).");
        if (string.IsNullOrWhiteSpace(Model) || Model.Length > 200)
            errors.Add("Choose a model.");
        if (MaxWrites is < 0 or > MaxWritesLimit)
            errors.Add($"The change limit must be between 0 and {MaxWritesLimit}.");

        if (!TryParseSchedule(Cron, TimeZone, out var expression, out var zone))
            errors.Add("The schedule is not a valid five-field cron expression in a known time zone.");
        else if (ShortestInterval(expression, zone, DateTimeOffset.UtcNow) < minInterval)
            errors.Add($"Runs must be at least {(int)minInterval.TotalMinutes} minutes apart.");

        if (AccountIds is not { Count: > 0 })
            errors.Add("Pick at least one account for the task.");

        var roles = new List<AccountRole>();
        foreach (var id in AccountIds ?? [])
        {
            if (!delegatedRows.TryGetValue(id, out var row) || row.Entry is null)
                errors.Add("Every account a task uses must allow scheduled tasks (Profile menu).");
            else
                roles.Add(row.Entry.Role);
        }

        foreach (var name in AllowedWrites ?? [])
        {
            if (!catalog.TryGet(name, out var entry) || !entry.Write)
                errors.Add($"'{name}' is not a tool that makes changes.");
            else if (!roles.Any(r => ToolPolicy.RoleAllows(r, entry.Scope)))
                errors.Add($"None of the task's accounts can use '{name}'.");
        }

        if ((AllowedWrites?.Count ?? 0) > 0 && (AccountIds ?? []).All(id =>
                !delegatedRows.TryGetValue(id, out var row) || row.Entry is not { ReadOnly: false }))
            errors.Add("Changes need at least one account signed in with \"Allow changes\".");

        if (EmailAccountId is { } emailId)
        {
            if (!(AccountIds ?? []).Contains(emailId) || !delegatedRows.TryGetValue(emailId, out var row) ||
                row.Entry is not { ReadOnly: false } entry || entry.Role == AccountRole.SysAdmin)
                errors.Add("Results can only be emailed from one of the task's mailbox accounts signed in with \"Allow changes\".");
        }

        return errors;
    }
}
