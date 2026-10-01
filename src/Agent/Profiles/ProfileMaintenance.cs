using SmarterMailAgent.Auth;
using SmarterMailAgent.Server;
using SmarterMailAgent.Storage;

namespace SmarterMailAgent.Profiles;

/// <summary>
/// Server-mode housekeeping. Every minute: expired passkey ceremonies. Once a day:
/// <list type="bullet">
///   <item>profiles nobody opened for <c>PROFILE_IDLE_DAYS</c> are deleted (their delegated
///   accounts, the only ones the server can open alone, are revoked first);</item>
///   <item>delegated accounts untouched for a day are refreshed, so a task that runs weekly still
///   finds a live refresh token. The rotated token is saved like any other.</item>
/// </list>
/// </summary>
public sealed class ProfileMaintenance(
    PasskeyService passkeys,
    ProfileStore store,
    ProfileRegistry registry,
    AccountRestorer restorer,
    ServerOptions options,
    ILogger<ProfileMaintenance> logger) : BackgroundService
{
    private static readonly TimeSpan KeepAliveAge = TimeSpan.FromHours(20);
    private DateTimeOffset _lastDaily = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                passkeys.Sweep();
                if (DateTimeOffset.UtcNow - _lastDaily > TimeSpan.FromHours(24))
                {
                    _lastDaily = DateTimeOffset.UtcNow;
                    await DailyAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Profile maintenance failed.");
            }
        }
    }

    internal async Task DailyAsync(CancellationToken ct)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-options.ProfileIdleDays).ToUnixTimeMilliseconds();
        foreach (var id in store.IdleProfiles(cutoff))
        {
            if (registry.Find(id) is not null)
                continue;   // in use right now

            var runtime = registry.AcquireTask(id);
            try
            {
                await runtime.RestoreAsync(restorer, SessionStore.MaxAccounts, ct);
                await runtime.RevokeAllAsync();
                store.DeleteProfile(id);
                logger.LogInformation("Idle profile deleted.");
            }
            finally
            {
                await runtime.ReleaseTaskAsync();
            }
        }

        if (registry.ServerSealer is null)
            return;

        var stale = DateTimeOffset.UtcNow.Add(-KeepAliveAge).ToUnixTimeMilliseconds();
        foreach (var profileId in store.ProfilesWithDelegatedAccounts(stale))
        {
            var runtime = registry.AcquireTask(profileId);
            try
            {
                var ids = store.Accounts(profileId)
                    .Where(a => a.Seal == ProfileStore.SealServer && a.State == "ok" && a.UpdatedAt < stale)
                    .Select(a => a.Id)
                    .Where(a => runtime.Accounts.FindById(a) is null)
                    .ToHashSet(StringComparer.Ordinal);
                if (ids.Count > 0)
                    await runtime.RestoreAsync(restorer, SessionStore.MaxAccounts, ct, ids);
            }
            finally
            {
                await runtime.ReleaseTaskAsync();
            }
        }
    }
}
