using System.Net;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using SmarterMailAgent.Auth;

namespace SmarterMailAgent.Mcp;

/// <summary>
/// The one path every tool call takes, from <c>/api/tools/call</c> and from <c>/mcp</c> alike:
/// <list type="number">
///   <item>resolve the account from the <c>account</c> argument (<see cref="ToolPolicy.Resolve{T}"/>);</item>
///   <item>strip that argument, so the tool never sees it;</item>
///   <item>refuse a wrong role or a write on a read-only account;</item>
///   <item>in a remembered session, refresh that account's token first if it is about to expire;</item>
///   <item>invoke the tool on an <see cref="AccountServiceProvider"/> for that account;</item>
///   <item>retry once after an in-memory token refresh if SmarterMail answered 401.</item>
/// </list>
/// </summary>
public sealed class ToolDispatcher(
    ToolCatalog catalog, ToolInvoker invoker, SessionStore store, ILogger<ToolDispatcher> logger)
{
    public enum Status
    {
        Ok,
        UnknownTool,

        /// <summary>Missing, unknown or wrong-role <c>account</c>: the model can fix it.</summary>
        InvalidAccount,

        /// <summary>A write tool on a read-only account.</summary>
        ReadOnly,
    }

    /// <param name="Account">The handle the call ran as; null when it failed before resolving one.</param>
    public sealed record Outcome(Status Status, CallToolResult Result, string? Account, TimeSpan Duration)
    {
        /// <summary>For the non-<see cref="Status.Ok"/> statuses: the message to hand back.</summary>
        public string Message => ToolInvoker.Flatten(Result);
    }

    public async Task<Outcome> DispatchAsync(
        Session session,
        string name,
        IReadOnlyDictionary<string, JsonElement>? arguments,
        IServiceProvider requestServices,
        CancellationToken ct)
    {
        if (!catalog.TryGet(name, out var entry))
            return Refuse(Status.UnknownTool, $"Unknown tool '{name}'.");

        var (requested, toolArguments) = SplitAccount(arguments);

        switch (ToolPolicy.Resolve(name, entry.Scope, entry.Write, session.Accounts, requested))
        {
            case ToolPolicy.Resolution<Account>.Invalid invalid:
                logger.LogInformation("Tool {Tool} refused: no usable account.", name);
                return Refuse(Status.InvalidAccount, invalid.Message);

            case ToolPolicy.Resolution<Account>.ReadOnly readOnly:
                logger.LogInformation("Blocked write tool {Tool} on a read-only account.", name);
                return Refuse(Status.ReadOnly, readOnly.Message);

            case ToolPolicy.Resolution<Account>.Ok ok:
                if (session.IsRemembered && !await EnsureFreshAsync(session, ok.Account, ct))
                {
                    return Refuse(Status.InvalidAccount,
                        $"SmarterMail no longer accepts the sign-in for account '{ok.Account.Handle}', so it was " +
                        "removed from this chat. Ask the user to add it again.");
                }
                return await InvokeAsync(entry, ok.Account, toolArguments, requestServices, ct);

            default:
                throw new InvalidOperationException("Unhandled account resolution.");
        }
    }

    /// <summary>
    /// The lazy refresh of a remembered session (the sweeper leaves those alone so the browser's
    /// bundle stays valid while it is away). False only when SmarterMail refused the refresh token:
    /// the account is dead and is dropped, as the sweeper drops one in an ordinary session. An
    /// unreachable server is not fatal here; the call goes ahead on the token it has.
    /// </summary>
    private async Task<bool> EnsureFreshAsync(Session session, Account account, CancellationToken ct)
    {
        var result = await account.EnsureFreshAsync(ct);
        if (result != RefreshResult.Rejected)
            return true;

        logger.LogWarning("Token refresh refused for an account ({Role}) in a remembered session; dropping it.", account.Role);
        await store.RemoveAccountAsync(session, account.Id);
        return false;
    }

    private async Task<Outcome> InvokeAsync(
        ToolEntry entry, Account account, IReadOnlyDictionary<string, JsonElement>? arguments,
        IServiceProvider requestServices, CancellationToken ct)
    {
        var services = new AccountServiceProvider(requestServices, account);
        var result = await invoker.InvokeAsync(entry.Tool, arguments, services, ct);
        var duration = result.Duration;

        // One transparent retry when SmarterMail says the access token went stale: Core's own
        // refresh path reads the token file we deliberately never write, so we refresh in memory.
        if (result.Failed && LooksUnauthorized(ToolInvoker.Flatten(result.Raw)) &&
            await account.RefreshTokenAsync(ct))
        {
            logger.LogInformation("Retrying {Tool} after an in-memory token refresh.", entry.Name);
            result = await invoker.InvokeAsync(entry.Tool, arguments, services, ct);
            duration += result.Duration;
        }

        // Tool name, role, duration and outcome only. Never the account, arguments or results.
        logger.LogInformation("Tool {Tool} ({Role}) took {Duration}ms isError={IsError}",
            entry.Name, account.Role, (int)duration.TotalMilliseconds, result.Failed);

        return new Outcome(Status.Ok, result.Raw, account.Handle, duration);
    }

    /// <summary>
    /// Separates the <c>account</c> argument from the tool's own. A JSON null counts as omitted; any
    /// other non-string is passed on as its raw text so it fails as an unknown account.
    /// </summary>
    public static (string? Account, IReadOnlyDictionary<string, JsonElement>? Arguments) SplitAccount(
        IReadOnlyDictionary<string, JsonElement>? arguments)
    {
        if (arguments is null || !arguments.TryGetValue(ToolPolicy.AccountProperty, out var value))
            return (null, arguments);

        var rest = arguments
            .Where(kv => kv.Key != ToolPolicy.AccountProperty)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

        var account = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => value.GetRawText(),
        };

        return (account, rest);
    }

    private static Outcome Refuse(Status status, string message) =>
        new(status, ToolInvoker.ErrorResult(message), null, TimeSpan.Zero);

    private static bool LooksUnauthorized(string content) =>
        content.Contains(nameof(HttpStatusCode.Unauthorized), StringComparison.OrdinalIgnoreCase) ||
        content.Contains("401", StringComparison.Ordinal);
}
