namespace SmarterMailAgent.Storage;

public sealed record ProfileRow(
    string Id,
    long CreatedAt,
    long LastSeenAt,
    string PublicKey,
    string EncryptedPrivateKey,
    string? Settings,
    long SettingsVersion,
    string AccountsKeyCheck,
    string? RecoveryWrappedKey,
    string? RecoveryAuthHash,
    string? TaskLlmKey,
    bool TasksPaused);

public sealed record PasskeyRow(
    string CredentialId,
    string ProfileId,
    byte[] PublicKey,
    uint SignCount,
    string? Aaguid,
    string? Label,
    string WrappedKey,
    long CreatedAt,
    long? LastUsedAt);

/// <summary>
/// A stored account. <c>Seal</c>: <c>profile</c> = sealed with the profile's accounts key (usable only
/// while someone has unlocked the profile), <c>server</c> = sealed with <c>DATA_KEY</c> (delegated to
/// scheduled tasks). <c>State</c>: <c>ok</c>, or <c>rejected</c> once SmarterMail refused its refresh
/// token (the row stays so the UI can ask for a fresh sign-in, which reuses the row and its id).
/// </summary>
public sealed record StoredAccountRow(string Id, string ProfileId, string Seal, string State, string Blob, long UpdatedAt);

/// <summary>Profiles, their passkeys and their stored accounts. Raw ADO.NET; every value already sealed.</summary>
public sealed class ProfileStore(DataStore db)
{
    public const string SealProfile = "profile";
    public const string SealServer = "server";

    public int CountProfiles()
    {
        using var c = db.Open();
        using var cmd = c.Command("SELECT COUNT(*) FROM profiles");
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public void CreateProfile(ProfileRow profile, PasskeyRow passkey, IEnumerable<StoredAccountRow> accounts)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        using (var cmd = c.Command(
                   """
                   INSERT INTO profiles (id, created_at, last_seen_at, public_key, encrypted_private_key, settings,
                       settings_version, accounts_key_check, recovery_wrapped_key, recovery_auth_hash, task_llm_key, tasks_paused)
                   VALUES ($id, $created, $seen, $pub, $priv, $settings, $sv, $check, $rwk, $rah, $llm, 0)
                   """,
                   ("$id", profile.Id), ("$created", profile.CreatedAt), ("$seen", profile.LastSeenAt),
                   ("$pub", profile.PublicKey), ("$priv", profile.EncryptedPrivateKey), ("$settings", profile.Settings),
                   ("$sv", profile.SettingsVersion), ("$check", profile.AccountsKeyCheck),
                   ("$rwk", profile.RecoveryWrappedKey), ("$rah", profile.RecoveryAuthHash), ("$llm", profile.TaskLlmKey)))
        {
            cmd.Transaction = tx;
            cmd.ExecuteNonQuery();
        }

        InsertPasskey(c, tx, passkey);
        foreach (var account in accounts)
            UpsertAccount(c, tx, account);
        tx.Commit();
    }

    public ProfileRow? GetProfile(string id)
    {
        using var c = db.Open();
        using var cmd = c.Command(
            """
            SELECT id, created_at, last_seen_at, public_key, encrypted_private_key, settings, settings_version,
                   accounts_key_check, recovery_wrapped_key, recovery_auth_hash, task_llm_key, tasks_paused
            FROM profiles WHERE id = $id
            """, ("$id", id));
        using var r = cmd.ExecuteReader();
        if (!r.Read())
            return null;
        return new ProfileRow(r.GetString(0), r.GetInt64(1), r.GetInt64(2), r.GetString(3), r.GetString(4),
            r.StringOrNull(5), r.GetInt64(6), r.GetString(7), r.StringOrNull(8), r.StringOrNull(9), r.StringOrNull(10),
            r.GetInt64(11) != 0);
    }

    public void TouchProfile(string id)
    {
        using var c = db.Open();
        using var cmd = c.Command("UPDATE profiles SET last_seen_at = $now WHERE id = $id", ("$now", DataStore.Now()), ("$id", id));
        cmd.ExecuteNonQuery();
    }

    /// <summary>Optimistic write: false when <paramref name="expectedVersion"/> is not the stored one.</summary>
    public bool UpdateSettings(string id, string settings, long expectedVersion, out long newVersion)
    {
        newVersion = expectedVersion + 1;
        using var c = db.Open();
        using var cmd = c.Command(
            "UPDATE profiles SET settings = $s, settings_version = $nv WHERE id = $id AND settings_version = $ev",
            ("$s", settings), ("$nv", newVersion), ("$id", id), ("$ev", expectedVersion));
        return cmd.ExecuteNonQuery() == 1;
    }

