using Microsoft.Data.Sqlite;
using SmarterMailAgent.Server;

namespace SmarterMailAgent.Storage;

/// <summary>
/// The server-mode database: one SQLite file under <c>DATA_DIR</c>. Only registered in server mode;
/// a browser-only agent never constructs it, so nothing is created on disk.
/// <para>
/// What is in it is encrypted before it gets here (see <c>Profiles/ProfileCrypto.cs</c>): blobs the
/// browser encrypted with keys the server never sees, blobs sealed with the profile's accounts key
/// (held in memory only while the profile is unlocked), and blobs sealed with <c>DATA_KEY</c>. In
/// clear: ids, timestamps, passkey public keys and counters, and task scheduling state.
/// </para>
/// Migrations are ordered and keyed on <c>PRAGMA user_version</c>; append, never edit one that shipped.
/// </summary>
public sealed class DataStore
{
    private readonly string _connectionString;

    private static readonly string[] Migrations =
    [
        // 1: profiles, passkeys, stored accounts, scheduled tasks and their runs.
        """
        CREATE TABLE profiles (
            id                    TEXT PRIMARY KEY,
            created_at            INTEGER NOT NULL,
            last_seen_at          INTEGER NOT NULL,
            public_key            TEXT NOT NULL,
            encrypted_private_key TEXT NOT NULL,
            settings              TEXT,
            settings_version      INTEGER NOT NULL DEFAULT 0,
            accounts_key_check    TEXT NOT NULL,
            recovery_wrapped_key  TEXT,
            recovery_auth_hash    TEXT,
            task_llm_key          TEXT,
            tasks_paused          INTEGER NOT NULL DEFAULT 0
        );

        CREATE TABLE passkeys (
            credential_id TEXT PRIMARY KEY,
            profile_id    TEXT NOT NULL REFERENCES profiles(id) ON DELETE CASCADE,
            public_key    BLOB NOT NULL,
            sign_count    INTEGER NOT NULL,
            aaguid        TEXT,
            label         TEXT,
            wrapped_key   TEXT NOT NULL,
            created_at    INTEGER NOT NULL,
            last_used_at  INTEGER
        );
        CREATE INDEX passkeys_profile ON passkeys(profile_id);

        CREATE TABLE profile_accounts (
            id         TEXT PRIMARY KEY,
            profile_id TEXT NOT NULL REFERENCES profiles(id) ON DELETE CASCADE,
            seal       TEXT NOT NULL CHECK (seal IN ('profile', 'server')),
            state      TEXT NOT NULL DEFAULT 'ok',
            blob       TEXT NOT NULL,
            created_at INTEGER NOT NULL,
            updated_at INTEGER NOT NULL
        );
        CREATE INDEX profile_accounts_profile ON profile_accounts(profile_id);

        CREATE TABLE tasks (
            id                   TEXT PRIMARY KEY,
            profile_id           TEXT NOT NULL REFERENCES profiles(id) ON DELETE CASCADE,
            enabled              INTEGER NOT NULL,
            definition           TEXT NOT NULL,
            next_run_at          INTEGER,
            status               TEXT NOT NULL DEFAULT 'ok',
            consecutive_failures INTEGER NOT NULL DEFAULT 0,
            last_run_at          INTEGER,
            created_at           INTEGER NOT NULL,
            updated_at           INTEGER NOT NULL
        );
        CREATE INDEX tasks_due ON tasks(enabled, next_run_at);
        CREATE INDEX tasks_profile ON tasks(profile_id);

        CREATE TABLE task_runs (
            id                TEXT PRIMARY KEY,
            task_id           TEXT NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
            profile_id        TEXT NOT NULL REFERENCES profiles(id) ON DELETE CASCADE,
            started_at        INTEGER NOT NULL,
            finished_at       INTEGER,
            status            TEXT NOT NULL,
            dry_run           INTEGER NOT NULL,
            trigger           TEXT NOT NULL,
            error_code        TEXT,
            tool_calls        INTEGER NOT NULL DEFAULT 0,
            writes            INTEGER NOT NULL DEFAULT 0,
            prompt_tokens     INTEGER,
            completion_tokens INTEGER,
            transcript        TEXT,
            read              INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX task_runs_task ON task_runs(task_id, started_at);
        """,

        // 2: a profile's own session idle timeout (minutes); NULL = the server's SESSION_IDLE_MINUTES.
        "ALTER TABLE profiles ADD COLUMN idle_minutes INTEGER;",

        // 3: the approval queue. A task run proposes a write; a person approves it (executed once) or
        // denies it. payload is DATA_KEY-sealed and erased at every terminal state; display and result
        // are sealed to the profile's public key; the tool name is never in clear.
        """
        CREATE TABLE task_proposals (
            id            TEXT PRIMARY KEY,
            profile_id    TEXT NOT NULL REFERENCES profiles(id) ON DELETE CASCADE,
            task_id       TEXT NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
            run_id        TEXT NOT NULL,
            status        TEXT NOT NULL,
            needs_passkey INTEGER NOT NULL,
            dedupe        TEXT NOT NULL,
            payload       TEXT,
            display       TEXT NOT NULL,
            result        TEXT,
            error_code    TEXT,
            created_at    INTEGER NOT NULL,
            expires_at    INTEGER NOT NULL,
            decided_at    INTEGER,
            executed_at   INTEGER,
            read          INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX task_proposals_profile ON task_proposals(profile_id, status, created_at);
        CREATE INDEX task_proposals_dedupe ON task_proposals(dedupe, status);
        CREATE INDEX task_proposals_expiry ON task_proposals(status, expires_at);
        ALTER TABLE task_runs ADD COLUMN proposals INTEGER NOT NULL DEFAULT 0;
        """,
    ];

