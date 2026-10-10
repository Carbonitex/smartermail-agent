using System.Diagnostics;
using System.Text.Json;
using SmarterMailAgent.Auth;
using SmarterMailAgent.Llm;
using SmarterMailAgent.Mcp;
using SmarterMailAgent.Profiles;
using SmarterMailAgent.Storage;

namespace SmarterMailAgent.Tasks.Approvals;

/// <summary>
/// Executes an approved proposal, at most once:
/// <list type="number">
///   <item>claim it (<c>pending</c> → <c>executing</c>, one conditional UPDATE; only one caller wins);</item>
///   <item>open the payload (erased at every terminal state, so a row flipped back to pending in the
///   database has nothing left to run);</item>
///   <item>compare the browser's hash with the payload's: a mismatch puts it back to pending, nothing ran;</item>
///   <item>re-check the <i>current</i> task: tool still allowed, account still in the task;</item>
///   <item>get the account live (as a run would); a mail server that does not answer before the call
///   puts it back to pending;</item>
///   <item>dispatch under <see cref="ToolGate.Approved"/>: exactly this tool, account and argument hash, once;</item>
///   <item>store the outcome with the result sealed to the profile's public key, and erase the payload.</item>
/// </list>
/// Logs the proposal id, tool name, role, duration and outcome; never arguments, results or accounts.
/// </summary>
public sealed class ProposalExecutor(
    ProposalStore store,
    TaskStore tasks,
    ProfileStore profiles,
    ProfileRegistry registry,
    AccountRestorer restorer,
    ToolDispatcher dispatcher,
    IServiceProvider services,
    ILogger<ProposalExecutor> logger)
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    public enum Kind
    {
        /// <summary>The tool ran and reported success.</summary>
        Executed,

        /// <summary>Claimed, then refused or the tool failed: terminal, see the error code.</summary>
        Failed,

        /// <summary>The run was cut off mid-call: it may or may not have happened.</summary>
        Unknown,

        NotFound,
        NotPending,
        Expired,

        /// <summary>The browser's hash is not the payload's. Still pending; nothing ran.</summary>
        Mismatch,

        /// <summary>The mail server did not answer before the call. Still pending; nothing ran.</summary>
        Unavailable,
    }

    public sealed record Execution(Kind Kind, string? ErrorCode = null, string? Result = null);

    public sealed record ResultPayload(int V, bool IsError, string Content, string? Account, long DurationMs);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<Execution> ExecuteAsync(string profileId, string proposalId, string argsHash)
    {
        var now = DataStore.Now();
        if (store.Get(profileId, proposalId) is not { } row)
            return new Execution(Kind.NotFound);
        if (!store.Claim(profileId, proposalId, now))
            return new Execution(IsExpired(store.Get(profileId, proposalId) ?? row, now) ? Kind.Expired : Kind.NotPending);

        try
        {
            return await ExecuteClaimedAsync(row, argsHash);
        }
        catch (Exception ex)
        {
            // Whatever happened, the claim must not stay 'executing' with its payload.
            logger.LogError(ex, "Proposal {Proposal} execution failed unexpectedly.", proposalId);
            store.Finish(profileId, proposalId, ProposalStore.Unknown, "ERROR", null, read: true);
            return new Execution(Kind.Unknown, "ERROR");
        }
    }

    private async Task<Execution> ExecuteClaimedAsync(ProposalRow row, string argsHash)
    {
        var profileId = row.ProfileId;
        if (registry.ServerSealer is not { } sealer || profiles.GetProfile(profileId) is not { } profile)
            return Fail(row, "TASKS_DISABLED");

        var payload = TaskProposalSink.OpenPayload(sealer, profileId, row.Id, row.Payload);
        if (payload is null || payload.TaskId != row.TaskId)
            return Fail(row, "PROPOSAL_UNREADABLE");

        if (!ProposalHash.Matches(payload.ArgsHash, argsHash))
        {
            store.Release(profileId, row.Id);
            logger.LogInformation("Proposal {Proposal}: the approved hash does not match; left pending.", row.Id);
            return new Execution(Kind.Mismatch);
        }

        // The task as it is now: approval never overrides a definition that was tightened since.
        if (tasks.Get(profileId, row.TaskId) is not { } task ||
            TaskDefinition.Open(sealer, profileId, task.Id, task.Definition) is not { } definition)
            return Fail(row, "TOOL_NO_LONGER_ALLOWED");
        if (!definition.AllowedWrites.Contains(payload.Tool, StringComparer.Ordinal))
            return Fail(row, "TOOL_NO_LONGER_ALLOWED");
        if (!definition.AccountIds.Contains(payload.AccountId, StringComparer.Ordinal))
            return Fail(row, "ACCOUNT_NOT_IN_TASK");

        Dictionary<string, JsonElement> arguments;
        try
        {
            arguments = ProposalHash.Parse(payload.ArgsJson);
        }
        catch (Exception ex) when (ex is JsonException or ProposalHash.InvalidArgumentsException)
        {
            return Fail(row, "PROPOSAL_UNREADABLE");
        }

        var runtime = registry.AcquireTask(profileId);
        try
        {
            using var timeout = new CancellationTokenSource(Timeout);
            var (account, failure) = await AccountAsync(runtime, payload.AccountId, timeout.Token);
            if (failure == "ACCOUNT_UNAVAILABLE")
            {
                store.Release(profileId, row.Id);
                logger.LogInformation("Proposal {Proposal}: the mail server did not answer; left pending.", row.Id);
                return new Execution(Kind.Unavailable, failure);
            }
            if (failure is not null)
                return Fail(row, failure);

            var gate = ToolGate.Approved(payload.Tool, payload.AccountId, payload.ArgsHash);
            var context = new TaskToolContext([account!], runtime, gate);
            var watch = Stopwatch.StartNew();
            ToolDispatcher.Outcome outcome;
            try
            {
                outcome = await dispatcher.DispatchAsync(context, payload.Tool, arguments, services, timeout.Token);
            }
            catch (OperationCanceledException)
            {
                store.Finish(profileId, row.Id, ProposalStore.Unknown, "TIMEOUT",
                    SealResult(profile, row.Id, new ResultPayload(1, true, "The call did not finish within 60 seconds.", account!.Handle,
                        watch.ElapsedMilliseconds)), read: true);
                logger.LogWarning("Proposal {Proposal} ({Tool}) timed out; outcome unknown.", row.Id, payload.Tool);
                return new Execution(Kind.Unknown, "TIMEOUT");
            }

            var content = ToolInvoker.Flatten(outcome.Result);
            string? code = outcome.Status switch
            {
                ToolDispatcher.Status.Ok when (outcome.Result.IsError ?? false) || ToolInvoker.PayloadIndicatesFailure(content) => "TOOL_ERROR",
                ToolDispatcher.Status.Ok => null,
                ToolDispatcher.Status.ReadOnly => "ACCOUNT_READ_ONLY",
                ToolDispatcher.Status.InvalidAccount => runtime.Row(payload.AccountId)?.State == "rejected" ? "NEEDS_SIGN_IN" : "ACCOUNT_ROLE_CHANGED",
                ToolDispatcher.Status.UnknownTool => "TOOL_NO_LONGER_ALLOWED",
                _ => "PROPOSAL_UNREADABLE",
            };

            var ran = outcome.Status == ToolDispatcher.Status.Ok;
            var sealedResult = SealResult(profile, row.Id, new ResultPayload(1, code is not null,
                AgentLoop.Clamp(content), ran ? outcome.Account : null, (long)outcome.Duration.TotalMilliseconds));
            store.Finish(profileId, row.Id, code is null ? ProposalStore.Executed : ProposalStore.Failed, code, sealedResult, read: true);

            logger.LogInformation("Proposal {Proposal} executed: {Tool} ({Role}) took {Duration}ms, isError={IsError}, code={Code}.",
                row.Id, payload.Tool, account!.Role, (int)outcome.Duration.TotalMilliseconds, code is not null, code ?? "ok");
            return new Execution(code is null ? Kind.Executed : Kind.Failed, code, sealedResult);
        }
        finally
        {
            await runtime.ReleaseTaskAsync();
        }
    }

    /// <summary>The proposal's account, live: as a task run gets its accounts (borrowed or restored from its server-sealed row).</summary>
    private async Task<(Account?, string?)> AccountAsync(ProfileRuntime runtime, string accountId, CancellationToken ct)
    {
        if (runtime.Accounts.FindById(accountId) is null)
            await runtime.RestoreAsync(restorer, SessionStore.MaxAccounts, ct, new HashSet<string>(StringComparer.Ordinal) { accountId });

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

    /// <summary>Expired, or still pending past its expiry (the sweep has not reached it yet).</summary>
    public static bool IsExpired(ProposalRow row, long now) =>
        row.Status == ProposalStore.Expired || (row.Status == ProposalStore.Pending && row.ExpiresAt <= now);

    private Execution Fail(ProposalRow row, string code)
    {
        store.Finish(row.ProfileId, row.Id, ProposalStore.Failed, code, null, read: true);
        logger.LogInformation("Proposal {Proposal} refused after approval: {Code}.", row.Id, code);
        return new Execution(Kind.Failed, code);
    }

    private static string SealResult(ProfileRow profile, string proposalId, ResultPayload result) =>
        ProfileCrypto.SealToPublicKey(JsonSerializer.SerializeToUtf8Bytes(result, Json), profile.PublicKey,
            TaskProposalSink.ResultContext(profile.Id, proposalId));

    public static string Explain(string code) => code switch
    {
        "PROPOSAL_UNREADABLE" => "The proposal could not be opened with this server's key (DATA_KEY changed?). Nothing ran.",
        "TOOL_NO_LONGER_ALLOWED" => "The task no longer allows this change. Nothing ran.",
        "ACCOUNT_NOT_IN_TASK" => "The task no longer uses this account. Nothing ran.",
        "ACCOUNT_READ_ONLY" => "The account is signed in without \"Allow changes\" now. Nothing ran.",
        "ACCOUNT_ROLE_CHANGED" => "The account can no longer run this tool. Nothing ran.",
        "TOOL_ERROR" => "The change was attempted and SmarterMail reported an error; see the result.",
        "INTERRUPTED" => "The server stopped while the change was running: it may or may not have happened. Check before running the task again.",
        "TIMEOUT" => "The change did not finish within 60 seconds: it may or may not have happened. Check before running the task again.",
        "ERROR" => "Something went wrong while running the change: it may or may not have happened.",
        _ => TaskRunner.Explain(code),
    };
}
