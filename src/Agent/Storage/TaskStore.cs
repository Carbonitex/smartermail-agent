namespace SmarterMailAgent.Storage;

/// <summary>A scheduled task. <c>Definition</c> is sealed with <c>DATA_KEY</c>; the rest is scheduling state.</summary>
public sealed record TaskRow(
    string Id,
    string ProfileId,
    bool Enabled,
    string Definition,
    long? NextRunAt,
    string Status,
    int ConsecutiveFailures,
    long? LastRunAt,
    long CreatedAt,
    long UpdatedAt);

/// <summary>One run of a task. <c>Transcript</c> is sealed to the profile's public key.</summary>
public sealed record TaskRunRow(
    string Id,
    string TaskId,
    string ProfileId,
    long StartedAt,
    long? FinishedAt,
    string Status,
    bool DryRun,
    string Trigger,
    string? ErrorCode,
    int ToolCalls,
    int Writes,
    long? PromptTokens,
    long? CompletionTokens,
    string? Transcript,
    bool Read);

/// <summary>Scheduled tasks and their runs. Raw ADO.NET.</summary>
public sealed class TaskStore(DataStore db)
{
    private const string TaskColumns =
        "id, profile_id, enabled, definition, next_run_at, status, consecutive_failures, last_run_at, created_at, updated_at";

    private const string RunColumns =
        "id, task_id, profile_id, started_at, finished_at, status, dry_run, trigger, error_code, tool_calls, writes, " +
        "prompt_tokens, completion_tokens, transcript, read";

    public void Insert(TaskRow t)
    {
        using var c = db.Open();
        using var cmd = c.Command(
            $"INSERT INTO tasks ({TaskColumns}) VALUES ($id, $p, $en, $def, $next, $status, 0, NULL, $now, $now)",
            ("$id", t.Id), ("$p", t.ProfileId), ("$en", t.Enabled ? 1 : 0), ("$def", t.Definition),
            ("$next", t.NextRunAt), ("$status", t.Status), ("$now", DataStore.Now()));
        cmd.ExecuteNonQuery();
    }

    public bool Update(string profileId, string id, bool enabled, string definition, long? nextRunAt, string status)
    {
        using var c = db.Open();
        using var cmd = c.Command(
            """
            UPDATE tasks SET enabled = $en, definition = $def, next_run_at = $next, status = $status,
                consecutive_failures = CASE WHEN $status = 'ok' THEN 0 ELSE consecutive_failures END, updated_at = $now
            WHERE id = $id AND profile_id = $p
            """,
            ("$en", enabled ? 1 : 0), ("$def", definition), ("$next", nextRunAt), ("$status", status),
            ("$now", DataStore.Now()), ("$id", id), ("$p", profileId));
        return cmd.ExecuteNonQuery() == 1;
    }

    public TaskRow? Get(string profileId, string id)
    {
        using var c = db.Open();
        using var cmd = c.Command($"SELECT {TaskColumns} FROM tasks WHERE id = $id AND profile_id = $p", ("$id", id), ("$p", profileId));
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadTask(r) : null;
    }

    public IReadOnlyList<TaskRow> ForProfile(string profileId)
    {
        using var c = db.Open();
        using var cmd = c.Command($"SELECT {TaskColumns} FROM tasks WHERE profile_id = $p ORDER BY created_at", ("$p", profileId));
        return ReadTasks(cmd);
    }

