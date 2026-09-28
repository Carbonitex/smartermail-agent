namespace SmarterMailAgent.Auth;

/// <summary>
/// Caps authoritative login failures per target SmarterMail server.
///
/// Every sign-in through this service leaves from the same egress IP, and SmarterMail's intrusion
/// detection counts failed logins per <b>source IP</b>. Left alone, a handful of visitors mistyping
/// passwords against one server would get this service's IP blocked on that server for everyone.
/// SmarterMail's default IDS "Brute Force by IP" rules (see Settings → Security → IDS Rules):
/// delay at 15 failures / 10 min, block 30 min at 25 / 10 min, block 4 h at 35 / 50 min. The default
/// cap here (10 per 60 min, sliding) stays under all three: no 10-minute slice can exceed 10 and no
/// 50-minute slice can either.
///
/// Only answers SmarterMail itself counts as a failed login are recorded (see
/// <see cref="CountsAsFailure"/>); network errors, blocked hosts and our own 429s are not.
/// A success does not reset anything; failures simply age out of the window.
///
/// Attempts in flight hold a slot too (<see cref="TryBegin"/>), so concurrent sign-ins cannot
/// overshoot the cap. In memory only, one lock (sign-ins are rate-limited and rare), bounded to
/// <see cref="MaxHosts"/> tracked servers, swept by <c>SessionSweeper</c>.
/// </summary>
public sealed class HostLoginThrottle
{
    public const int DefaultLimit = 10;
    public const int DefaultWindowMinutes = 60;
    public const int DefaultMaxHosts = 10_000;

    /// <summary>How long a caller is told to wait when the cap is held only by attempts still in flight.</summary>
    public static readonly TimeSpan InFlightRetry = TimeSpan.FromSeconds(5);

    /// <summary>SmarterMail message codes that are an authoritative "wrong credentials".</summary>
    private static readonly HashSet<string> FailureCodes = new(StringComparer.Ordinal)
    {
        "USERNAME_OR_PASSWORD_INCORRECT",
        "USER_NOT_FOUND",
        "INVALID_TWO_FACTOR_CODE",
    };

    private readonly object _gate = new();
    private readonly Dictionary<string, HostState> _hosts = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;
    private readonly ILogger? _logger;

    public HostLoginThrottle(
        int limit = DefaultLimit, TimeSpan? window = null, int maxHosts = DefaultMaxHosts,
        TimeProvider? time = null, ILogger? logger = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxHosts, 1);

