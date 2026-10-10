using SmarterMailAgent.Auth;

namespace SmarterMailAgent.Tasks.Triggers;

/// <summary>
/// A token bucket per key, refilled continuously at <c>perMinute</c> tokens a minute up to that many.
/// In memory, one lock, at most <see cref="MaxKeys"/> keys (the least recently used go first).
/// </summary>
public class KeyedTokenBucket(double perMinute, TimeProvider? clock = null)
{
    public const int MaxKeys = 10_000;

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Dictionary<string, (double Tokens, DateTimeOffset At)> _buckets = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    private double Capacity => Math.Max(1, perMinute);

    /// <summary>Takes one token for <paramref name="key"/>; false when that key's bucket is empty.</summary>
    public bool TryTakeKey(string key)
    {
        var now = _clock.GetUtcNow();
        lock (_lock)
        {
            var (tokens, at) = _buckets.TryGetValue(key, out var b) ? b : (Capacity, now);
            tokens = Math.Min(Capacity, tokens + (now - at).TotalMinutes * Capacity);
            if (tokens < 1)
            {
                _buckets[key] = (tokens, now);
                return false;
            }

            if (!_buckets.ContainsKey(key) && _buckets.Count >= MaxKeys)
            {
                var oldest = _buckets.MinBy(kv => kv.Value.At).Key;
                _buckets.Remove(oldest);
            }
            _buckets[key] = (tokens - 1, now);
            return true;
        }
    }

    /// <summary>Gives back a token taken by <see cref="TryTakeKey"/> for a call that was then not made.</summary>
    public void ReturnKey(string key)
    {
        lock (_lock)
        {
            if (_buckets.TryGetValue(key, out var b))
                _buckets[key] = (Math.Min(Capacity, b.Tokens + 1), b.At);
        }
    }
}

/// <summary>
/// The scheduled probes' bucket per mail server (<c>scheme://host:port</c>, keyed like
/// <see cref="HostLoginThrottle"/>), shared by every profile: <c>PROBES_PER_HOST_PER_MINUTE</c> probe
/// calls a minute. A probe that finds no token waits for the next tick; it is not a failure.
/// Interactive probes never draw from it (<see cref="InteractiveProbeLimiter"/>).
/// </summary>
public sealed class ProbeHostBucket(TriggerOptions options, TimeProvider? clock = null)
    : KeyedTokenBucket(options.ProbesPerHostPerMinute, clock)
{
    /// <summary>Takes one token for <paramref name="baseUrl"/>'s server; false when its bucket is empty.</summary>
    public bool TryTake(string baseUrl) => TryTakeKey(HostLoginThrottle.NormalizeHost(baseUrl));
}

/// <summary>
/// Probes a person starts (the editor's "Test probe", "Run now" / "Test run" on a condition task).
/// They have their own per-server bucket, <see cref="TriggerOptions.InteractivePerHostPerMinute"/>
/// (a fifth of <c>PROBES_PER_HOST_PER_MINUTE</c>, at least 1), so they can never starve scheduled
/// probes, and a per-profile cap, <see cref="TriggerOptions.InteractivePerProfilePerMinute"/>, so one
/// profile cannot spend a server's interactive share alone.
/// </summary>
public sealed class InteractiveProbeLimiter(TriggerOptions options, TimeProvider? clock = null)
{
    private readonly KeyedTokenBucket _hosts = new(options.InteractivePerHostPerMinute, clock);
    private readonly KeyedTokenBucket _profiles = new(options.InteractivePerProfilePerMinute, clock);

    public const string ProfileMessage = "Too many checks from this profile this minute. Try again shortly.";
    public const string HostMessage = "That mail server has had too many checks this minute. Try again shortly.";

    /// <summary>Null when the probe may go ahead; otherwise the user-facing reason (code <c>PROBE_THROTTLED</c>).</summary>
    public string? TryTake(string profileId, string? baseUrl)
    {
        if (!_profiles.TryTakeKey(profileId))
            return ProfileMessage;
        if (baseUrl is null || _hosts.TryTakeKey(HostLoginThrottle.NormalizeHost(baseUrl)))
            return null;
        _profiles.ReturnKey(profileId);
        return HostMessage;
    }
}