    public int CountForProfile(string profileId)
    {
        using var c = db.Open();
        using var cmd = c.Command("SELECT COUNT(*) FROM tasks WHERE profile_id = $p", ("$p", profileId));
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>Enabled tasks due by <paramref name="now"/>, oldest first, skipping profiles that paused all their tasks.</summary>
    public IReadOnlyList<TaskRow> Due(long now, int limit)
    {
        using var c = db.Open();
        using var cmd = c.Command(
            $"""
             SELECT {string.Join(", ", TaskColumns.Split(", ").Select(col => "t." + col))}
             FROM tasks t JOIN profiles p ON p.id = t.profile_id
             WHERE t.enabled = 1 AND t.next_run_at IS NOT NULL AND t.next_run_at <= $now AND p.tasks_paused = 0
             ORDER BY t.next_run_at LIMIT $limit
             """,
            ("$now", now), ("$limit", limit));
        return ReadTasks(cmd);
    }

    /// <summary>Claims a due run: moves <c>next_run_at</c> on, so no second tick starts it. False if someone else did.</summary>
    public bool Claim(string id, long expectedNextRunAt, long? newNextRunAt)
    {
        using var c = db.Open();
        using var cmd = c.Command(
            "UPDATE tasks SET next_run_at = $new, last_run_at = $now WHERE id = $id AND next_run_at = $expected",
            ("$new", newNextRunAt), ("$now", DataStore.Now()), ("$id", id), ("$expected", expectedNextRunAt));
        return cmd.ExecuteNonQuery() == 1;
    }

    /// <summary>Failures no retry can fix: they pause the task at once.</summary>
    public static readonly IReadOnlySet<string> HardFailures = new HashSet<string>(StringComparer.Ordinal)
    {
        "NEEDS_SIGN_IN", "LLM_KEY_REJECTED", "NO_TASK_KEY", "NO_CREDITS", "ACCOUNT_REMOVED", "ACCOUNT_NOT_DELEGATED",
        "ACCOUNT_UNREADABLE", "DEFINITION_UNREADABLE", "PROFILE_GONE", TaskInviteStore.NotInvitedCode,
    };

    /// <summary>Records how a run went; three failures in a row, or one hard failure, pause the task (<c>status</c> says why).</summary>
    public void RecordOutcome(string id, bool ok, string status, int pauseAfter)
    {
        using var c = db.Open();
        using var cmd = c.Command(
            """
            UPDATE tasks SET
                consecutive_failures = CASE WHEN $ok = 1 THEN 0 ELSE consecutive_failures + 1 END,
                status = $status,
                enabled = CASE WHEN $ok = 0 AND (consecutive_failures + 1 >= $pause OR $hard = 1) THEN 0 ELSE enabled END,
                updated_at = $now
            WHERE id = $id
            """,
            ("$ok", ok ? 1 : 0), ("$status", status), ("$pause", pauseAfter),
            ("$hard", HardFailures.Contains(status) ? 1 : 0),
            ("$now", DataStore.Now()), ("$id", id));
        cmd.ExecuteNonQuery();
    }

    /// <summary>Pauses every task of a profile that uses an account (it was removed, or no longer delegated).</summary>
    public void PauseUsing(string profileId, IReadOnlySet<string> taskIds, string status)
    {
        if (taskIds.Count == 0)
            return;
        using var c = db.Open();
        foreach (var id in taskIds)
        {
            using var cmd = c.Command("UPDATE tasks SET enabled = 0, status = $s, updated_at = $now WHERE id = $id AND profile_id = $p",
                ("$s", status), ("$now", DataStore.Now()), ("$id", id), ("$p", profileId));
            cmd.ExecuteNonQuery();
        }
    }

    public bool Delete(string profileId, string id)
    {
        using var c = db.Open();
        using var cmd = c.Command("DELETE FROM tasks WHERE id = $id AND profile_id = $p", ("$id", id), ("$p", profileId));
        return cmd.ExecuteNonQuery() == 1;
    }

    // ---- runs ----

    public void StartRun(TaskRunRow run)
    {
        using var c = db.Open();
        using var cmd = c.Command(
            $"INSERT INTO task_runs ({RunColumns}) VALUES ($id, $t, $p, $start, NULL, 'running', $dry, $trigger, NULL, 0, 0, NULL, NULL, NULL, 0)",
            ("$id", run.Id), ("$t", run.TaskId), ("$p", run.ProfileId), ("$start", run.StartedAt),
            ("$dry", run.DryRun ? 1 : 0), ("$trigger", run.Trigger));
        cmd.ExecuteNonQuery();
    }

    public void FinishRun(string id, string status, string? errorCode, int toolCalls, int writes,
        long? promptTokens, long? completionTokens, string? transcript)
    {
        using var c = db.Open();
        using var cmd = c.Command(
            """
            UPDATE task_runs SET finished_at = $now, status = $status, error_code = $err, tool_calls = $calls, writes = $writes,
                prompt_tokens = $pt, completion_tokens = $ct, transcript = $tr
            WHERE id = $id
            """,
            ("$now", DataStore.Now()), ("$status", status), ("$err", errorCode), ("$calls", toolCalls), ("$writes", writes),
            ("$pt", promptTokens), ("$ct", completionTokens), ("$tr", transcript), ("$id", id));
        cmd.ExecuteNonQuery();
    }

    /// <summary>Runs left 'running' by a process that died mid-run.</summary>
    public int AbandonRunning()
    {
        using var c = db.Open();
        using var cmd = c.Command(
            "UPDATE task_runs SET status = 'failed', error_code = 'INTERRUPTED', finished_at = $now WHERE status = 'running'",
            ("$now", DataStore.Now()));
        return cmd.ExecuteNonQuery();
    }

    /// <summary>Newest first, without transcripts.</summary>
    public IReadOnlyList<TaskRunRow> Runs(string profileId, string? taskId, int limit)
    {
        using var c = db.Open();
        using var cmd = c.Command(
            $"SELECT {RunColumns.Replace("transcript", "NULL")} FROM task_runs WHERE profile_id = $p AND ($t IS NULL OR task_id = $t) " +
            "ORDER BY started_at DESC LIMIT $limit",
            ("$p", profileId), ("$t", taskId), ("$limit", limit));
        using var r = cmd.ExecuteReader();
        var list = new List<TaskRunRow>();
        while (r.Read())
            list.Add(ReadRun(r));
        return list;
    }

    /// <summary>
    /// The runs a new run's prompt looks back on: the latest finished, real (not test) run that ended
    /// <c>ok</c>, and the latest finished real run of any outcome. Test runs and the run named by
    /// <paramref name="excludeRunId"/> (the one being started) never count.
    /// </summary>
    public (TaskRunRow? LastOk, TaskRunRow? Latest) PreviousRuns(string taskId, string excludeRunId)
    {
        using var c = db.Open();
        TaskRunRow? One(string extra)
        {
            using var cmd = c.Command(
                $"SELECT {RunColumns.Replace("transcript", "NULL")} FROM task_runs WHERE task_id = $t AND id != $x AND dry_run = 0 " +
                $"AND status != 'running' {extra} ORDER BY started_at DESC LIMIT 1", ("$t", taskId), ("$x", excludeRunId));
            using var r = cmd.ExecuteReader();
            return r.Read() ? ReadRun(r) : null;
        }
        return (One("AND status = 'ok'"), One(""));
    }

    public TaskRunRow? Run(string profileId, string id)
    {
        using var c = db.Open();
        using var cmd = c.Command($"SELECT {RunColumns} FROM task_runs WHERE id = $id AND profile_id = $p", ("$id", id), ("$p", profileId));
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadRun(r) : null;
    }

    public void MarkRead(string profileId, string id)
    {
        using var c = db.Open();
        using var cmd = c.Command("UPDATE task_runs SET read = 1 WHERE id = $id AND profile_id = $p", ("$id", id), ("$p", profileId));
        cmd.ExecuteNonQuery();
    }

    public int UnreadCount(string profileId)
    {
        using var c = db.Open();
        using var cmd = c.Command("SELECT COUNT(*) FROM task_runs WHERE profile_id = $p AND read = 0 AND status != 'running'", ("$p", profileId));
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>Keeps the newest <paramref name="keep"/> runs of a task.</summary>
    public void Prune(string taskId, int keep)
    {
        using var c = db.Open();
        using var cmd = c.Command(
            """
            DELETE FROM task_runs WHERE task_id = $t AND id NOT IN (
                SELECT id FROM task_runs WHERE task_id = $t ORDER BY started_at DESC LIMIT $keep)
            """,
            ("$t", taskId), ("$keep", keep));
        cmd.ExecuteNonQuery();
    }

    private static IReadOnlyList<TaskRow> ReadTasks(Microsoft.Data.Sqlite.SqliteCommand cmd)
    {
        using var r = cmd.ExecuteReader();
        var list = new List<TaskRow>();
        while (r.Read())
            list.Add(ReadTask(r));
        return list;
    }

    private static TaskRow ReadTask(Microsoft.Data.Sqlite.SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetInt64(2) != 0, r.GetString(3), r.Int64OrNull(4), r.GetString(5),
        (int)r.GetInt64(6), r.Int64OrNull(7), r.GetInt64(8), r.GetInt64(9));

    private static TaskRunRow ReadRun(Microsoft.Data.Sqlite.SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt64(3), r.Int64OrNull(4), r.GetString(5), r.GetInt64(6) != 0,
        r.GetString(7), r.StringOrNull(8), (int)r.GetInt64(9), (int)r.GetInt64(10), r.Int64OrNull(11), r.Int64OrNull(12),
        r.StringOrNull(13), r.GetInt64(14) != 0);
}
