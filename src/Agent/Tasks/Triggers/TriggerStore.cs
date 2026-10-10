using SmarterMailAgent.Storage;

namespace SmarterMailAgent.Tasks.Triggers;

/// <summary>A condition task as the prober claims it: the task row plus its probe columns.</summary>
public sealed record ProbeRow(TaskRow Task, long NextProbeAt, string? State, int ProbeFailures);

/// <summary>What the task card shows. No evidence, no seen keys.</summary>
public sealed record TriggerStatus(
    DateTimeOffset? NextProbeAt, DateTimeOffset? LastProbeAt, bool? LastValue, int ProbeFailures, int FiresToday);

/// <summary>The probe columns of <c>tasks</c> (migration 4). Raw ADO.NET, next to <see cref="TaskStore"/>.</summary>
public sealed class TriggerStore(DataStore db)
{
    public static readonly TimeSpan Day = TimeSpan.FromHours(24);

    private const string TaskColumns =
        "t.id, t.profile_id, t.enabled, t.definition, t.next_run_at, t.status, t.consecutive_failures, t.last_run_at, t.created_at, t.updated_at";

    /// <summary>
    /// After a create or an edit: a condition task gets its first probe at <paramref name="nextProbeAt"/>
    /// (a cron task NULL), and the state is cleared, so the next probe only takes a baseline.
    /// </summary>
    public void Reset(string profileId, string id, long? nextProbeAt)
    {
        using var c = db.Open();
        using var cmd = c.Command(
            """
            UPDATE tasks SET next_probe_at = $next, trigger_state = NULL, last_value = NULL, probe_failures = 0
            WHERE id = $id AND profile_id = $p
            """,
            ("$next", nextProbeAt), ("$id", id), ("$p", profileId));
        cmd.ExecuteNonQuery();
    }

