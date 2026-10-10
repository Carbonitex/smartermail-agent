using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using SmarterMailAgent.Auth;
using SmarterMailAgent.Server;
using SmarterMailAgent.Storage;

namespace SmarterMailAgent.Profiles;

/// <summary>
/// One profile in memory: the single owner of its live accounts while anyone uses them — any number
/// of its browser sessions, and its scheduled tasks. SmarterMail keeps one token per (user, clientId),
/// so two live copies of a stored account (two browsers, or a browser and a task) would each rotate
/// the other's refresh token dead; sharing one <see cref="Account"/> and its refresh lock prevents it.
/// <para>
/// Holds the profile's accounts key while at least one session has unlocked it. When the last
/// session goes, the accounts sealed with that key are forgotten (not revoked: the store keeps their
/// refresh tokens) and the key is dropped. Delegated accounts (sealed with <c>DATA_KEY</c>) can stay
/// for a running task. When nothing holds the profile any more it leaves the <see cref="ProfileRegistry"/>.
/// </para>
/// </summary>
public sealed class ProfileRuntime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly ProfileRegistry _registry;
    private readonly ProfileStore _store;
    private readonly Sealer? _serverSealer;
    private readonly ILogger _logger;
    private readonly object _lock = new();
    private readonly Dictionary<string, RowInfo> _rows = new(StringComparer.Ordinal);
    private Sealer? _accountsSealer;

    internal int Sessions;
    internal int Tasks;

    /// <summary>What is known about one stored account. <see cref="Entry"/> is null until it was opened.</summary>
    public sealed record RowInfo(string Id, string Seal, string State, ResumeAccount? Entry);

    internal ProfileRuntime(string profileId, ProfileRegistry registry, ProfileStore store, Sealer? serverSealer, ILogger logger)
    {
        ProfileId = profileId;
        _registry = registry;
        _store = store;
        _serverSealer = serverSealer;
        _logger = logger;
    }

    public string ProfileId { get; }

    public AccountSet Accounts { get; } = new();

    public bool IsUnlocked
    {
        get { lock (_lock) return _accountsSealer is not null; }
    }

    public bool CanDelegate => _serverSealer is not null;

    private long _idleTicks;

    /// <summary>
    /// The profile's own session idle timeout (Settings), already clamped to what the server allows;
    /// null = the server default. Every session of the profile follows it, including ones already open.
    /// </summary>
    public TimeSpan? IdleTimeout
    {
        get => Interlocked.Read(ref _idleTicks) is > 0 and var ticks ? TimeSpan.FromTicks(ticks) : null;
        internal set => Interlocked.Exchange(ref _idleTicks, value?.Ticks ?? 0);
    }

    /// <summary>Checks <paramref name="accountsKey"/> against the stored check value and keeps it while unlocked.</summary>
    public bool Unlock(byte[] accountsKey, string storedCheck)
    {
        if (accountsKey is not { Length: 32 } || !ProfileCrypto.AccountsKeyMatches(accountsKey, storedCheck))
            return false;

        lock (_lock)
            _accountsSealer ??= new Sealer(accountsKey.ToArray());
        return true;
    }

    /// <summary>For a brand-new profile: the creator's key, already checked by the caller.</summary>
    internal void UnlockNew(byte[] accountsKey)
    {
        lock (_lock)
            _accountsSealer = new Sealer(accountsKey.ToArray());
    }

    // ---------------------------------------------------------------- stored rows

    /// <summary>The blob for <paramref name="account"/> under <paramref name="seal"/>; null when that key is not available.</summary>
    internal string? SealAccount(Account account, string seal)
    {
        var sealer = SealerFor(seal);
        if (sealer is null)
            return null;

        var plaintext = JsonSerializer.SerializeToUtf8Bytes(AccountBuilder.ToStored(account), Json);
        try
        {
            return sealer.SealString(plaintext, LabelFor(seal), ProfileCrypto.Context(ProfileId, account.Id));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private ResumeAccount? OpenRow(StoredAccountRow row)
    {
        var sealer = SealerFor(row.Seal);
        var plaintext = sealer?.OpenString(row.Blob, LabelFor(row.Seal), ProfileCrypto.Context(ProfileId, row.Id));
        if (plaintext is null)
            return null;
        try
        {
            return JsonSerializer.Deserialize<ResumeAccount>(plaintext, Json);
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

    private Sealer? SealerFor(string seal)
    {
        lock (_lock)
            return seal == ProfileStore.SealServer ? _serverSealer : _accountsSealer;
    }

    private static string LabelFor(string seal) =>
        seal == ProfileStore.SealServer ? ProfileCrypto.ServerAccountsLabel : ProfileCrypto.AccountsLabel;

    /// <summary>Every stored row this runtime knows about (opened ones carry their entry).</summary>
    public IReadOnlyList<RowInfo> Rows
    {
        get { lock (_lock) return _rows.Values.ToList(); }
    }

    public RowInfo? Row(string id)
    {
        lock (_lock)
            return _rows.GetValueOrDefault(id);
    }

    /// <summary>The stored row for this login, so a fresh sign-in replaces it and keeps its id (tasks refer to it).</summary>
    public RowInfo? RowForLogin(string baseUrl, string login)
    {
        lock (_lock)
        {
            return _rows.Values.FirstOrDefault(r => r.Entry is { } e &&
                string.Equals(e.BaseUrl.TrimEnd('/'), baseUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) &&
                string.Equals(e.Login, login, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// Brings back every stored account this runtime can open and does not hold yet (with
    /// <paramref name="onlyIds"/>: just those). Accounts SmarterMail refuses are marked rejected and
    /// stay listed for a fresh sign-in; unreachable or throttled ones are left as they are.
    /// </summary>
    public async Task<IReadOnlyList<AccountRestorer.Skipped>> RestoreAsync(
        AccountRestorer restorer, int maxAccounts, CancellationToken ct, IReadOnlySet<string>? onlyIds = null)
    {
        var rows = _store.Accounts(ProfileId);
        var live = Accounts.Accounts.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        var candidates = new List<AccountRestorer.Candidate>();

        foreach (var row in rows)
        {
            var entry = OpenRow(row);
            lock (_lock)
                _rows[row.Id] = new RowInfo(row.Id, row.Seal, row.State, entry ?? _rows.GetValueOrDefault(row.Id)?.Entry);

            if (entry is null || live.Contains(row.Id) || (onlyIds is not null && !onlyIds.Contains(row.Id)))
                continue;
            candidates.Add(new AccountRestorer.Candidate(entry, row.Id));
        }

        var free = Math.Max(0, maxAccounts - live.Count);
        var result = await restorer.RestoreAsync(candidates, free, throttledFailsAll: false, "Profile", ct);

        foreach (var (candidate, account) in result.Accounts)
        {
            var seal = Row(account.Id)?.Seal ?? ProfileStore.SealProfile;
            Attach(account, seal);
            if (Accounts.Add(account, maxAccounts, out var replaced) == AccountSet.AddStatus.LimitReached)
            {
                await account.ForgetAsync();
                continue;
            }

            if (replaced is not null && !ReferenceEquals(replaced, account))
                await replaced.ForgetAsync();

            // The restore rotated the refresh token: the stored copy is dead until this write lands.
            await PersistAsync(account, seal);
            SetState(account.Id, "ok");
        }

        foreach (var skip in result.Skipped.Where(s => s.Reason is "REJECTED" or "EXPIRED"))
        {
            if (skip.Candidate.Id is { } id)
            {
                _store.SetAccountState(ProfileId, id, "rejected");
                SetState(id, "rejected");
            }
        }

        return result.Skipped;
    }

    private void SetState(string id, string state)
    {
        lock (_lock)
        {
            if (_rows.TryGetValue(id, out var row))
                _rows[id] = row with { State = state };
        }
    }

    /// <summary>
    /// Saves a freshly signed-in account to the profile (sealed with the accounts key, or with
    /// <c>DATA_KEY</c> when its row was delegated before). False when the needed key is not available.
    /// </summary>
    public bool Save(Account account)
    {
        var seal = Row(account.Id)?.Seal ?? ProfileStore.SealProfile;
        if (SealAccount(account, seal) is not { } blob)
            return false;

        _store.UpsertAccount(new StoredAccountRow(account.Id, ProfileId, seal, "ok", blob, DataStore.Now()));
        lock (_lock)
            _rows[account.Id] = new RowInfo(account.Id, seal, "ok", AccountBuilder.ToStored(account) with { RefreshToken = "" });
        Attach(account, seal);
        return true;
    }

    /// <summary>
    /// Turns delegation on (re-seal with <c>DATA_KEY</c>, so scheduled tasks can use the account while
    /// nobody is signed in) or off (back under the accounts key). The account must be live.
    /// </summary>
    public bool SetDelegation(string accountId, bool delegated)
    {
        var account = Accounts.FindById(accountId);
        if (account is null)
            return false;

        var seal = delegated ? ProfileStore.SealServer : ProfileStore.SealProfile;
        if (SealAccount(account, seal) is not { } blob)
            return false;

        if (!_store.UpdateAccountBlob(ProfileId, accountId, seal, blob))
            return false;

        lock (_lock)
        {
            if (_rows.TryGetValue(accountId, out var row))
                _rows[accountId] = row with { Seal = seal, State = "ok" };
        }
        Attach(account, seal);
        _logger.LogInformation("Account delegation {State} for a profile account ({Role}).", delegated ? "on" : "off", account.Role);
        return true;
    }

    /// <summary>Removes an account from the profile: revoked on SmarterMail, deleted from the store.</summary>
    public async Task<bool> RemoveAsync(string accountId)
    {
        var account = Accounts.Remove(accountId);
        var deleted = _store.DeleteAccount(ProfileId, accountId);
        lock (_lock)
            _rows.Remove(accountId);

        if (account is not null)
            await account.DisposeAsync();
        return deleted || account is not null;
    }

    /// <summary>SmarterMail refused this account's refresh token: drop it from memory, keep its row for a fresh sign-in.</summary>
    public async Task MarkRejectedAsync(Account account)
    {
        if (Accounts.Remove(account.Id) is { } removed)
            await removed.ForgetAsync();
        _store.SetAccountState(ProfileId, account.Id, "rejected");
        SetState(account.Id, "rejected");
    }

    private void Attach(Account account, string seal) =>
        account.Persist = a => PersistAsync(a, seal);

    private Task PersistAsync(Account account, string seal)
    {
        try
        {
            if (SealAccount(account, seal) is { } blob)
                _store.UpdateAccountBlob(ProfileId, account.Id, seal, blob);
            else
                _logger.LogWarning("A rotated token for a profile account could not be saved: the profile is locked.");
        }
        catch (Exception ex)
        {
            // The account keeps working in memory; the stored copy is stale until the next rotation.
            _logger.LogError(ex, "Saving a rotated token for a profile account failed.");
        }

        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------- leases

    /// <summary>A browser session of this profile ended (logout, idle, expiry).</summary>
    public Task ReleaseSessionAsync() => ReleaseAsync(task: false);

    /// <summary>A scheduled run of this profile finished.</summary>
    public Task ReleaseTaskAsync() => ReleaseAsync(task: true);

    private async Task ReleaseAsync(bool task)
    {
        var (lastSession, empty) = _registry.Release(this, task);
        var forget = new List<Account>();

        if (empty)
        {
            forget.AddRange(Accounts.TakeAll());
        }
        else if (lastSession)
        {
            // No browser holds the key any more. Accounts sealed with it cannot be saved after their
            // next rotation, so they go now; delegated ones may stay for a running task.
            forget.AddRange(Accounts.TakeWhere(a => Row(a.Id)?.Seal != ProfileStore.SealServer));
        }

        if (empty || lastSession)
        {
            lock (_lock)
                _accountsSealer = null;
        }

        await Task.WhenAll(forget.Select(a => a.ForgetAsync().AsTask()));
        if (forget.Count > 0)
            _logger.LogInformation("Profile locked; {Count} account(s) forgotten (their stored tokens stay valid).", forget.Count);
    }

    /// <summary>
    /// The profile is being deleted: every live account is revoked, and the runtime stays empty until
    /// its remaining leases go.
    /// </summary>
    internal async Task RevokeAllAsync()
    {
        var accounts = Accounts.TakeAll();
        lock (_lock)
        {
            _rows.Clear();
            _accountsSealer = null;
        }
        await Task.WhenAll(accounts.Select(a => a.DisposeAsync().AsTask()));
    }
}

/// <summary>The live <see cref="ProfileRuntime"/>s, created on first use and dropped when nothing holds them.</summary>
public sealed class ProfileRegistry
{
    private readonly ProfileStore _store;
    private readonly ServerOptions _options;
    private readonly ILoggerFactory _loggers;
    private readonly Dictionary<string, ProfileRuntime> _live = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public ProfileRegistry(ProfileStore store, ServerOptions options, ILoggerFactory loggers)
    {
        _store = store;
        _options = options;
        _loggers = loggers;
        ServerSealer = options.DataKey is { } key ? new Sealer(key, options.DataKeyPrevious) : null;
    }

    /// <summary>Seals what the server must read unattended (<c>DATA_KEY</c>); null when it is unset.</summary>
    public Sealer? ServerSealer { get; }

    public ProfileRuntime AcquireSession(string profileId) => Acquire(profileId, task: false);

    public ProfileRuntime AcquireTask(string profileId) => Acquire(profileId, task: true);

    public ProfileRuntime? Find(string profileId)
    {
        lock (_lock)
            return _live.GetValueOrDefault(profileId);
    }

    public int Count
    {
        get { lock (_lock) return _live.Count; }
    }

    private ProfileRuntime Acquire(string profileId, bool task)
    {
        lock (_lock)
        {
            if (!_live.TryGetValue(profileId, out var runtime))
            {
                runtime = new ProfileRuntime(profileId, this, _store, ServerSealer, _loggers.CreateLogger<ProfileRuntime>())
                {
                    IdleTimeout = ClampIdle(_store.IdleMinutes(profileId)),
                };
                _live[profileId] = runtime;
            }

            if (task)
                runtime.Tasks++;
            else
                runtime.Sessions++;
            return runtime;
        }
    }

    /// <summary>Stores a profile's idle timeout (null = the server default) and applies it to its live sessions.</summary>
    public void SetIdleMinutes(string profileId, int? minutes)
    {
        _store.SetIdleMinutes(profileId, minutes);
        if (Find(profileId) is { } runtime)
            runtime.IdleTimeout = ClampIdle(minutes);
    }

    /// <summary>A stored value within today's limits (the operator may have lowered the maximum since).</summary>
    private TimeSpan? ClampIdle(int? minutes) =>
        minutes is { } m
            ? TimeSpan.FromMinutes(Math.Clamp(m, ServerOptions.ProfileMinIdleMinutes, _options.ProfileMaxIdleMinutes))
            : null;

    /// <returns>Whether that was the last session, and whether nothing holds the runtime any more.</returns>
    internal (bool LastSession, bool Empty) Release(ProfileRuntime runtime, bool task)
    {
        lock (_lock)
        {
            if (task)
                runtime.Tasks = Math.Max(0, runtime.Tasks - 1);
            else
                runtime.Sessions = Math.Max(0, runtime.Sessions - 1);

            var lastSession = !task && runtime.Sessions == 0;
            var empty = runtime.Sessions == 0 && runtime.Tasks == 0;
            if (empty && _live.TryGetValue(runtime.ProfileId, out var current) && ReferenceEquals(current, runtime))
                _live.Remove(runtime.ProfileId);
            return (lastSession, empty);
        }
    }
}
