using SmarterMailAgent.Auth;

namespace SmarterMailAgent.Tasks.Triggers;

/// <summary>
/// A token bucket per mail server (<c>scheme://host:port</c>, keyed like <see cref="HostLoginThrottle"/>),
/// shared by every profile: <c>PROBES_PER_HOST_PER_MINUTE</c> probe calls a minute, refilled
/// continuously. A probe that finds no token waits for the next tick; it is not a failure. In memory,
/// one lock, at most <see cref="MaxHosts"/> servers (the least recently used go first).
/// </summary>
public sealed class ProbeHostBucket(TriggerOptions options, TimeProvider? clock = null)
{
    public const int MaxHosts = 10_000;

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Dictionary<string, (double Tokens, DateTimeOffset At)> _buckets = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    private double Capacity => Math.Max(1, options.ProbesPerHostPerMinute);

    /// <summary>Takes one token for <paramref name="baseUrl"/>; false when that server's bucket is empty.</summary>
    public bool TryTake(string baseUrl)
    {
        var key = HostLoginThrottle.NormalizeHost(baseUrl);
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

            if (!_buckets.ContainsKey(key) && _buckets.Count >= MaxHosts)
            {
                var oldest = _buckets.MinBy(kv => kv.Value.At).Key;
                _buckets.Remove(oldest);
            }
            _buckets[key] = (tokens - 1, now);
            return true;
        }
    }
}