    public void UpdateRecovery(string id, string? wrappedKey, string? authHash)
    {
        using var c = db.Open();
        using var cmd = c.Command(
            "UPDATE profiles SET recovery_wrapped_key = $w, recovery_auth_hash = $h WHERE id = $id",
            ("$w", wrappedKey), ("$h", authHash), ("$id", id));
        cmd.ExecuteNonQuery();
    }

    public void UpdateTaskLlmKey(string id, string? sealedKey)
    {
        using var c = db.Open();
        using var cmd = c.Command("UPDATE profiles SET task_llm_key = $k WHERE id = $id", ("$k", sealedKey), ("$id", id));
        cmd.ExecuteNonQuery();
    }

    public void SetTasksPaused(string id, bool paused)
    {
        using var c = db.Open();
        using var cmd = c.Command("UPDATE profiles SET tasks_paused = $p WHERE id = $id", ("$p", paused ? 1 : 0), ("$id", id));
        cmd.ExecuteNonQuery();
    }

    /// <summary>The profile's own session idle timeout in minutes; null = the server default.</summary>
    public int? IdleMinutes(string id)
    {
        using var c = db.Open();
        using var cmd = c.Command("SELECT idle_minutes FROM profiles WHERE id = $id", ("$id", id));
        return cmd.ExecuteScalar() is long minutes ? (int)minutes : null;
    }

    public void SetIdleMinutes(string id, int? minutes)
    {
        using var c = db.Open();
        using var cmd = c.Command("UPDATE profiles SET idle_minutes = $m WHERE id = $id", ("$m", minutes), ("$id", id));
        cmd.ExecuteNonQuery();
    }

    public void DeleteProfile(string id)
    {
        using var c = db.Open();
        using var cmd = c.Command("DELETE FROM profiles WHERE id = $id", ("$id", id));
        cmd.ExecuteNonQuery();
    }

    /// <summary>Profiles nobody has opened since <paramref name="before"/> (unix ms).</summary>
    public IReadOnlyList<string> IdleProfiles(long before)
    {
        using var c = db.Open();
        using var cmd = c.Command("SELECT id FROM profiles WHERE last_seen_at < $before", ("$before", before));
        using var r = cmd.ExecuteReader();
        var ids = new List<string>();
        while (r.Read())
            ids.Add(r.GetString(0));
        return ids;
    }

    /// <summary>Profiles with a delegated account not rewritten since <paramref name="before"/> (unix ms).</summary>
    public IReadOnlyList<string> ProfilesWithDelegatedAccounts(long before)
    {
        using var c = db.Open();
        using var cmd = c.Command(
            "SELECT DISTINCT profile_id FROM profile_accounts WHERE seal = 'server' AND state = 'ok' AND updated_at < $before",
            ("$before", before));
        using var r = cmd.ExecuteReader();
        var ids = new List<string>();
        while (r.Read())
            ids.Add(r.GetString(0));
        return ids;
    }

    // ---- passkeys ----

