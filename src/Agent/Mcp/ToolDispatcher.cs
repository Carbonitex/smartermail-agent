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
///   <item>for a scheduled task, apply its gate (allowlist, write budget, dry run);</item>
///   <item>in a remembered or profile session (or a task), refresh that account's token first if it is about to expire;</item>
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

        /// <summary>A scheduled task's gate refused the call (not on its allowlist, or over its write budget).</summary>
        NotAllowed,
    }

    /// <param name="Account">The handle the call ran as; null when it failed before resolving one.</param>
    public sealed record Outcome(Status Status, CallToolResult Result, string? Account, TimeSpan Duration)
    {
        /// <summary>For the non-<see cref="Status.Ok"/> statuses: the message to hand back.</summary>
        public string Message => ToolInvoker.Flatten(Result);

        /// <summary>A dry run answered this write itself; SmarterMail was never called.</summary>
        public bool Simulated { get; init; }
    }

    /// <summary>A browser (or MCP-token) call, as the chat makes it.</summary>
    public Task<Outcome> DispatchAsync(
        Session session,
        string name,
        IReadOnlyDictionary<string, JsonElement>? arguments,
        IServiceProvider requestServices,
        CancellationToken ct) =>
        DispatchAsync(new SessionToolContext(session, store), name, arguments, requestServices, ct);

    public async Task<Outcome> DispatchAsync(
        IToolContext context,
        string name,
        IReadOnlyDictionary<string, JsonElement>? arguments,
        IServiceProvider requestServices,
        CancellationToken ct)
    {
        if (!catalog.TryGet(name, out var entry))
            return Refuse(Status.UnknownTool, $"Unknown tool '{name}'.");

        var (requested, toolArguments) = SplitAccount(arguments);

        switch (ToolPolicy.Resolve(name, entry.Scope, entry.Write, context.Accounts, requested))
        {
            case ToolPolicy.Resolution<Account>.Invalid invalid:
                logger.LogInformation("Tool {Tool} refused: no usable account.", name);
                return Refuse(Status.InvalidAccount, invalid.Message);

            case ToolPolicy.Resolution<Account>.ReadOnly readOnly:
                logger.LogInformation("Blocked write tool {Tool} on a read-only account.", name);
                return Refuse(Status.ReadOnly, readOnly.Message);

            case ToolPolicy.Resolution<Account>.Ok ok:
                if (entry.Write && context.Gate is { } gate)
                {
                    switch (gate.Admit(name))
                    {
                        case ToolGate.Decision.NotAllowed:
                            logger.LogInformation("Task gate refused write tool {Tool}: not allowed.", name);
                            return Refuse(Status.NotAllowed,
                                $"'{name}' is not one of the changes this task may make. Do not retry it; " +
                                "say in your summary that it would be needed.");
                        case ToolGate.Decision.OverBudget:
                            logger.LogInformation("Task gate refused write tool {Tool}: write budget spent.", name);
                            return Refuse(Status.NotAllowed,
                                $"This task may make at most {gate.MaxWrites} change(s) per run, and that budget is " +
                                "spent. Make no further changes; list what is left in your summary.");
                        case ToolGate.Decision.DryRun:
                            logger.LogInformation("Dry run: write tool {Tool} simulated.", name);
                            return new Outcome(Status.Ok, Simulated(name, ok.Account.Handle, toolArguments), ok.Account.Handle, TimeSpan.Zero)
                            {
                                Simulated = true,
                            };
                    }
                }

                if (context.RefreshesLazily && !await EnsureFreshAsync(context, ok.Account, ct))
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

    private static CallToolResult Simulated(string name, string handle, IReadOnlyDictionary<string, JsonElement>? arguments) =>
        new()
        {
            Content =
            [
                new TextContentBlock
                {
                    Text = JsonSerializer.Serialize(new
                    {
                        dryRun = true,
                        note = "Test run: nothing was changed. Carry on as if this call had succeeded.",
                        wouldCall = name,
                        account = handle,
                        arguments,
                    }),
                },
            ],
        };

    /// <summary>
    /// The lazy refresh of a remembered session (the sweeper leaves those alone so the browser's
    /// bundle stays valid while it is away). False only when SmarterMail refused the refresh token:
    /// the account is dead and is dropped, as the sweeper drops one in an ordinary session. An
    /// unreachable server is not fatal here; the call goes ahead on the token it has.
    /// </summary>
    private async Task<bool> EnsureFreshAsync(IToolContext context, Account account, CancellationToken ct)
    {
        var result = await account.EnsureFreshAsync(ct);
        if (result != RefreshResult.Rejected)
            return true;

        logger.LogWarning("Token refresh refused for an account ({Role}) refreshed per call; dropping it.", account.Role);
        await context.OnRejectedAsync(account);
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

/// <summary>What a tool call runs against: a browser session, or a scheduled task's run.</summary>
public interface IToolContext
{
    IReadOnlyList<Account> Accounts { get; }

    /// <summary>Refresh per call (lazily) rather than relying on the sweeper.</summary>
    bool RefreshesLazily { get; }

    /// <summary>SmarterMail refused this account's refresh token: it is dead.</summary>
    Task OnRejectedAsync(Account account);

    /// <summary>Extra limits on writes; null for a person at the keyboard.</summary>
    ToolGate? Gate { get; }
}

/// <summary>A browser session as a tool context: a dead account leaves the session (or its profile marks it).</summary>
public sealed class SessionToolContext(Session session, SessionStore store) : IToolContext
{
    public IReadOnlyList<Account> Accounts => session.Accounts;
    public bool RefreshesLazily => session.RefreshesLazily;
    public ToolGate? Gate => null;

    public async Task OnRejectedAsync(Account account)
    {
        if (session.Profile is { } profile)
            await profile.MarkRejectedAsync(account);
        else
            await store.RemoveAccountAsync(session, account.Id);
    }
}

/// <summary>
/// The limits a scheduled task puts on writes, enforced here and not only by the tool list the model
/// was shown: an explicit allowlist of tool names, a budget per run, and dry-run (writes answered
/// without calling SmarterMail).
/// </summary>
public sealed class ToolGate(IReadOnlySet<string> allowedWrites, int maxWrites, bool dryRun)
{
    private int _writes;

    public enum Decision { Run, DryRun, NotAllowed, OverBudget }

    public IReadOnlySet<string> AllowedWrites => allowedWrites;
    public int MaxWrites => maxWrites;
    public bool DryRunMode => dryRun;

    /// <summary>Writes admitted so far (simulated ones included).</summary>
    public int Writes => Volatile.Read(ref _writes);

    public Decision Admit(string toolName)
    {
        if (!allowedWrites.Contains(toolName))
            return Decision.NotAllowed;
        if (Interlocked.Increment(ref _writes) > maxWrites)
        {
            Interlocked.Decrement(ref _writes);
            return Decision.OverBudget;
        }
        return dryRun ? Decision.DryRun : Decision.Run;
    }
}