    public DataStore(ServerOptions options, ILogger<DataStore> logger)
    {
        var directory = Path.GetDirectoryName(options.DatabasePath)!;
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, $".write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Server mode needs a writable DATA_DIR ({directory}). Mount a volume there, or set " +
                "BROWSER_ONLY_MODE=true to store nothing on the server.", ex);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = options.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
        }.ToString();

        using var connection = Open();
        Exec(connection, "PRAGMA journal_mode = WAL;");

        // One migration per write transaction, re-reading the version inside it, so two processes
        // opening a new file at once (tests do) never run the same migration twice.
        while (true)
        {
            using var tx = connection.BeginTransaction(deferred: false);
            var version = Convert.ToInt32(Scalar(connection, "PRAGMA user_version;", tx));
            if (version >= Migrations.Length)
            {
                tx.Commit();
                break;
            }

            Exec(connection, Migrations[version], tx);
            Exec(connection, $"PRAGMA user_version = {version + 1};", tx);
            tx.Commit();
            logger.LogInformation("Database migrated to version {Version}.", version + 1);
        }

        TryRestrictPermissions(options.DatabasePath);
    }

    public int SchemaVersion => Migrations.Length;

    /// <summary>A pooled connection with foreign keys on and a busy timeout; dispose it after use.</summary>
    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        Exec(connection, "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;");
        return connection;
    }

    public static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public static void Exec(SqliteConnection connection, string sql, SqliteTransaction? tx = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = tx;
        command.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection connection, string sql, SqliteTransaction? tx = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = tx;
        return command.ExecuteScalar();
    }

    /// <summary>Owner-only on Unix (the file holds sealed credentials); best effort.</summary>
    private static void TryRestrictPermissions(string path)
    {
        if (OperatingSystem.IsWindows())
            return;
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception)
        {
            // A volume that does not support modes keeps its own.
        }
    }
}

/// <summary>Small helpers for building parameterised commands without an ORM.</summary>
public static class SqliteCommandExtensions
{
    public static SqliteCommand Command(this SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    public static string? StringOrNull(this SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    public static long? Int64OrNull(this SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
}
