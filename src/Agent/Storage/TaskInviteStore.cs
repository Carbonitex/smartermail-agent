using System.Security.Cryptography;
using System.Text;
using SmarterMailAgent.Auth;
using SmarterMailAgent.Profiles;

namespace SmarterMailAgent.Storage;

/// <param name="ExpiresAt">Unix ms; null = never.</param>
public sealed record TaskInviteRow(
    string Id, string? Note, int MaxUses, int Uses, long CreatedAt, long? ExpiresAt, long? RevokedAt);

/// <param name="InviteId">The code it redeemed; null = granted by the operator.</param>
public sealed record TaskAccessRow(string ProfileId, long GrantedAt, string? InviteId, string? InviteNote, long LastSeenAt);

/// <summary>
/// Invite codes for scheduled tasks (<c>TASKS_ACCESS=invite</c>) and which profiles hold access.
/// A code is 80 random bits, shown once as <c>XXXX-XXXX-XXXX-XXXX</c> (Crockford base32); only its
/// SHA-256 is stored. Codes are made and revoked by the operator (<c>Server/AdminCli.cs</c>), redeemed by
/// a profile (<c>POST /api/profile/task-access</c>).
/// </summary>
public sealed class TaskInviteStore(DataStore db)
{
    public enum RedeemOutcome { Granted, AlreadyGranted, Invalid }

    /// <summary>What a revoked profile's tasks are paused with (also a hard run failure).</summary>
    public const string NotInvitedCode = "TASKS_NOT_INVITED";

    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private const int CodeChars = 16;

    // ------------------------------------------------------------------ codes

    /// <summary>A new code; the plain code is returned once and never stored.</summary>
    public (TaskInviteRow Invite, string Code) Create(string? note, int maxUses, TimeSpan? lifetime)
    {
        var code = NewCode();
        var now = DataStore.Now();
        var row = new TaskInviteRow(ProfileCrypto.NewId(6), note, maxUses, 0, now,
            lifetime is { } l ? now + (long)l.TotalMilliseconds : null, null);

        using var c = db.Open();
        using var cmd = c.Command(
            """
            INSERT INTO task_invites (id, code_hash, note, max_uses, uses, created_at, expires_at)
            VALUES ($id, $hash, $note, $max, 0, $created, $expires)
            """,
            ("$id", row.Id), ("$hash", Hash(Normalize(code)!)), ("$note", note), ("$max", maxUses),
            ("$created", now), ("$expires", row.ExpiresAt));
        cmd.ExecuteNonQuery();
        return (row, code);
    }

    public IReadOnlyList<TaskInviteRow> List()
    {
        using var c = db.Open();
        using var cmd = c.Command(
            "SELECT id, note, max_uses, uses, created_at, expires_at, revoked_at FROM task_invites ORDER BY created_at");
        using var r = cmd.ExecuteReader();
        var rows = new List<TaskInviteRow>();
        while (r.Read())
            rows.Add(new TaskInviteRow(r.GetString(0), r.StringOrNull(1), r.GetInt32(2), r.GetInt32(3), r.GetInt64(4),
                r.Int64OrNull(5), r.Int64OrNull(6)));
        return rows;
    }

    /// <summary>
    /// Stops a code from being redeemed again. With <paramref name="profilesToo"/>, every profile that
    /// redeemed it loses access (<see cref="RevokeAccess"/>). Returns false for an unknown id.
    /// </summary>
    public bool RevokeInvite(string id, bool profilesToo, out int profilesRevoked)
    {
        profilesRevoked = 0;
        using (var c = db.Open())
        using (var cmd = c.Command("UPDATE task_invites SET revoked_at = COALESCE(revoked_at, $now) WHERE id = $id",
                   ("$now", DataStore.Now()), ("$id", id)))
        {
            if (cmd.ExecuteNonQuery() != 1)
                return false;
        }

        if (profilesToo)
        {
            foreach (var access in AccessList().Where(a => a.InviteId == id))
                if (RevokeAccess(access.ProfileId))
                    profilesRevoked++;
        }
        return true;
    }

    // ------------------------------------------------------------------ access

    /// <summary>
    /// Redeems <paramref name="code"/> for a profile: one use of a live code (not revoked, not expired, uses
    /// left). A profile that already has access spends nothing. Unknown, used-up, expired and revoked codes
    /// are all <see cref="RedeemOutcome.Invalid"/>, so an answer says nothing about which.
    /// </summary>
    public RedeemOutcome Redeem(string profileId, string? code)
    {
        var normalized = Normalize(code);
        using var c = db.Open();
        using var tx = c.BeginTransaction(deferred: false);

        using (var check = c.Command("SELECT task_access_at FROM profiles WHERE id = $id", ("$id", profileId)))
        {
            check.Transaction = tx;
            var current = check.ExecuteScalar();
            if (current is null)
                return RedeemOutcome.Invalid;   // no such profile
            if (current is long)
                return RedeemOutcome.AlreadyGranted;
        }

        if (normalized is null)
            return RedeemOutcome.Invalid;

        var now = DataStore.Now();
        string? inviteId;
        using (var use = c.Command(
                   """
                   UPDATE task_invites SET uses = uses + 1
                   WHERE code_hash = $hash AND revoked_at IS NULL AND uses < max_uses AND (expires_at IS NULL OR expires_at > $now)
                   RETURNING id
                   """,
                   ("$hash", Hash(normalized)), ("$now", now)))
        {
            use.Transaction = tx;
            inviteId = use.ExecuteScalar() as string;
        }
        if (inviteId is null)
            return RedeemOutcome.Invalid;

        using (var grant = c.Command("UPDATE profiles SET task_access_at = $now, task_invite_id = $invite WHERE id = $id",
                   ("$now", now), ("$invite", inviteId), ("$id", profileId)))
        {
            grant.Transaction = tx;
            grant.ExecuteNonQuery();
        }
        tx.Commit();
        return RedeemOutcome.Granted;
    }