    public void AddPasskey(PasskeyRow passkey)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        InsertPasskey(c, tx, passkey);
        tx.Commit();
    }

    private static void InsertPasskey(Microsoft.Data.Sqlite.SqliteConnection c, Microsoft.Data.Sqlite.SqliteTransaction tx, PasskeyRow p)
    {
        using var cmd = c.Command(
            """
            INSERT INTO passkeys (credential_id, profile_id, public_key, sign_count, aaguid, label, wrapped_key, created_at, last_used_at)
            VALUES ($id, $profile, $pk, $count, $aaguid, $label, $wrapped, $created, NULL)
            """,
            ("$id", p.CredentialId), ("$profile", p.ProfileId), ("$pk", p.PublicKey), ("$count", (long)p.SignCount),
            ("$aaguid", p.Aaguid), ("$label", p.Label), ("$wrapped", p.WrappedKey), ("$created", p.CreatedAt));
        cmd.Transaction = tx;
        cmd.ExecuteNonQuery();
    }

    public PasskeyRow? GetPasskey(string credentialId)
    {
        using var c = db.Open();
        using var cmd = c.Command(Select("WHERE credential_id = $id"), ("$id", credentialId));
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadPasskey(r) : null;
    }

    public IReadOnlyList<PasskeyRow> Passkeys(string profileId)
    {
        using var c = db.Open();
        using var cmd = c.Command(Select("WHERE profile_id = $p ORDER BY created_at"), ("$p", profileId));
        using var r = cmd.ExecuteReader();
        var list = new List<PasskeyRow>();
        while (r.Read())
            list.Add(ReadPasskey(r));
        return list;
    }

    public void RecordPasskeyUse(string credentialId, uint signCount)
    {
        using var c = db.Open();
        using var cmd = c.Command(
            "UPDATE passkeys SET sign_count = $count, last_used_at = $now WHERE credential_id = $id",
            ("$count", (long)signCount), ("$now", DataStore.Now()), ("$id", credentialId));
        cmd.ExecuteNonQuery();
    }

    /// <summary>Deletes one passkey unless it is the profile's last; false when it was the last or unknown.</summary>
    public bool DeletePasskey(string profileId, string credentialId)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        using var count = c.Command("SELECT COUNT(*) FROM passkeys WHERE profile_id = $p", ("$p", profileId));
        count.Transaction = tx;
        if (Convert.ToInt32(count.ExecuteScalar()) <= 1)
            return false;
        using var cmd = c.Command("DELETE FROM passkeys WHERE profile_id = $p AND credential_id = $id",
            ("$p", profileId), ("$id", credentialId));
        cmd.Transaction = tx;
        var deleted = cmd.ExecuteNonQuery() == 1;
        tx.Commit();
        return deleted;
    }

    private static string Select(string where) =>
        $"SELECT credential_id, profile_id, public_key, sign_count, aaguid, label, wrapped_key, created_at, last_used_at FROM passkeys {where}";

    private static PasskeyRow ReadPasskey(Microsoft.Data.Sqlite.SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), (byte[])r.GetValue(2), (uint)r.GetInt64(3), r.StringOrNull(4), r.StringOrNull(5),
        r.GetString(6), r.GetInt64(7), r.Int64OrNull(8));

    // ---- stored accounts ----

    public IReadOnlyList<StoredAccountRow> Accounts(string profileId)
    {
        using var c = db.Open();
        using var cmd = c.Command(
            "SELECT id, profile_id, seal, state, blob, updated_at FROM profile_accounts WHERE profile_id = $p ORDER BY created_at",
            ("$p", profileId));
        using var r = cmd.ExecuteReader();
        var list = new List<StoredAccountRow>();
        while (r.Read())
            list.Add(new StoredAccountRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt64(5)));
        return list;
    }

    public void UpsertAccount(StoredAccountRow account)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        UpsertAccount(c, tx, account);
        tx.Commit();
    }

    private static void UpsertAccount(Microsoft.Data.Sqlite.SqliteConnection c, Microsoft.Data.Sqlite.SqliteTransaction tx, StoredAccountRow a)
    {
        var now = DataStore.Now();
        using var cmd = c.Command(
            """
            INSERT INTO profile_accounts (id, profile_id, seal, state, blob, created_at, updated_at)
            VALUES ($id, $p, $seal, $state, $blob, $now, $now)
            ON CONFLICT(id) DO UPDATE SET seal = excluded.seal, state = excluded.state, blob = excluded.blob, updated_at = excluded.updated_at
            WHERE profile_accounts.profile_id = excluded.profile_id
            """,
            ("$id", a.Id), ("$p", a.ProfileId), ("$seal", a.Seal), ("$state", a.State), ("$blob", a.Blob), ("$now", now));
        cmd.Transaction = tx;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Rewrites an existing row only (a late rotation after the row was deleted writes nothing).</summary>
    public bool UpdateAccountBlob(string profileId, string id, string seal, string blob)
    {
        using var c = db.Open();
        using var cmd = c.Command(
            "UPDATE profile_accounts SET seal = $seal, state = 'ok', blob = $blob, updated_at = $now WHERE id = $id AND profile_id = $p",
            ("$seal", seal), ("$blob", blob), ("$now", DataStore.Now()), ("$id", id), ("$p", profileId));
        return cmd.ExecuteNonQuery() == 1;
    }

    public void SetAccountState(string profileId, string id, string state)
    {
        using var c = db.Open();
        using var cmd = c.Command("UPDATE profile_accounts SET state = $s, updated_at = $now WHERE id = $id AND profile_id = $p",
            ("$s", state), ("$now", DataStore.Now()), ("$id", id), ("$p", profileId));
        cmd.ExecuteNonQuery();
    }

    public bool DeleteAccount(string profileId, string id)
    {
        using var c = db.Open();
        using var cmd = c.Command("DELETE FROM profile_accounts WHERE id = $id AND profile_id = $p", ("$id", id), ("$p", profileId));
        return cmd.ExecuteNonQuery() == 1;
    }
}
