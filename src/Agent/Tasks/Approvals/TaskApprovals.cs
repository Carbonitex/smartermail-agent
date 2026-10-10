using System.Text.Json.Nodes;

namespace SmarterMailAgent.Tasks.Approvals;

/// <summary>
/// The approval part of a task definition (<c>TaskDefinition.Approvals</c>; null on tasks saved
/// before approvals existed, which behave exactly as before). Every tool in <see cref="Writes"/> is
/// also in <c>AllowedWrites</c>: the run may touch it, but only by <i>proposing</i> the exact call,
/// which a person approves later. The server does not force approval on any tool; the editor
/// pre-selects it for destructive and admin-scope tools.
/// </summary>
/// <param name="Writes">The allowed writes that are proposed instead of run.</param>
/// <param name="MaxProposals">Proposals one run may create (0 to <c>TASK_MAX_PROPOSALS</c>); separate from <c>MaxWrites</c>.</param>
/// <param name="RequirePasskey">Ask for a passkey for every approval, not only destructive and admin ones.</param>
/// <param name="TtlHours">How long a proposal waits before it expires (1 to 168).</param>
public sealed record TaskApprovals(
    IReadOnlyList<string>? Writes = null,
    int? MaxProposals = null,
    bool? RequirePasskey = null,
    int? TtlHours = null)
{
    public const int DefaultMaxProposals = 10;
    public const int DefaultTtlHours = 72;
    public const int MaxTtlHours = 168;

    public IReadOnlyList<string> WriteList => Writes ?? [];
    public int ProposalLimit => MaxProposals ?? DefaultMaxProposals;
    public bool PasskeyAlways => RequirePasskey ?? false;
    public TimeSpan Ttl => TimeSpan.FromHours(TtlHours ?? DefaultTtlHours);

    /// <summary>A request's approvals with defaults filled in and duplicates dropped; null when absent.</summary>
    public static TaskApprovals? Normalize(TaskApprovals? request) =>
        request is null
            ? null
            : new TaskApprovals(
                (request.Writes ?? []).Where(w => !string.IsNullOrWhiteSpace(w)).Select(w => w.Trim())
                    .Distinct(StringComparer.Ordinal).ToList(),
                request.MaxProposals ?? DefaultMaxProposals,
                request.RequirePasskey ?? false,
                request.TtlHours ?? DefaultTtlHours);

    /// <summary>Every reason these settings cannot be saved with the task, or empty.</summary>
    public IReadOnlyList<string> Validate(IReadOnlyCollection<string> allowedWrites, int maxProposalsLimit)
    {
        var errors = new List<string>();
        foreach (var name in WriteList)
        {
            if (!allowedWrites.Contains(name))
                errors.Add($"'{name}' is set to need approval but is not one of the changes the task may make.");
        }
        if (ProposalLimit < 0 || ProposalLimit > maxProposalsLimit)
            errors.Add($"The approval limit must be between 0 and {maxProposalsLimit} per run.");
        if (TtlHours is { } ttl && (ttl < 1 || ttl > MaxTtlHours))
            errors.Add($"Proposals must wait between 1 hour and {MaxTtlHours / 24} days.");
        return errors;
    }
}

/// <summary>
/// The <c>approvalNote</c> argument: offered only on approval tools in a task run, stripped by the
/// dispatcher before the tool runs, shown to the reviewer as untrusted text.
/// </summary>
public static class ApprovalNote
{
    public const string Property = "approvalNote";
    public const int MaxLength = 500;

    /// <summary>
    /// Adds <c>approvalNote</c> to the parameters of every function in <paramref name="tools"/> (an
    /// OpenRouter function list) named in <paramref name="approvalWrites"/>. Works on the list's own
    /// (already cloned) schemas and returns the same list.
    /// </summary>
    public static JsonArray Inject(JsonArray tools, IReadOnlySet<string> approvalWrites)
    {
        if (approvalWrites.Count == 0)
            return tools;

        foreach (var node in tools)
        {
            if (node?["function"] is not JsonObject function ||
                function["name"]?.GetValue<string>() is not { } name || !approvalWrites.Contains(name))
                continue;

            if (function["parameters"] is not JsonObject parameters)
            {
                parameters = new JsonObject { ["type"] = "object" };
                function["parameters"] = parameters;
            }
            if (parameters["properties"] is not JsonObject properties)
            {
                properties = new JsonObject();
                parameters["properties"] = properties;
            }
            properties[Property] = new JsonObject
            {
                ["type"] = "string",
                ["maxLength"] = MaxLength,
                ["description"] = "One sentence for the user: why this change is needed. The change is queued for their approval, not made.",
            };
        }
        return tools;
    }

    /// <summary>What the reviewer sees of a note: at most 500 characters, no control characters.</summary>
    public static string? Clean(string? note)
    {
        if (string.IsNullOrWhiteSpace(note))
            return null;
        var cleaned = new string(note.Where(c => !char.IsControl(c) || c == '\n').ToArray()).Trim();
        return cleaned.Length > MaxLength ? cleaned[..MaxLength] : cleaned;
    }
}