    /// <summary>Condition tasks of a profile, <paramref name="exceptId"/> left out (the one being edited).</summary>
    public int CountForProfile(string profileId, string? exceptId = null)
    {
        using var c = db.Open();
        using var cmd = c.Command(
            "SELECT COUNT(*) FROM tasks WHERE profile_id = $p AND next_probe_at IS NOT NULL AND ($x IS NULL OR id != $x)",
            ("$p", profileId), ("$x", exceptId));
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>Enabled condition tasks due by <paramref name="now"/>, oldest first, skipping profiles that paused all their tasks.</summary>
    public IReadOnlyList<ProbeRow> Due(long now, int limit)
    {
        using var c = db.Open();
        using var cmd = c.Command(
            $"""
             SELECT {TaskColumns}, t.next_probe_at, t.trigger_state, t.probe_failures
             FROM tasks t JOIN profiles p ON p.id = t.profile_id
             WHERE t.enabled = 1 AND t.next_probe_at IS NOT NULL AND t.next_probe_at <= $now AND p.tasks_paused = 0
             ORDER BY t.next_probe_at LIMIT $limit
             """,
            ("$now", now), ("$limit", limit));
        using var r = cmd.ExecuteReader();
        var list = new List<ProbeRow>();
        while (r.Read())
        {
            var task = new TaskRow(r.GetString(0), r.GetString(1), r.GetInt64(2) != 0, r.GetString(3), r.Int64OrNull(4),
                r.GetString(5), (int)r.GetInt64(6), r.Int64OrNull(7), r.GetInt64(8), r.GetInt64(9));
            list.Add(new ProbeRow(task, r.GetInt64(10), r.StringOrNull(11), (int)r.GetInt64(12)));
        }
        return list;
    }

    /// <summary>One condition task's probe row (for "Run now").</summary>
    public ProbeRow? Get(string profileId, string id)
    {
        using var c = db.Open();
        using var cmd = c.Command(
            $"""
             SELECT {TaskColumns}, t.next_probe_at, t.trigger_state, t.probe_failures
             FROM tasks t WHERE t.id = $id AND t.profile_id = $p AND t.next_probe_at IS NOT NULL
             """,
            ("$id", id), ("$p", profileId));
        using var r = cmd.ExecuteReader();
        if (!r.Read())
            return null;
        var task = new TaskRow(r.GetString(0), r.GetString(1), r.GetInt64(2) != 0, r.GetString(3), r.Int64OrNull(4),
            r.GetString(5), (int)r.GetInt64(6), r.Int64OrNull(7), r.GetInt64(8), r.GetInt64(9));
        return new ProbeRow(task, r.GetInt64(10), r.StringOrNull(11), (int)r.GetInt64(12));
    }

    /// <summary>(task id, profile id) of every enabled condition task in a profile that has not paused its tasks.</summary>
    public IReadOnlyList<(string TaskId, string ProfileId)> Enabled()
    {
        using var c = db.Open();
        using var cmd = c.Command(
            """
            SELECT t.id, t.profile_id FROM tasks t JOIN profiles p ON p.id = t.profile_id
            WHERE t.enabled = 1 AND t.next_probe_at IS NOT NULL AND p.tasks_paused = 0
            """);
        using var r = cmd.ExecuteReader();
        var list = new List<(string, string)>();
        while (r.Read())
            list.Add((r.GetString(0), r.GetString(1)));
        return list;
    }

    /// <summary>Claims a due probe by moving <c>next_probe_at</c> on; false if another pass already did.</summary>
    public bool Claim(string id, long expected, long next)
    {
        using var c = db.Open();
        using var cmd = c.Command(
            "UPDATE tasks SET next_probe_at = $new WHERE id = $id AND next_probe_at = $expected",
            ("$new", next), ("$id", id), ("$expected", expected));
        return cmd.ExecuteNonQuery() == 1;
    }

    /// <summary>Moves the next probe (the host bucket was empty: retry at the next tick).</summary>
    public void Defer(string id, long next)
    {
        using var c = db.Open();
        using var cmd = c.Command("UPDATE tasks SET next_probe_at = $n WHERE id = $id AND next_probe_at IS NOT NULL",
            ("$n", next), ("$id", id));
        cmd.ExecuteNonQuery();
    }

    /// <summary>A successful probe: its value, the new state, and the failure count back to zero.</summary>
    public void RecordProbe(string id, bool value, string sealedState, long now)
    {
        using var c = db.Open();
        using var cmd = c.Command(
            """
            UPDATE tasks SET last_probe_at = $now, last_value = $v, probe_failures = 0, trigger_state = $s
            WHERE id = $id AND next_probe_at IS NOT NULL
            """,
            ("$now", now), ("$v", value ? 1 : 0), ("$s", sealedState), ("$id", id));
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// A failed probe. <paramref name="hard"/> pauses at once, otherwise <paramref name="pauseAfter"/> in
    /// a row do; the task's <c>status</c> then says why. Returns whether the task is now paused.
    /// </summary>
    public bool RecordFailure(string id, string code, bool hard, int pauseAfter, long now)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        using (var cmd = c.Command(
                   """
                   UPDATE tasks SET last_probe_at = $now, probe_failures = probe_failures + 1,
                       enabled = CASE WHEN $hard = 1 OR probe_failures + 1 >= $pause THEN 0 ELSE enabled END,
                       status = CASE WHEN $hard = 1 OR probe_failures + 1 >= $pause THEN $code ELSE status END,
                       updated_at = CASE WHEN $hard = 1 OR probe_failures + 1 >= $pause THEN $now ELSE updated_at END
                   WHERE id = $id
                   """,
                   ("$now", now), ("$hard", hard ? 1 : 0), ("$pause", pauseAfter), ("$code", code), ("$id", id)))
        {
            cmd.Transaction = tx;
            cmd.ExecuteNonQuery();
        }

        bool paused;
        using (var read = c.Command("SELECT enabled FROM tasks WHERE id = $id", ("$id", id)))
        {
            read.Transaction = tx;
            paused = read.ExecuteScalar() is long enabled && enabled == 0;
        }
        tx.Commit();
        return paused;
    }

    /// <summary>Real (not test) runs a condition started in the last 24 hours: the daily cap.</summary>
    public int FiresSince(string taskId, long since)
    {
        using var c = db.Open();
        using var cmd = c.Command(
            "SELECT COUNT(*) FROM task_runs WHERE task_id = $t AND trigger = 'condition' AND dry_run = 0 AND started_at > $since",
            ("$t", taskId), ("$since", since));
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>The card status of every condition task of a profile, by task id.</summary>
    public IReadOnlyDictionary<string, TriggerStatus> Status(string profileId, long now)
    {
        using var c = db.Open();
        using var cmd = c.Command(
            """
            SELECT t.id, t.next_probe_at, t.last_probe_at, t.last_value, t.probe_failures,
                (SELECT COUNT(*) FROM task_runs r WHERE r.task_id = t.id AND r.trigger = 'condition' AND r.dry_run = 0 AND r.started_at > $since)
            FROM tasks t WHERE t.profile_id = $p AND t.next_probe_at IS NOT NULL
            """,
            ("$p", profileId), ("$since", now - (long)Day.TotalMilliseconds));
        using var r = cmd.ExecuteReader();
        var map = new Dictionary<string, TriggerStatus>(StringComparer.Ordinal);
        while (r.Read())
        {
            map[r.GetString(0)] = new TriggerStatus(
                Time(r.Int64OrNull(1)), Time(r.Int64OrNull(2)), r.Int64OrNull(3) is { } v ? v != 0 : null,
                (int)r.GetInt64(4), (int)r.GetInt64(5));
        }
        return map;
    }

    private static DateTimeOffset? Time(long? ms) => ms is { } v ? DateTimeOffset.FromUnixTimeMilliseconds(v) : null;
}
