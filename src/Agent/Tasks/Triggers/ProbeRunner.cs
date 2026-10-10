using System.Text.Json;
using SmarterMailAgent.Auth;
using SmarterMailAgent.Mcp;
using SmarterMailAgent.Profiles;
using SmarterMailAgent.Storage;

namespace SmarterMailAgent.Tasks.Triggers;

/// <summary>
/// One probe call: a read tool through <see cref="ToolDispatcher"/> on a <see cref="ProbeToolContext"/>,
/// whose gate admits no write at all. Validation already refuses a write tool; the gate is what holds
/// when a stored definition was tampered with. The result must be JSON (at most
/// <see cref="MaxResultChars"/>, depth <see cref="MaxDepth"/>); an error, <c>success:false</c> or
/// anything else is a probe failure, never "false".
/// </summary>
public sealed class ProbeRunner(ToolCatalog catalog, ToolDispatcher dispatcher, IServiceProvider services)
{
    public const int MaxResultChars = 1_000_000;
    public const int MaxDepth = 64;

    /// <summary>
    /// <paramref name="Code"/> null on success (then <paramref name="Json"/> is set and owned by the caller);
    /// otherwise <c>PROBE_INVALID</c>, <c>PROBE_FAILED</c> or <c>PROBE_NOT_JSON</c>.
    /// </summary>
    public sealed record Result(string? Code, string Content, JsonDocument? Json, string? Account) : IDisposable
    {
        public void Dispose() => Json?.Dispose();
    }

    public async Task<Result> RunAsync(Account account, ProfileRuntime? runtime, string tool, JsonElement arguments, CancellationToken ct)
    {
        if (!catalog.TryGet(tool, out var entry) || entry.Write)
            return new Result("PROBE_INVALID", $"'{tool}' is not a tool that only reads.", null, null);

        var args = arguments.ValueKind == JsonValueKind.Object
            ? arguments.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal)
            : new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        var outcome = await dispatcher.DispatchAsync(new ProbeToolContext(account, runtime), tool, args, services, ct);
        var content = ToolInvoker.Flatten(outcome.Result);
        if (outcome.Status != ToolDispatcher.Status.Ok || (outcome.Result.IsError ?? false) || ToolInvoker.PayloadIndicatesFailure(content))
            return new Result("PROBE_FAILED", content, null, outcome.Account);

        if (content.Length > MaxResultChars)
            return new Result("PROBE_NOT_JSON", content[..1000], null, outcome.Account);

        try
        {
            var json = JsonDocument.Parse(content, new JsonDocumentOptions { MaxDepth = MaxDepth });
            return new Result(null, content, json, outcome.Account);
        }
        catch (JsonException)
        {
            return new Result("PROBE_NOT_JSON", content, null, outcome.Account);
        }
    }
}

/// <summary>A probe as a tool context: one account, lazy refresh, and a gate that admits no write.</summary>
public sealed class ProbeToolContext(Account account, ProfileRuntime? runtime) : IToolContext
{
    private static readonly IReadOnlySet<string> NoWrites = new HashSet<string>();

    public IReadOnlyList<Account> Accounts { get; } = [account];
    public bool RefreshesLazily => true;

    /// <summary>A fresh gate per call site, with an empty allowlist and no budget: every write is NotAllowed.</summary>
    public ToolGate? Gate { get; } = new(NoWrites, 0, false);

    public Task OnRejectedAsync(Account rejected) => runtime?.MarkRejectedAsync(rejected) ?? Task.CompletedTask;
}

/// <summary>
/// A task's delegated account, live: borrowed from the profile's runtime, or restored from its
/// server-sealed row. The same checks and codes as a scheduled run (<c>TaskRunner</c>).
/// </summary>
public static class TriggerAccounts
{
    public static async Task<(Account? Account, string? Code)> ResolveAsync(
        ProfileRuntime runtime, AccountRestorer restorer, string accountId, CancellationToken ct)
    {
        if (runtime.Accounts.FindById(accountId) is null)
        {
            await runtime.RestoreAsync(restorer, SessionStore.MaxAccounts, ct,
                new HashSet<string>(StringComparer.Ordinal) { accountId });
        }

        var row = runtime.Row(accountId);
        if (row is null)
            return (null, "ACCOUNT_REMOVED");
        if (row.Seal != ProfileStore.SealServer)
            return (null, "ACCOUNT_NOT_DELEGATED");
        if (row.State == "rejected")
            return (null, "NEEDS_SIGN_IN");
        if (row.Entry is null)
            return (null, "ACCOUNT_UNREADABLE");
        return runtime.Accounts.FindById(accountId) is { } account ? (account, null) : (null, "ACCOUNT_UNAVAILABLE");
    }

    /// <summary>
    /// The mail server of a delegated account, without restoring it (no token refresh): from the live
    /// runtime when it knows the row, otherwise by opening the stored row with the server key.
    /// </summary>
    public static string? BaseUrlOf(ProfileRegistry registry, ProfileStore profiles, string profileId, string accountId)
    {
        if (registry.Find(profileId)?.Row(accountId)?.Entry is { } known)
            return known.BaseUrl;
        if (registry.ServerSealer is not { } sealer)
            return null;

        var row = profiles.Accounts(profileId).FirstOrDefault(r => r.Id == accountId && r.Seal == ProfileStore.SealServer);
        var plaintext = row is null ? null : sealer.OpenString(row.Blob, ProfileCrypto.ServerAccountsLabel, ProfileCrypto.Context(profileId, row.Id));
        if (plaintext is null)
            return null;
        try
        {
            using var doc = JsonDocument.Parse(plaintext);
            return doc.RootElement.TryGetProperty("baseUrl", out var url) && url.ValueKind == JsonValueKind.String ? url.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(plaintext);
        }
    }
}
