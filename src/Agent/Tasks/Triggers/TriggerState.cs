using System.Security.Cryptography;
using System.Text.Json;
using SmarterMailAgent.Auth;
using SmarterMailAgent.Profiles;

namespace SmarterMailAgent.Tasks.Triggers;

/// <summary>
/// What a trigger remembers between probes, in <c>tasks.trigger_state</c>, sealed with <c>DATA_KEY</c>
/// (label <see cref="Label"/>, context <c>profileId|taskId</c>). Saving the task resets it, so the
/// first probe after a create or an edit only takes a baseline.
/// </summary>
/// <param name="Seen">Hashes of item keys a <c>new</c> node has looked at, oldest first, at most <see cref="MaxSeen"/>.</param>
/// <param name="ConsecutiveTrue">True probes in a row.</param>
/// <param name="LastValue">The last probe's value.</param>
/// <param name="Latched">Edge: fired (or skipped) in the current true streak; re-arms on a false probe.</param>
/// <param name="LastFiredAt">Unix ms of the last accepted fire (cooldown).</param>
/// <param name="SkippedFires">Fires skipped by the daily cap since the last accepted one; the next run is told.</param>
public sealed record TriggerState(
    int V, bool Baselined, IReadOnlyList<string> Seen, int ConsecutiveTrue, bool LastValue, bool Latched,
    long? LastFiredAt, int SkippedFires)
{
    public const string Label = "sma-task-trigger-state-v1";
    public const int MaxSeen = 500;

    public static readonly TriggerState Initial = new(1, false, [], 0, false, false, null, 0);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string Seal(Sealer sealer, string profileId, string taskId)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(this, Json);
        try
        {
            return sealer.SealString(plaintext, Label, ProfileCrypto.Context(profileId, taskId));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <summary>Null when there is nothing stored; <paramref name="unreadable"/> when something is stored that does not open.</summary>
    public static TriggerState? Open(Sealer sealer, string profileId, string taskId, string? sealedValue, out bool unreadable)
    {
        unreadable = false;
        if (sealedValue is null)
            return null;
        var plaintext = sealer.OpenString(sealedValue, Label, ProfileCrypto.Context(profileId, taskId));
        if (plaintext is null)
        {
            unreadable = true;
            return null;
        }
        try
        {
            var state = JsonSerializer.Deserialize<TriggerState>(plaintext, Json);
            unreadable = state is null;
            return state is null ? null : state with { Seen = state.Seen ?? [] };
        }
        catch (JsonException)
        {
            unreadable = true;
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <summary>
    /// <paramref name="keys"/> moved to the newest end (an item still in the result is never the
    /// oldest), then trimmed to <see cref="MaxSeen"/> from the oldest end.
    /// </summary>
    public IReadOnlyList<string> Touch(IEnumerable<string> keys)
    {
        var touched = keys.Distinct(StringComparer.Ordinal).ToList();
        if (touched.Count == 0)
            return Seen;
        var drop = touched.ToHashSet(StringComparer.Ordinal);
        var list = Seen.Where(k => !drop.Contains(k)).Concat(touched).ToList();
        return list.Count > MaxSeen ? list[^MaxSeen..] : list;
    }
}

/// <summary>Edge / level / hold-for / cooldown / new-item semantics, as a pure function of state and one evaluation.</summary>
public static class TriggerLogic
{
    /// <summary>
    /// What a probe means. When <see cref="WantsFire"/> is false, <see cref="State"/> is the next state
    /// and <see cref="Reason"/> says why (<c>baseline</c>, <c>false</c>, <c>hold</c>, <c>latched</c>,
    /// <c>cooldown</c>). When it is true the caller picks one of the three next states:
    /// <see cref="IfFired"/> (run started or alert sent / attempted), <see cref="IfSkipped"/> (daily cap:
    /// the state still advances) or <see cref="IfDeferred"/> (the task was busy: nothing advances, so the
    /// next probe sees the same items again).
    /// </summary>
    public sealed record Step(bool WantsFire, string? Reason, TriggerState State, TriggerState IfFired, TriggerState IfSkipped, TriggerState IfDeferred);

    public static Step Decide(TaskTrigger trigger, bool usesNew, TriggerState state, Predicate.Evaluation evaluation, DateTimeOffset now)
    {
        var value = evaluation.Value;
        var consecutive = value ? Math.Min(state.ConsecutiveTrue + 1, 1000) : 0;
        var all = state.Touch(evaluation.Keys);
        var withoutNew = state.Touch(evaluation.Keys.Where(k => !evaluation.NewKeys.Contains(k)));
        var basic = state with
        {
            Baselined = true,
            ConsecutiveTrue = consecutive,
            LastValue = value,
            Latched = value && state.Latched,
        };

        Step Hold(string reason, TriggerState next) => new(false, reason, next, next, next, next);

        if (!state.Baselined)
        {
            // New since the trigger was set up: record what is there now; an edge condition that is
            // already true waits for it to go false first.
            return Hold("baseline", basic with { Seen = all, Latched = value });
        }

        string? notYet = null;
        if (!value)
            notYet = "false";
        else if (!usesNew && consecutive < trigger.HoldFor)
            notYet = "hold";
        else if (!usesNew && trigger.Fire == "edge" && state.Latched)
            notYet = "latched";
        if (notYet is not null)
            return Hold(notYet, basic with { Seen = all });

        if (state.LastFiredAt is { } last && now.ToUnixTimeMilliseconds() - last < trigger.CooldownMinutes * 60_000L)
            return Hold("cooldown", basic with { Seen = withoutNew });

        return new Step(true, null, basic,
            IfFired: basic with { Seen = all, Latched = true, LastFiredAt = now.ToUnixTimeMilliseconds(), SkippedFires = 0 },
            IfSkipped: basic with { Seen = all, Latched = true, SkippedFires = Math.Min(state.SkippedFires + 1, 100_000) },
            IfDeferred: basic with { Seen = withoutNew });
    }
}
