using Microsoft.Data.Sqlite;

namespace SmarterMailAgent.Storage;

/// <summary>
/// One proposed write from a task run (<c>task_proposals</c>). <c>Payload</c> is sealed with
/// <c>DATA_KEY</c> and is null once the proposal reached a terminal state; <c>Display</c> and
/// <c>Result</c> are sealed to the profile's public key. In clear: status, timestamps, whether a
/// passkey is needed, and the dedupe HMAC. Never the tool name or arguments.
/// </summary>
public sealed record ProposalRow(
    string Id,
    string ProfileId,
    string TaskId,
    string RunId,
    string Status,
    bool NeedsPasskey,
    string Dedupe,
    string? Payload,
    string Display,
    string? Result,
    string? ErrorCode,
    long CreatedAt,
    long ExpiresAt,
    long? DecidedAt,
    long? ExecutedAt,
    bool Read);

/// <summary>Proposals of task runs. Raw ADO.NET, like the other stores.</summary>
public sealed class ProposalStore(DataStore db)
{
    public const string Pending = "pending";
    public const string Executing = "executing";
    public const string Executed = "executed";
    public const string Failed = "failed";
    public const string Denied = "denied";
    public const string Expired = "expired";
    public const string Unknown = "unknown";

    private const string Columns =
        "id, profile_id, task_id, run_id, status, needs_passkey, dedupe, payload, display, result, error_code, " +
        "created_at, expires_at, decided_at, executed_at, read";

    /// <summary>What <see cref="Create"/> did.</summary>
    public enum CreateOutcome { Created, Deduped, QueueFull }

    /// <summary>
    /// Inserts a pending proposal, in one write transaction: when the same dedupe key is already
    /// pending (and unexpired) nothing is written and that row's id comes back; when the profile has
    /// <paramref name="maxPending"/> pending proposals, nothing is written either.
    /// </summary>
    public (CreateOutcome Outcome, string? Id) Create(ProposalRow row, int maxPending)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction(deferred: false);

        using (var existing = c.Command(
                   "SELECT id FROM task_proposals WHERE dedupe = $d AND status = 'pending' AND expires_at > $now LIMIT 1",
                   ("$d", row.Dedupe), ("$now", row.CreatedAt)))
        {
            existing.Transaction = tx;
            if (existing.ExecuteScalar() is string id)
            {
                tx.Commit();
                return (CreateOutcome.Deduped, id);
            }
        }

        using (var count = c.Command(
                   "SELECT COUNT(*) FROM task_proposals WHERE profile_id = $p AND status = 'pending' AND expires_at > $now",
                   ("$p", row.ProfileId), ("$now", row.CreatedAt)))
        {
            count.Transaction = tx;
            if (Convert.ToInt32(count.ExecuteScalar()) >= maxPending)
            {
                tx.Commit();
                return (CreateOutcome.QueueFull, null);
            }
        }

        using (var insert = c.Command(
                   $"""
                    INSERT INTO task_proposals ({Columns})
                    VALUES ($id, $p, $t, $r, 'pending', $np, $d, $payload, $display, NULL, NULL, $created, $expires, NULL, NULL, 0)
                    """,
                   ("$id", row.Id), ("$p", row.ProfileId), ("$t", row.TaskId), ("$r", row.RunId), ("$np", row.NeedsPasskey ? 1 : 0),
                   ("$d", row.Dedupe), ("$payload", row.Payload), ("$display", row.Display), ("$created", row.CreatedAt),
                   ("$expires", row.ExpiresAt)))
        {
            insert.Transaction = tx;
            insert.ExecuteNonQuery();
        }

