using System.Net;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using SmarterMailAgent.Auth;
using SmarterMailAgent.Tasks.Approvals;

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

        /// <summary>An approval write: queued as this proposal instead of run; SmarterMail was never called.</summary>
        public string? ProposalId { get; init; }

        public bool Proposed => ProposalId is not null;
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
        // A task's model may annotate an approval write; the tool itself never sees the note.
        string? note = null;
        if (context.Gate is not null)
            (note, toolArguments) = SplitNote(toolArguments);

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
                    switch (gate.Admit(name, ok.Account, toolArguments))
                    {
                        case ToolGate.Decision.Propose:
                            return await ProposeAsync(gate, entry, ok.Account, toolArguments, note, ct);
                        case ToolGate.Decision.OverBudget when gate.IsApprovalWrite(name):
                            logger.LogInformation("Task gate refused write tool {Tool}: proposal budget spent.", name);
                            return Refuse(Status.NotAllowed,
                                $"This task may queue at most {gate.MaxProposals} change(s) for approval per run, and that " +
                                "budget is spent. Queue nothing more; list what is left in your report.");
                        case ToolGate.Decision.DryRun when gate.IsApprovalWrite(name):
                            logger.LogInformation("Dry run: approval write tool {Tool} simulated.", name);
                            return new Outcome(Status.Ok, SimulatedProposal(name, ok.Account.Handle, toolArguments), ok.Account.Handle,
                                TimeSpan.Zero)
                            {
                                Simulated = true,
                            };
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

    private async Task<Outcome> ProposeAsync(
        ToolGate gate, ToolEntry entry, Account account, IReadOnlyDictionary<string, JsonElement>? arguments, string? note,
        CancellationToken ct)
    {
        if (gate.Proposals is not { } sink)
        {
            gate.ReturnProposal();
            return Refuse(Status.NotAllowed,
                $"'{entry.Name}' needs the user's approval and cannot be queued here. Do not retry it; list it in your report.");
        }

        switch (await sink.CreateAsync(entry, account, arguments, note, ct))
        {
            case ProposalResult.Queued queued:
                logger.LogInformation("Task gate queued write tool {Tool} for approval.", entry.Name);
                return new Outcome(Status.Ok, Queued(queued.Id), account.Handle, TimeSpan.Zero) { ProposalId = queued.Id };
            case ProposalResult.TooLarge tooLarge:
                gate.ReturnProposal();
                return Refuse(Status.NotAllowed,
                    $"{tooLarge.Message} This call is too large to queue for approval: nothing was queued or changed. " +
                    "Mention it in your report.");
            default:
                gate.ReturnProposal();
                return Refuse(Status.NotAllowed,
                    "The approval queue is full: nothing was queued or changed. Do not retry; list the change in your report.");
        }
    }

    private static CallToolResult Queued(string proposalId) =>
        new()
        {
            Content =
            [
                new TextContentBlock
                {
                    Text = JsonSerializer.Serialize(new
                    {
                        queued = true,
                        proposalId,
                        note = "Queued for the user's approval. It has NOT happened. Do not retry it or make the same change " +
                               "another way. List it in your report as awaiting approval.",
                    }),
                },
            ],
        };

    private static CallToolResult SimulatedProposal(string name, string handle, IReadOnlyDictionary<string, JsonElement>? arguments) =>
        new()
        {
            Content =
            [
                new TextContentBlock
                {
                    Text = JsonSerializer.Serialize(new
                    {
                        dryRun = true,
                        queued = true,
                        note = "Test run: in a real run this change would be queued for the user's approval, not made. " +
                               "Nothing was queued or changed. Carry on, and list it in your report as awaiting approval.",
                        wouldQueue = name,
                        account = handle,
                        arguments,
                    }),
                },
            ],
        };

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

    /// <summary>
    /// Separates <c>approvalNote</c> (see <see cref="ApprovalNote"/>) from the tool's own arguments.
    /// Only a task's calls are split: in a chat the argument does not exist.
    /// </summary>
    public static (string? Note, IReadOnlyDictionary<string, JsonElement>? Arguments) SplitNote(
        IReadOnlyDictionary<string, JsonElement>? arguments)
    {
        if (arguments is null || !arguments.TryGetValue(ApprovalNote.Property, out var value))
            return (null, arguments);

        var rest = arguments
            .Where(kv => kv.Key != ApprovalNote.Property)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        return (value.ValueKind == JsonValueKind.String ? value.GetString() : null, rest);
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
/// without calling SmarterMail), and approval writes (proposed instead of run, with their own budget;
/// see <c>Tasks/Approvals</c>). <see cref="Approved"/> makes the one-shot gate an approved proposal
/// executes under.
/// </summary>
public sealed class ToolGate(IReadOnlySet<string> allowedWrites, int maxWrites, bool dryRun)
{
    private static readonly IReadOnlySet<string> None = new HashSet<string>(StringComparer.Ordinal);

    private int _writes;
    private int _proposals;
    private int _approvedUsed;
    private (string Tool, string AccountId, string ArgsHash)? _approved;

    public enum Decision { Run, DryRun, NotAllowed, OverBudget, Propose }

    public IReadOnlySet<string> AllowedWrites => allowedWrites;
    public int MaxWrites => maxWrites;
    public bool DryRunMode => dryRun;

    /// <summary>The allowed writes that are proposed for approval instead of run.</summary>
    public IReadOnlySet<string> ApprovalWrites { get; init; } = None;

    /// <summary>Proposals one run may make (simulated ones in a dry run included).</summary>
    public int MaxProposals { get; init; }

    /// <summary>Where proposals go; null = approval writes are refused.</summary>
    public IProposalSink? Proposals { get; init; }

    /// <summary>Writes admitted so far (simulated ones included).</summary>
    public int Writes => Volatile.Read(ref _writes);

    /// <summary>Proposals admitted so far (simulated ones included).</summary>
    public int ProposalsAdmitted => Volatile.Read(ref _proposals);

    public bool IsApprovalWrite(string toolName) => _approved is null && ApprovalWrites.Contains(toolName);

    /// <summary>
    /// A gate that admits exactly one call: <paramref name="tool"/>, on account
    /// <paramref name="accountId"/>, with arguments whose canonical hash is <paramref name="argsHash"/>.
    /// Then nothing, not even the same call again.
    /// </summary>
    public static ToolGate Approved(string tool, string accountId, string argsHash)
    {
        var gate = new ToolGate(new HashSet<string>(StringComparer.Ordinal) { tool }, 1, dryRun: false);
        gate._approved = (tool, accountId, argsHash);
        return gate;
    }

    /// <summary>The name-only check (allowlist, budget, dry run). An approved gate refuses it.</summary>
    public Decision Admit(string toolName) => Admit(toolName, null, null);

    /// <summary>
    /// In order: not allowed → <see cref="Decision.NotAllowed"/>; an approval write → over its own
    /// budget, else <see cref="Decision.DryRun"/> in a dry run, else <see cref="Decision.Propose"/>
    /// (never consumes <see cref="MaxWrites"/>); otherwise the write budget, then run or dry run.
    /// </summary>
    public Decision Admit(string toolName, Account? account, IReadOnlyDictionary<string, JsonElement>? arguments)
    {
        if (_approved is { } approved)
        {
            if (account is null || !string.Equals(toolName, approved.Tool, StringComparison.Ordinal) ||
                !string.Equals(account.Id, approved.AccountId, StringComparison.Ordinal))
                return Decision.NotAllowed;
            string hash;
            try
            {
                hash = ProposalHash.Of(toolName, account.Id, arguments);
            }
            catch (ProposalHash.InvalidArgumentsException)
            {
                return Decision.NotAllowed;
            }
            if (!ProposalHash.Matches(approved.ArgsHash, hash))
                return Decision.NotAllowed;
            return Interlocked.CompareExchange(ref _approvedUsed, 1, 0) == 0 ? Decision.Run : Decision.NotAllowed;
        }

        if (!allowedWrites.Contains(toolName))
            return Decision.NotAllowed;

        if (ApprovalWrites.Contains(toolName))
        {
            if (Interlocked.Increment(ref _proposals) > MaxProposals)
            {
                Interlocked.Decrement(ref _proposals);
                return Decision.OverBudget;
            }
            return dryRun ? Decision.DryRun : Decision.Propose;
        }

        if (Interlocked.Increment(ref _writes) > maxWrites)
        {
            Interlocked.Decrement(ref _writes);
            return Decision.OverBudget;
        }
        return dryRun ? Decision.DryRun : Decision.Run;
    }

    /// <summary>Gives back a proposal slot whose proposal was not queued (queue full, too large).</summary>
    internal void ReturnProposal() => Interlocked.Decrement(ref _proposals);
}