        Limit = limit;
        Window = window ?? TimeSpan.FromMinutes(DefaultWindowMinutes);
        if (Window <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(window), "The window must be positive.");
        MaxHosts = maxHosts;
        _time = time ?? TimeProvider.System;
        _logger = logger;
    }

    /// <summary>
    /// From <c>HOST_FAILED_LOGIN_LIMIT</c> (default 10) and <c>HOST_FAILED_LOGIN_WINDOW_MINUTES</c>
    /// (default 60). Missing, unparsable or non-positive values fall back to the defaults.
    /// </summary>
    public static HostLoginThrottle FromEnvironment(ILogger? logger = null) => new(
        EnvInt("HOST_FAILED_LOGIN_LIMIT", DefaultLimit),
        TimeSpan.FromMinutes(EnvInt("HOST_FAILED_LOGIN_WINDOW_MINUTES", DefaultWindowMinutes)),
        logger: logger);

    public int Limit { get; }
    public TimeSpan Window { get; }
    public int MaxHosts { get; }

    public int TrackedHosts
    {
        get { lock (_gate) return _hosts.Count; }
    }

    /// <summary>
    /// <c>scheme://host:port</c>, lower-case, explicit port. <c>https://Mail.Example.com/</c> and
    /// <c>https://mail.example.com:443</c> are the same server.
    /// </summary>
    public static string NormalizeHost(string baseUrl)
    {
        if (Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host))
            return $"{uri.Scheme}://{uri.IdnHost}:{uri.Port}".ToLowerInvariant();

        return baseUrl.Trim().TrimEnd('/').ToLowerInvariant();
    }

    /// <summary>
    /// True for an answer SmarterMail counts against the source IP: a wrong username or password,
    /// an unknown user, or a rejected two-factor code. On the two-factor step,
    /// <paramref name="twoFactorStep"/> also counts the legacy inline path's answer to a wrong code,
    /// which is <c>TWO_FACTOR_REQUIRED</c> again rather than <c>INVALID_TWO_FACTOR_CODE</c>.
    /// Connection failures, timeouts, unreadable replies and anything unrecognised do not count.
    /// </summary>
    public static bool CountsAsFailure(AuthOutcome outcome, bool twoFactorStep = false) => outcome switch
    {
        AuthOutcome.Failed failed => FailureCodes.Contains(failed.Code),
        AuthOutcome.TwoFactorRequired => twoFactorStep,
        _ => false,
    };

    /// <summary>
    /// Reserves a slot for one login attempt against <paramref name="baseUrl"/>. Returns null, with
    /// <paramref name="retryAfter"/> set, when recorded failures plus attempts already in flight
    /// have reached <see cref="Limit"/>. Otherwise the caller must <see cref="Attempt.Complete"/>
    /// the attempt with SmarterMail's answer and dispose it (which releases the slot).
    /// </summary>
    public Attempt? TryBegin(string baseUrl, out TimeSpan retryAfter)
    {
        var key = NormalizeHost(baseUrl);
        var now = _time.GetUtcNow();

        lock (_gate)
        {
            if (_hosts.TryGetValue(key, out var state))
            {
                state.Prune(now - Window);
                if (state.Failures.Count + state.InFlight >= Limit)
                {
                    retryAfter = RetryAfterLocked(state, now);
                    return null;
                }
            }
            else
            {
                if (_hosts.Count >= MaxHosts)
                    MakeRoomLocked(now);
                state = new HostState();
                _hosts[key] = state;
            }

            state.InFlight++;
            state.LastActivity = now;
            retryAfter = TimeSpan.Zero;
            return new Attempt(this, key);
        }
    }

    /// <summary>How long until <paramref name="baseUrl"/> takes another attempt; null if it would now.</summary>
    public TimeSpan? RetryAfter(string baseUrl)
    {
        var key = NormalizeHost(baseUrl);
        var now = _time.GetUtcNow();

        lock (_gate)
        {
            if (!_hosts.TryGetValue(key, out var state))
                return null;

            state.Prune(now - Window);
            return state.Failures.Count + state.InFlight >= Limit ? RetryAfterLocked(state, now) : null;
        }
    }

    /// <summary>Failures for <paramref name="baseUrl"/> still inside the window.</summary>
    public int FailureCount(string baseUrl)
    {
        var key = NormalizeHost(baseUrl);
        lock (_gate)
        {
            if (!_hosts.TryGetValue(key, out var state))
                return 0;
            state.Prune(_time.GetUtcNow() - Window);
            return state.Failures.Count;
        }
    }

    /// <summary>
    /// Drops every host with no failure left in the window and nothing in flight. Called by
    /// <c>SessionSweeper</c> every minute; returns how many were dropped.
    /// </summary>
    public int Sweep()
    {
        lock (_gate)
            return SweepLocked(_time.GetUtcNow());
    }

    private void Finish(string key, bool failed)
    {
        var now = _time.GetUtcNow();
        int count;

        lock (_gate)
        {
            if (!_hosts.TryGetValue(key, out var state))
            {
                // Evicted while in flight (only possible when MaxHosts is exhausted). Re-add a
                // failure so it still counts; a success has nothing to record.
                if (!failed) return;
                if (_hosts.Count >= MaxHosts)
                    MakeRoomLocked(now);
                state = new HostState { InFlight = 1 };
                _hosts[key] = state;
            }

            state.InFlight = Math.Max(0, state.InFlight - 1);
            state.LastActivity = now;
            state.Prune(now - Window);

            if (failed)
            {
                state.Failures.Enqueue(now);
                // Never needs more than Limit entries: the retry time depends only on the newest ones.
                while (state.Failures.Count > Limit)
                    state.Failures.Dequeue();
            }

            count = state.Failures.Count;
            if (count == 0 && state.InFlight == 0)
                _hosts.Remove(key);
        }

        if (!failed || _logger is null)
            return;

        // Host and count only: never the login, never the password.
        if (count >= Limit)
        {
            _logger.LogWarning(
                "Failed sign-ins to host {Host}: {Count} in {Minutes} min; refusing sign-ins to it until they age out.",
                key, count, (int)Window.TotalMinutes);
        }
        else
        {
            _logger.LogInformation("Failed sign-ins to host {Host}: {Count}/{Limit} in {Minutes} min.",
                key, count, Limit, (int)Window.TotalMinutes);
        }
    }

    private TimeSpan RetryAfterLocked(HostState state, DateTimeOffset now)
    {
        // Held partly by attempts still in flight: they finish within seconds, and a success frees
        // its slot. If they fail instead, the next try gets the real wait.
        if (state.Failures.Count < Limit)
            return InFlightRetry;

        // Enough of the oldest failures must age out to drop below Limit.
        var mustExpire = state.Failures.Count - Limit + 1;
        var freesAt = state.Failures.ElementAt(mustExpire - 1) + Window;
        var wait = freesAt - now;
        return wait < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : wait;
    }

    private int SweepLocked(DateTimeOffset now)
    {
        var cutoff = now - Window;
        var dropped = 0;
        foreach (var (key, state) in _hosts.ToArray())
        {
            state.Prune(cutoff);
            if (state.Failures.Count == 0 && state.InFlight == 0)
            {
                _hosts.Remove(key);
                dropped++;
            }
        }

        return dropped;
    }

    /// <summary>At <see cref="MaxHosts"/>: drop expired entries, then the least recently active idle one.</summary>
    private void MakeRoomLocked(DateTimeOffset now)
    {
        if (SweepLocked(now) > 0)
            return;

        string? oldest = null;
        var oldestAt = DateTimeOffset.MaxValue;
        foreach (var (key, state) in _hosts)
        {
            if (state.InFlight == 0 && state.LastActivity < oldestAt)
            {
                oldest = key;
                oldestAt = state.LastActivity;
            }
        }

        if (oldest is not null)
            _hosts.Remove(oldest);
    }

    private static int EnvInt(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value > 0
            ? value
            : fallback;

    private sealed class HostState
    {
        /// <summary>Failure times, oldest first.</summary>
        public Queue<DateTimeOffset> Failures { get; } = new();
        public int InFlight { get; set; }
        public DateTimeOffset LastActivity { get; set; }

        public void Prune(DateTimeOffset cutoff)
        {
            while (Failures.Count > 0 && Failures.Peek() <= cutoff)
                Failures.Dequeue();
        }
    }

    /// <summary>
    /// One reserved login attempt. <see cref="Complete"/> it with SmarterMail's answer; disposing
    /// releases the slot (and counts nothing if it was never completed, e.g. on cancellation).
    /// </summary>
    public sealed class Attempt : IDisposable
    {
        private readonly HostLoginThrottle _owner;
        private readonly string _key;
        private int _done;

        internal Attempt(HostLoginThrottle owner, string key)
        {
            _owner = owner;
            _key = key;
        }

        /// <summary>Records the answer (a failure only if <see cref="CountsAsFailure"/>) and releases the slot.</summary>
        public void Complete(AuthOutcome outcome, bool twoFactorStep = false) =>
            Finish(CountsAsFailure(outcome, twoFactorStep));

        /// <summary>Releases the slot, recording a failure if <paramref name="failed"/>.</summary>
        public void Finish(bool failed)
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
                _owner.Finish(_key, failed);
        }

        public void Dispose() => Finish(failed: false);
    }
}
