using System.Collections.Concurrent;
using SmarterMailAgent.Profiles;
using SmarterMailAgent.Server;
using SmarterMailAgent.Storage;
using SmarterMailAgent.Tasks.Triggers;

namespace SmarterMailAgent.Tasks;

/// <summary>
/// Starts scheduled runs. Every 30 seconds it claims the tasks that are due (moving each one's
/// <c>next_run_at</c> on first, so a run is never started twice) and runs them, at most
/// <c>TASK_CONCURRENCY</c> at once and never two runs of one task together. A task that was due
/// while the server was down runs once, then continues from the next occurrence after now.
/// Single instance: run one replica in server mode.
/// </summary>
public sealed class TaskRunScheduler(
    TaskStore tasks,
    TaskRunner runner,
    ProfileRegistry registry,
    ServerOptions options,
    ILogger<TaskRunScheduler> logger) : BackgroundService
{
    public static readonly TimeSpan Tick = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _slots = new(Math.Max(1, options.TaskConcurrency));
    private readonly ConcurrentDictionary<string, Task> _running = new(StringComparer.Ordinal);
    private CancellationToken _stopping = CancellationToken.None;

    /// <summary>Runs in flight, by task id.</summary>
    public int Running => _running.Count;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stopping = stoppingToken;
        var abandoned = tasks.AbandonRunning();
        if (abandoned > 0)
            logger.LogWarning("{Count} task run(s) were interrupted by a restart.", abandoned);

        using var timer = new PeriodicTimer(Tick);
        do
        {
            try
            {
                StartDue(DateTimeOffset.UtcNow);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Task scheduling pass failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>One pass: claim and start what is due at <paramref name="now"/>. Returns how many started.</summary>
    internal int StartDue(DateTimeOffset now)
    {
        if (!options.TasksEnabled)
            return 0;

        var started = 0;
        foreach (var task in tasks.Due(now.ToUnixTimeMilliseconds(), limit: 50))
        {
            if (_running.ContainsKey(task.Id) || task.NextRunAt is not { } due)
                continue;

            var next = ScheduleOf(task) is { } schedule ? TaskDefinition.NextRun(schedule.Cron, schedule.TimeZone, now) : null;
            if (!tasks.Claim(task.Id, due, next?.ToUnixTimeMilliseconds()))
                continue;

            if (Start(task, ProfileCrypto.NewId(), "schedule", dryRun: false))
                started++;
        }
        return started;
    }

    /// <summary>
    /// "Run now" / "Test run" from the UI. False when that task is already running. The run id is
    /// returned at once; the run itself carries on in the background.
    /// </summary>
    public bool RunNow(TaskRow task, bool dryRun, out string runId)
    {
        runId = ProfileCrypto.NewId();
        return Start(task, runId, "manual", dryRun);
    }

    /// <summary>
    /// A condition task's run (<c>condition</c> from the prober, <c>manual</c> for its "Run now"), with
    /// the probe's evidence as the seed. False when that task is already running: the fire was not accepted.
    /// </summary>
    public bool StartTriggered(TaskRow task, string trigger, bool dryRun, TriggerSeed seed, out string runId)
    {
        runId = ProfileCrypto.NewId();
        return Start(task, runId, trigger, dryRun, seed);
    }

    /// <summary>Whether a run of this task is in flight.</summary>
    public bool IsRunning(string taskId) => _running.ContainsKey(taskId);

    private bool Start(TaskRow task, string runId, string trigger, bool dryRun, TriggerSeed? seed = null)
    {
        var gate = new TaskCompletionSource();
        if (!_running.TryAdd(task.Id, gate.Task))
            return false;

        _ = Task.Run(async () =>
        {
            var slot = false;
            try
            {
                await _slots.WaitAsync(_stopping);
                slot = true;
                await runner.RunAsync(task, runId, trigger, dryRun, _stopping, seed);
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
            {
                // Shutting down; AbandonRunning marks the row at the next start.
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Task {Task} run crashed.", task.Id);
            }
            finally
            {
                if (slot)
                    _slots.Release();
                _running.TryRemove(task.Id, out _);
                gate.TrySetResult();
            }
        }, CancellationToken.None);
        return true;
    }

    /// <summary>Waits for runs in flight (tests, and a graceful shutdown).</summary>
    internal Task WhenIdleAsync() => Task.WhenAll(_running.Values);

    /// <summary>The schedule lives inside the sealed definition: open it with the server key.</summary>
    private (string Cron, string TimeZone)? ScheduleOf(TaskRow task) =>
        registry.ServerSealer is { } sealer &&
        TaskDefinition.Open(sealer, task.ProfileId, task.Id, task.Definition) is { } definition
            ? (definition.Cron, definition.TimeZone)
            : null;
}

public static class TaskRunSchedulerExtensions
{
    /// <summary>The scheduler is a singleton (the UI's "Run now" needs it) and a hosted service.</summary>
    public static IServiceCollection AddTaskRunScheduler(this IServiceCollection services)
    {
        services.AddSingleton<TaskRunScheduler>();
        services.AddHostedService(sp => sp.GetRequiredService<TaskRunScheduler>());
        return services;
    }
}
