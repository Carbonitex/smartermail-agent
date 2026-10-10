using Microsoft.Extensions.Logging.Abstractions;
using SmarterMailAgent.Storage;

namespace SmarterMailAgent.Server;

/// <summary>
/// The operator's commands for invite-only scheduled tasks (<c>TASKS_ACCESS=invite</c>), run in place of
/// the web host: <c>dotnet SmarterMailAgent.dll invites …</c> / <c>access …</c> (in a container,
/// <c>docker exec &lt;container&gt; dotnet SmarterMailAgent.dll invites create</c>). Reads the same
/// environment as the service (<c>DATA_DIR</c> above all) and opens the same SQLite file, which the
/// running service shares safely (WAL, busy timeout). There is deliberately no HTTP admin endpoint.
/// </summary>
public static class AdminCli
{
    public const string Usage = """
        Invite-only scheduled tasks (TASKS_ACCESS=invite):

          invites create [--uses N] [--days N] [--note TEXT]   make a code (shown once); default 1 use, no expiry
          invites list                                         codes: id, uses, expiry, note
          invites revoke <id> [--profiles]                     stop a code; --profiles also revokes every profile that used it
          access list                                          profiles that may use tasks
          access grant <profileId>                             give a profile access without a code
          access revoke <profileId>                            take it away: tasks paused, task key and instructions deleted, pending approvals denied
        """;

    public static bool Handles(string[] args) => args.Length > 0 && args[0] is "invites" or "access";

    public static int Run(string[] args, TextWriter? output = null, TextWriter? error = null, IConfiguration? configuration = null)
    {
        output ??= Console.Out;
        error ??= Console.Error;
        try
        {
            var config = configuration ?? new ConfigurationBuilder().AddEnvironmentVariables().Build();
            var options = ServerOptions.FromConfiguration(config);
            if (options.BrowserOnly)
            {
                error.WriteLine("BROWSER_ONLY_MODE=true: this server keeps no profiles, so there is nothing to invite to.");
                return 2;
            }

            var store = new TaskInviteStore(new DataStore(options, NullLogger<DataStore>.Instance));
            var code = Dispatch(args, store, output, error);
            if (code == 0 && !options.TaskInviteOnly && args is ["invites", "create", ..] or ["access", "grant", ..])
                error.WriteLine("Note: TASKS_ACCESS is not 'invite' here, so every profile may use tasks anyway.");
            return code;
        }
        catch (InvalidOperationException ex)
        {
            error.WriteLine(ex.Message);
            return 2;
        }
    }

    private static int Dispatch(string[] args, TaskInviteStore store, TextWriter output, TextWriter error)
    {
        switch (args)
        {
            case ["invites", "create", .. var rest]:
            {
                if (!TryOptions(rest, out var uses, out var days, out var note, out var problem))
                    return Fail(error, problem!);
                var (invite, code) = store.Create(note, uses, days is { } d ? TimeSpan.FromDays(d) : null);
                output.WriteLine(code);
                error.WriteLine($"Invite {invite.Id}: {uses} use(s), {(invite.ExpiresAt is { } e ? "expires " + Date(e) : "no expiry")}" +
                    (note is null ? "" : $", note \"{note}\"") + ". The code is shown only now.");
                return 0;
            }

            case ["invites", "list"]:
            {
                var rows = store.List();
                if (rows.Count == 0)
                    output.WriteLine("No invite codes.");
                foreach (var r in rows)
                {
                    var state = r.RevokedAt is not null ? "revoked"
                        : r.ExpiresAt is { } e && e <= DataStore.Now() ? "expired"
                        : r.Uses >= r.MaxUses ? "used up" : "open";
                    output.WriteLine($"{r.Id}  {state,-8} {r.Uses}/{r.MaxUses} used  created {Date(r.CreatedAt)}  " +
                        $"{(r.ExpiresAt is { } x ? "expires " + Date(x) : "no expiry")}{(r.Note is null ? "" : "  " + r.Note)}");
                }
                return 0;
            }

            case ["invites", "revoke", var id, .. var flags]:
            {
                if (flags.Any(f => f != "--profiles"))
                    return Fail(error, "invites revoke takes only --profiles.");
                if (!store.RevokeInvite(id, flags.Contains("--profiles"), out var revoked))
                    return Fail(error, $"No invite with id {id}.");
                output.WriteLine(flags.Contains("--profiles")
                    ? $"Invite {id} revoked; {revoked} profile(s) lost access."
                    : $"Invite {id} revoked. Profiles that already used it keep access (add --profiles to revoke them too).");
                return 0;
            }

            case ["access", "list"]:
            {
                var rows = store.AccessList();
                if (rows.Count == 0)
                    output.WriteLine("No profile has access.");
                foreach (var r in rows)
                    output.WriteLine($"{r.ProfileId}  since {Date(r.GrantedAt)}  last seen {Date(r.LastSeenAt)}  " +
                        (r.InviteId is null ? "granted" : $"invite {r.InviteId}{(r.InviteNote is null ? "" : " (" + r.InviteNote + ")")}"));
                return 0;
            }

            case ["access", "grant", var profileId]:
                if (!store.Grant(profileId))
                    return Fail(error, $"No profile with id {profileId}.");
                output.WriteLine($"Profile {profileId} may use scheduled tasks.");
                return 0;

            case ["access", "revoke", var profileId]:
                if (!store.RevokeAccess(profileId))
                    return Fail(error, $"Profile {profileId} has no task access (or does not exist).");
                output.WriteLine($"Profile {profileId}: access revoked, tasks paused, task key and instructions deleted, pending approvals denied.");
                return 0;

            default:
                error.WriteLine(Usage);
                return 2;
        }
    }

    private static bool TryOptions(string[] rest, out int uses, out int? days, out string? note, out string? problem)
    {
        uses = 1;
        days = null;
        note = null;
        problem = null;
        for (var i = 0; i < rest.Length; i++)
        {
            var value = i + 1 < rest.Length ? rest[i + 1] : null;
            switch (rest[i])
            {
                case "--uses" when int.TryParse(value, out var u) && u is > 0 and <= TaskInviteStore.MaxUses:
                    uses = u;
                    break;
                case "--days" when int.TryParse(value, out var d) && d is > 0 and <= TaskInviteStore.MaxDays:
                    days = d;
                    break;
                case "--note" when !string.IsNullOrWhiteSpace(value):
                    note = TaskInviteStore.CleanNote(value);
                    break;
                default:
                    problem = $"Unexpected '{rest[i]}'. --uses 1-10000, --days 1-3650, --note TEXT.";
                    return false;
            }
            i++;
        }
        return true;
    }

    private static int Fail(TextWriter error, string message)
    {
        error.WriteLine(message);
        return 1;
    }

    private static string Date(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'");
}