    /// <summary>The operator grants access without a code. False for an unknown profile.</summary>
    public bool Grant(string profileId)
    {
        using var c = db.Open();
        using var cmd = c.Command(
            "UPDATE profiles SET task_access_at = COALESCE(task_access_at, $now) WHERE id = $id",
            ("$now", DataStore.Now()), ("$id", profileId));
        return cmd.ExecuteNonQuery() == 1;
    }

    /// <summary>
    /// Takes a profile's access away, in the database only (the operator's CLI is another process): its
    /// tasks are paused with <see cref="NotInvitedCode"/>, its task key is deleted and its pending approvals
    /// are denied. Its delegated accounts stay sealed with <c>DATA_KEY</c> until the owner next unlocks the
    /// profile, which moves them back under the profile key; meanwhile the daily keep-alive skips them.
    /// False when the profile had no access.
    /// </summary>
    public bool RevokeAccess(string profileId)
    {
        var now = DataStore.Now();
        using var c = db.Open();
        using var tx = c.BeginTransaction(deferred: false);
        int changed;
        using (var cmd = c.Command(
                   """
                   UPDATE profiles SET task_access_at = NULL, task_invite_id = NULL, task_llm_key = NULL
                   WHERE id = $id AND task_access_at IS NOT NULL
                   """, ("$id", profileId)))
        {
            cmd.Transaction = tx;
            changed = cmd.ExecuteNonQuery();
        }
        if (changed == 0)
            return false;

        foreach (var sql in new[]
                 {
                     "UPDATE tasks SET enabled = 0, status = $code, updated_at = $now WHERE profile_id = $id",
                     """
                     UPDATE task_proposals SET status = 'denied', payload = NULL, decided_at = $now, read = 1
                     WHERE profile_id = $id AND status = 'pending'
                     """,
                 })
        {
            using var cmd = c.Command(sql, ("$code", NotInvitedCode), ("$now", now), ("$id", profileId));
            cmd.Transaction = tx;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return true;
    }

    /// <summary>Profiles holding access, oldest grant first.</summary>
    public IReadOnlyList<TaskAccessRow> AccessList()
    {
        using var c = db.Open();
        using var cmd = c.Command(
            """
            SELECT p.id, p.task_access_at, p.task_invite_id, i.note, p.last_seen_at
            FROM profiles p LEFT JOIN task_invites i ON i.id = p.task_invite_id
            WHERE p.task_access_at IS NOT NULL ORDER BY p.task_access_at
            """);
        using var r = cmd.ExecuteReader();
        var rows = new List<TaskAccessRow>();
        while (r.Read())
            rows.Add(new TaskAccessRow(r.GetString(0), r.GetInt64(1), r.StringOrNull(2), r.StringOrNull(3), r.GetInt64(4)));
        return rows;
    }

    // ------------------------------------------------------------------ the code itself

    internal static string NewCode()
    {
        Span<byte> bytes = stackalloc byte[CodeChars * 5 / 8];
        RandomNumberGenerator.Fill(bytes);
        var chars = new StringBuilder(CodeChars + 3);
        var buffer = 0;
        var bits = 0;
        foreach (var b in bytes)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                if (chars.Length is 4 or 9 or 14)
                    chars.Append('-');
                chars.Append(Alphabet[(buffer >> bits) & 31]);
            }
        }
        return chars.ToString();
    }

    /// <summary>
    /// The code as typed, reduced to its 16 characters: case, spaces and dashes ignored; O read as 0 and
    /// I / L as 1. Null when what is left is not a code.
    /// </summary>
    internal static string? Normalize(string? code)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > 64)
            return null;
        var chars = new StringBuilder(CodeChars);
        foreach (var ch in code.ToUpperInvariant())
        {
            if (ch is '-' or ' ' or '\t')
                continue;
            var c = ch switch { 'O' => '0', 'I' or 'L' => '1', _ => ch };
            if (!Alphabet.Contains(c))
                return null;
            chars.Append(c);
        }
        return chars.Length == CodeChars ? chars.ToString() : null;
    }

    private static string Hash(string normalized) =>
        Base64Url.Encode(SHA256.HashData(Encoding.UTF8.GetBytes("sma-task-invite-v1\n" + normalized)));
}