        tx.Commit();
        return (CreateOutcome.Created, row.Id);
    }

    public ProposalRow? Get(string profileId, string id)
    {
        using var c = db.Open();
        using var cmd = c.Command($"SELECT {Columns} FROM task_proposals WHERE id = $id AND profile_id = $p", ("$id", id), ("$p", profileId));
        using var r = cmd.ExecuteReader();
        return r.Read() ? Read(r) : null;
    }

    /// <summary>
    /// Newest first. <paramref name="filter"/>: <c>pending</c> (still pending, expired or not),
    /// <c>decided</c> (everything else), or <c>all</c>.
    /// </summary>
    public IReadOnlyList<ProposalRow> List(string profileId, string filter, string? taskId, int limit)
    {
        var where = filter switch
        {
            "pending" => "AND status = 'pending'",
            "decided" => "AND status != 'pending'",
            _ => "",
        };
        using var c = db.Open();
        using var cmd = c.Command(
            $"SELECT {Columns.Replace("payload", "NULL")} FROM task_proposals WHERE profile_id = $p AND ($t IS NULL OR task_id = $t) {where} " +
            "ORDER BY created_at DESC, id LIMIT $limit",
            ("$p", profileId), ("$t", taskId), ("$limit", limit));
        using var r = cmd.ExecuteReader();
        var list = new List<ProposalRow>();
        while (r.Read())
            list.Add(Read(r));
        return list;
    }

    public int PendingCount(string profileId)
    {
        using var c = db.Open();
        using var cmd = c.Command(
            "SELECT COUNT(*) FROM task_proposals WHERE profile_id = $p AND status = 'pending' AND expires_at > $now",
            ("$p", profileId), ("$now", DataStore.Now()));
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>
    /// The one step that decides who executes: pending and unexpired → <c>executing</c>. Exactly one
    /// caller gets true for a proposal.
    /// </summary>
    public bool Claim(string profileId, string id, long now)
    {
        using var c = db.Open();
        using var cmd = c.Command(
            """
            UPDATE task_proposals SET status = 'executing', decided_at = $now
            WHERE id = $id AND profile_id = $p AND status = 'pending' AND expires_at > $now
            """,
            ("$now", now), ("$id", id), ("$p", profileId));
        return cmd.ExecuteNonQuery() == 1;
    }

    /// <summary>Back to pending after a claim that ran nothing (hash mismatch, mail server down).</summary>
    public bool Release(string profileId, string id)
    {
        using var c = db.Open();
        using var cmd = c.Command(
            "UPDATE task_proposals SET status = 'pending', decided_at = NULL WHERE id = $id AND profile_id = $p AND status = 'executing'",
            ("$id", id), ("$p", profileId));
        return cmd.ExecuteNonQuery() == 1;
    }

    /// <summary>The end of an execution: a terminal status, the sealed result, and the payload erased.</summary>
    public bool Finish(string profileId, string id, string status, string? errorCode, string? result, bool read)
    {
        using var c = db.Open();
        using var cmd = c.Command(
            """
            UPDATE task_proposals SET status = $s, error_code = $err, result = $res, payload = NULL, executed_at = $now,
                read = $read
            WHERE id = $id AND profile_id = $p AND status = 'executing'
            """,
            ("$s", status), ("$err", errorCode), ("$res", result), ("$now", DataStore.Now()), ("$read", read ? 1 : 0),
            ("$id", id), ("$p", profileId));
        return cmd.ExecuteNonQuery() == 1;
    }

    /// <summary>Pending → denied (payload erased). False when it was not pending.</summary>
    public bool Deny(string profileId, string id)
    {
        using var c = db.Open();
        using var cmd = c.Command(
            """
            UPDATE task_proposals SET status = 'denied', payload = NULL, decided_at = $now, read = 1
            WHERE id = $id AND profile_id = $p AND status = 'pending'
            """,
            ("$now", DataStore.Now()), ("$id", id), ("$p", profileId));
        return cmd.ExecuteNonQuery() == 1;
    }

    /// <summary>Denies every pending proposal of one run. Returns how many.</summary>
    public int DenyRun(string profileId, string runId)
    {
        using var c = db.Open();
        using var cmd = c.Command(
            """
            UPDATE task_proposals SET status = 'denied', payload = NULL, decided_at = $now, read = 1
            WHERE run_id = $r AND profile_id = $p AND status = 'pending'
            """,
            ("$now", DataStore.Now()), ("$r", runId), ("$p", profileId));
        return cmd.ExecuteNonQuery();
    }

    public void MarkRead(string profileId, string id)
    {
        using var c = db.Open();
        using var cmd = c.Command("UPDATE task_proposals SET read = 1 WHERE id = $id AND profile_id = $p", ("$id", id), ("$p", profileId));
        cmd.ExecuteNonQuery();
    }

    /// <summary>Pending proposals past their expiry → expired (payload erased). Returns how many.</summary>
    public int ExpireDue(long now)
    {
        using var c = db.Open();
        using var cmd = c.Command(
            "UPDATE task_proposals SET status = 'expired', payload = NULL, decided_at = $now WHERE status = 'pending' AND expires_at <= $now",
            ("$now", now));
        return cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// At startup: proposals left <c>executing</c> by a process that died mid-execution become
    /// <c>unknown</c> (the change may or may not have happened) and are never retried.
    /// </summary>
    public int AbandonExecuting()
    {
        using var c = db.Open();
        using var cmd = c.Command(
            "UPDATE task_proposals SET status = 'unknown', error_code = 'INTERRUPTED', payload = NULL, executed_at = $now WHERE status = 'executing'",
            ("$now", DataStore.Now()));
        return cmd.ExecuteNonQuery();
    }

    /// <summary>How many proposals a run made (or pointed at, when deduplicated).</summary>
    public void SetRunProposals(string runId, int count)
    {
        using var c = db.Open();
        using var cmd = c.Command("UPDATE task_runs SET proposals = $n WHERE id = $id", ("$n", count), ("$id", runId));
        cmd.ExecuteNonQuery();
    }

    public int RunProposals(string runId)
    {
        using var c = db.Open();
        using var cmd = c.Command("SELECT proposals FROM task_runs WHERE id = $id", ("$id", runId));
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    private static ProposalRow Read(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt64(5) != 0, r.GetString(6),
        r.StringOrNull(7), r.GetString(8), r.StringOrNull(9), r.StringOrNull(10), r.GetInt64(11), r.GetInt64(12),
        r.Int64OrNull(13), r.Int64OrNull(14), r.GetInt64(15) != 0);
}
