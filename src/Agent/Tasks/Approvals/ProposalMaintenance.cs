using SmarterMailAgent.Storage;

namespace SmarterMailAgent.Tasks.Approvals;

/// <summary>
/// At startup, turns proposals a dead process left <c>executing</c> into <c>unknown</c> (never
/// retried); then every 30 seconds expires pending proposals past their time, erasing their payloads;
/// and once an hour deletes decided proposals older than <see cref="RetentionDays"/> or beyond the
/// newest <see cref="KeepPerProfile"/> of a profile. Logs counts only.
/// </summary>
public sealed class ProposalMaintenance(ProposalStore store, ILogger<ProposalMaintenance> logger) : BackgroundService
{
    public static readonly TimeSpan Tick = TimeSpan.FromSeconds(30);

    public static readonly TimeSpan PruneEvery = TimeSpan.FromHours(1);

    /// <summary>Decided proposals (executed, failed, denied, expired, unknown) are kept this long…</summary>
    public const int RetentionDays = 30;

    /// <summary>…and at most this many per profile, newest first.</summary>
    public const int KeepPerProfile = 200;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var abandoned = store.AbandonExecuting();
            if (abandoned > 0)
                logger.LogWarning("{Count} approved change(s) were interrupted by a restart; marked unknown.", abandoned);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not check for interrupted approvals.");
        }

        var lastPrune = DateTimeOffset.MinValue;
        using var timer = new PeriodicTimer(Tick);
        do
        {
            try
            {
                var expired = store.ExpireDue(DataStore.Now());
                if (expired > 0)
                    logger.LogInformation("{Count} proposal(s) expired.", expired);

                if (DateTimeOffset.UtcNow - lastPrune >= PruneEvery)
                {
                    lastPrune = DateTimeOffset.UtcNow;
                    var pruned = Prune(store, DataStore.Now());
                    if (pruned > 0)
                        logger.LogInformation("{Count} decided proposal(s) pruned.", pruned);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Proposal maintenance pass failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>One retention pass at <paramref name="now"/> (Unix ms).</summary>
    public static int Prune(ProposalStore store, long now) =>
        store.PruneDecided(now - (long)TimeSpan.FromDays(RetentionDays).TotalMilliseconds, KeepPerProfile);
}

public static class ApprovalServices
{
    /// <summary>The run-side queue, executor and the maintenance sweep (the store is registered with server mode). Needs scheduled tasks.</summary>
    public static IServiceCollection AddApprovals(this IServiceCollection services)
    {
        services.AddSingleton<ApprovalQueue>();
        services.AddSingleton<ProposalExecutor>();
        services.AddHostedService<ProposalMaintenance>();
        return services;
    }
}
