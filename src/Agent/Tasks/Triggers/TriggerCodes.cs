using SmarterMailAgent.Storage;

namespace SmarterMailAgent.Tasks.Triggers;

/// <summary>Failure codes of condition tasks, and which of them pause a task at once.</summary>
public static class TriggerCodes
{
    public const string ProbeFailed = "PROBE_FAILED";
    public const string ProbeNotJson = "PROBE_NOT_JSON";
    public const string ProbeInvalid = "PROBE_INVALID";
    public const string PredicateUnreadable = "PREDICATE_UNREADABLE";
    public const string PredicateLimit = "PREDICATE_LIMIT";
    public const string StateUnreadable = "TRIGGER_STATE_UNREADABLE";
    public const string EmailFailed = "EMAIL_FAILED";

    /// <summary>
    /// Hard: no retry can fix it, so the task pauses at once. The account codes are
    /// <see cref="TaskStore.HardFailures"/>; a stored predicate that does not parse, or a probe tool that
    /// is not a read (a tampered definition), join them.
    /// </summary>
    public static bool IsHard(string code) =>
        TaskStore.HardFailures.Contains(code) || code is PredicateUnreadable or ProbeInvalid;

    public static string? Explain(string code) => code switch
    {
        ProbeFailed => "The condition's check failed: the tool answered with an error.",
        ProbeNotJson => "The condition's check did not answer with JSON.",
        ProbeInvalid => "The condition's check is not a tool that only reads.",
        PredicateUnreadable => "The condition could not be read (it no longer fits the condition language).",
        PredicateLimit => "The condition needed too much work on the tool's result.",
        EmailFailed => "The alert email could not be sent.",
        _ => null,
    };
}
