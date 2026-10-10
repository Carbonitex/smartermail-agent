using SmarterMailAgent.Storage;

namespace SmarterMailAgent.Tasks.Approvals;

/// <summary>
/// At startup, turns proposals a dead process left <c>executing</c> into <c>unknown</c> (never
/// retried); then every 30 seconds expires pending proposals past their time, erasing their payloads.
/// Logs counts only.
/// </summary>
public sealed class ProposalMaintenance(ProposalStore store, ILogger<ProposalMaintenance> logger) : BackgroundService
{
    public static readonly TimeSpan Tick = TimeSpan.FromSeconds(30);

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

        using var timer = new PeriodicTimer(Tick);
        do
        {
            try
            {
                var expired = store.ExpireDue(DataStore.Now());
                if (expired > 0)
                    logger.LogInformation("{Count} proposal(s) expired.", expired);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Proposal expiry pass failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

public static class ApprovalServices
{
    /// <summary>The approval queue: store, run-side queue, executor and the expiry sweep. Needs scheduled tasks.</summary>
    public static IServiceCollection AddApprovals(this IServiceCollection services)
    {
        services.AddSingleton<ProposalStore>();
        services.AddSingleton<ApprovalQueue>();
        services.AddSingleton<ProposalExecutor>();
        services.AddHostedService<ProposalMaintenance>();
        return services;
    }
}
