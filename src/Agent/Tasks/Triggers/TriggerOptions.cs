using SmarterMailAgent.Server;

namespace SmarterMailAgent.Tasks.Triggers;

/// <summary>
/// Condition-triggered tasks. Read from configuration like <see cref="ServerOptions"/>; on only where
/// scheduled tasks are (server mode with <c>DATA_KEY</c>) and <c>TRIGGERS_ENABLED</c> is not false.
/// A malformed value fails startup with its name.
/// </summary>
public sealed record TriggerOptions
{
    public static readonly TriggerOptions Default = new() { TasksEnabled = true };

    public bool TasksEnabled { get; init; }

    /// <summary><c>TRIGGERS_ENABLED</c> (default true).</summary>
    public bool EnabledSetting { get; init; } = true;

    public bool Enabled => TasksEnabled && EnabledSetting;

    /// <summary><c>TRIGGER_MIN_INTERVAL_MINUTES</c>: the shortest probe interval (default 5).</summary>
    public int MinIntervalMinutes { get; init; } = 5;

    public const int MaxIntervalMinutes = 1440;

    /// <summary><c>TRIGGERS_PER_PROFILE</c>: condition tasks per profile, counted inside <c>TASKS_PER_PROFILE</c> (default 5).</summary>
    public int PerProfile { get; init; } = 5;

    /// <summary><c>TRIGGER_MAX_RUNS_PER_DAY</c>: fires per task per rolling 24 hours (default 24).</summary>
    public int MaxRunsPerDay { get; init; } = 24;

    /// <summary><c>TRIGGER_CONCURRENCY</c>: probes at once, separate from run slots (default 4).</summary>
    public int Concurrency { get; init; } = 4;

    /// <summary><c>PROBES_PER_HOST_PER_MINUTE</c>: probe calls per mail server per minute, all profiles together (default 30).</summary>
    public int ProbesPerHostPerMinute { get; init; } = 30;

    /// <summary>Interactive probes (Test probe, Run now) per mail server per minute: their own bucket, a fifth of <see cref="ProbesPerHostPerMinute"/>, at least 1.</summary>
    public int InteractivePerHostPerMinute => Math.Max(1, ProbesPerHostPerMinute / 5);

    /// <summary><c>PROBES_PER_PROFILE_PER_MINUTE</c>: interactive probes per profile per minute (default 6).</summary>
    public int InteractivePerProfilePerMinute { get; init; } = 6;

    /// <summary>One probe call, including the token refresh it may need.</summary>
    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Consecutive soft probe failures that pause a task.</summary>
    public int PauseAfterProbeFailures { get; init; } = 5;

    /// <summary>
    /// Triggers probing more often than this keep their profile's delegated accounts live between
    /// probes (a task lease), so each probe does not cost a token refresh.
    /// </summary>
    public int LeaseBelowMinutes { get; init; } = 15;

    public static TriggerOptions FromConfiguration(IConfiguration config, ServerOptions server) => new()
    {
        TasksEnabled = server.TasksEnabled,
        EnabledSetting = Bool(config, "TRIGGERS_ENABLED", true),
        MinIntervalMinutes = Math.Min(MaxIntervalMinutes, Int(config, "TRIGGER_MIN_INTERVAL_MINUTES", 5)),
        PerProfile = Int(config, "TRIGGERS_PER_PROFILE", 5),
        MaxRunsPerDay = Int(config, "TRIGGER_MAX_RUNS_PER_DAY", 24),
        Concurrency = Int(config, "TRIGGER_CONCURRENCY", 4),
        ProbesPerHostPerMinute = Int(config, "PROBES_PER_HOST_PER_MINUTE", 30),
        InteractivePerProfilePerMinute = Int(config, "PROBES_PER_PROFILE_PER_MINUTE", 6),
    };

    private static bool Bool(IConfiguration config, string name, bool fallback) =>
        config[name]?.Trim().ToLowerInvariant() switch
        {
            null or "" => fallback,
            "true" or "1" or "yes" => true,
            "false" or "0" or "no" => false,
            _ => throw new InvalidOperationException($"{name} must be true or false."),
        };

    private static int Int(IConfiguration config, string name, int fallback) =>
        config[name] is { Length: > 0 } raw
            ? int.TryParse(raw, out var value) && value > 0
                ? value
                : throw new InvalidOperationException($"{name} must be a positive whole number.")
            : fallback;
}
