namespace SmarterMailAgent.Tasks.Approvals;

/// <summary>What a run's model and its report email are told about approval writes. Built from the definition only.</summary>
public static class ApprovalPrompt
{
    /// <summary>The "Changes that need approval" section of the system prompt; empty when the task has none.</summary>
    public static IReadOnlyList<string> Lines(IReadOnlyCollection<string>? approvalWrites)
    {
        if (approvalWrites is not { Count: > 0 })
            return [];

        return
        [
            "# Changes that need approval",
            $"- These tools never run during this run: {string.Join(", ", approvalWrites.OrderBy(n => n, StringComparer.Ordinal))}. Calling one queues that exact call for the user to approve later. When the answer says it was queued, the change has NOT happened.",
            "- Pass `approvalNote` with each of them: one sentence telling the user why the change is needed.",
            "- Do not retry a queued call, and do not make the same change another way. Queued changes do not count against the change limit above, but the queue has its own limit per run.",
            "- List every queued change in your report as awaiting approval.",
            "",
        ];
    }

    /// <summary>
    /// The fixed footer appended to an emailed report when the run queued proposals. No arguments,
    /// no per-proposal links, nothing that approves from email: email is the channel an attacker
    /// controls.
    /// </summary>
    public static string? EmailFooter(int count, DateTimeOffset? earliestExpiry, TimeZoneInfo zone, string? appUrl)
    {
        if (count <= 0)
            return null;
        var expiry = earliestExpiry is { } e
            ? $" {(count == 1 ? "It expires" : "The first expires")} {TimeZoneInfo.ConvertTime(e, zone):yyyy-MM-dd HH:mm} ({zone.Id})."
            : "";
        var where = string.IsNullOrEmpty(appUrl) ? "SmarterMail Agent" : $"SmarterMail Agent ({appUrl})";
        return $"{count} change{(count == 1 ? " is" : "s are")} waiting for your approval in {where} → Tasks → To approve.{expiry}";
    }

    /// <summary>The report with the footer, or the report unchanged when there is no footer.</summary>
    public static string? WithFooter(string? report, string? footer) =>
        footer is null
            ? report
            : $"{(string.IsNullOrWhiteSpace(report) ? "(The task finished without a report.)" : report)}\n\n{footer}";
}
