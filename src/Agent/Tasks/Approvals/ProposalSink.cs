using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SmarterMailAgent.Auth;
using SmarterMailAgent.Mcp;
using SmarterMailAgent.Profiles;
using SmarterMailAgent.Storage;

namespace SmarterMailAgent.Tasks.Approvals;

/// <summary>Where a task gate's <c>Propose</c> decisions go.</summary>
public interface IProposalSink
{
    Task<ProposalResult> CreateAsync(
        ToolEntry tool, Account account, IReadOnlyDictionary<string, JsonElement>? arguments, string? note, CancellationToken ct);
}

/// <summary>What became of a proposed call.</summary>
public abstract record ProposalResult
{
    /// <param name="Deduped">The same call was already pending for this task: that proposal's id.</param>
    public sealed record Queued(string Id, bool Deduped, DateTimeOffset ExpiresAt) : ProposalResult;

    /// <summary>The profile already has <c>APPROVAL_MAX_PENDING</c> pending proposals.</summary>
    public sealed record QueueFull : ProposalResult;

    /// <summary>The canonical arguments are over 64 KB, or not canonicalisable.</summary>
    public sealed record TooLarge(string Message) : ProposalResult;
}

/// <summary>
/// One task run's proposals: canonicalises and hashes the call, seals the payload with
/// <c>DATA_KEY</c> and the display copy to the profile's public key, and writes the row (deduplicated
/// against the same task's pending proposals, capped per profile). Logs ids and whether it deduped,
/// never the tool's arguments, the note, the hash or the account.
/// </summary>
public sealed class TaskProposalSink(
    ProposalStore store,
    Sealer serverSealer,
    byte[] dedupeKey,
    ProfileRow profile,
    string taskId,
    string runId,
    string taskName,
    TaskApprovals approvals,
    int maxPending,
    ILogger logger) : IProposalSink
{
    public const string PayloadLabel = "sma-task-proposal-v1";

    private readonly Lock _lock = new();
    private readonly HashSet<string> _ids = new(StringComparer.Ordinal);
    private DateTimeOffset? _earliestExpiry;

    /// <summary>Distinct proposals this run created or pointed at.</summary>
    public int Count
    {
        get { lock (_lock) return _ids.Count; }
    }

    /// <summary>When the first of them expires; null when there are none.</summary>
    public DateTimeOffset? EarliestExpiry
    {
        get { lock (_lock) return _earliestExpiry; }
    }

    public sealed record Payload(int V, string Tool, string AccountId, string ArgsJson, string ArgsHash, string TaskId, string RunId);

    public sealed record Display(
        int V, string Tool, string Description, string Category, string Scope, bool Destructive,
        string AccountId, string AccountLogin, string AccountRole, string AccountHost, bool ReadOnly,
        string ArgsJson, string ArgsHash, string? Note, string TaskName, string TaskId, string RunId);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task<ProposalResult> CreateAsync(
        ToolEntry tool, Account account, IReadOnlyDictionary<string, JsonElement>? arguments, string? note, CancellationToken ct)
    {
        string argsJson;
        try
        {
            argsJson = ProposalHash.Canonical(arguments);
        }
        catch (ProposalHash.InvalidArgumentsException ex)
        {
            return Task.FromResult<ProposalResult>(new ProposalResult.TooLarge(ex.Message));
        }
        if (Encoding.UTF8.GetByteCount(argsJson) > ProposalHash.MaxArgsBytes)
            return Task.FromResult<ProposalResult>(new ProposalResult.TooLarge("The arguments are too large to queue for approval."));

        var argsHash = ProposalHash.Of(tool.Name, account.Id, argsJson);
        var id = ProfileCrypto.NewId();
        var now = DateTimeOffset.UtcNow;
        var expires = now + approvals.Ttl;
        var destructive = tool.Tool.ProtocolTool.Annotations?.DestructiveHint == true;

        var payload = JsonSerializer.SerializeToUtf8Bytes(new Payload(1, tool.Name, account.Id, argsJson, argsHash, taskId, runId), Json);
        var display = JsonSerializer.SerializeToUtf8Bytes(new Display(
            1, tool.Name, tool.Tool.ProtocolTool.Description ?? "", tool.Category, tool.Scope.ToString(), destructive,
            account.Id, account.EmailAddress, account.Role.ToString(), Account.HostOf(account.BaseUrl), account.ReadOnly,
            argsJson, argsHash, ApprovalNote.Clean(note), taskName, taskId, runId), Json);

        ProposalRow row;
        try
        {
            row = new ProposalRow(id, profile.Id, taskId, runId, ProposalStore.Pending,
                NeedsPasskey(tool, approvals), Dedupe(dedupeKey, profile.Id, taskId, argsHash),
                serverSealer.SealString(payload, PayloadLabel, ProfileCrypto.Context(profile.Id, id)),
                ProfileCrypto.SealToPublicKey(display, profile.PublicKey, DisplayContext(profile.Id, id)),
                null, null, now.ToUnixTimeMilliseconds(), expires.ToUnixTimeMilliseconds(), null, null, false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
            CryptographicOperations.ZeroMemory(display);
        }

        var (outcome, storedId) = store.Create(row, maxPending);
        if (outcome == ProposalStore.CreateOutcome.QueueFull)
        {
            logger.LogInformation("Task {Task}: approval queue full, nothing proposed.", taskId);
            return Task.FromResult<ProposalResult>(new ProposalResult.QueueFull());
        }

        var deduped = outcome == ProposalStore.CreateOutcome.Deduped;
        var expiresAt = deduped && store.Get(profile.Id, storedId!) is { } existing
            ? DateTimeOffset.FromUnixTimeMilliseconds(existing.ExpiresAt)
            : expires;
        lock (_lock)
        {
            _ids.Add(storedId!);
            if (_earliestExpiry is null || expiresAt < _earliestExpiry)
                _earliestExpiry = expiresAt;
        }

        logger.LogInformation("Task {Task}: proposal {Proposal} {Kind}.", taskId, storedId, deduped ? "already pending (deduped)" : "created");
        return Task.FromResult<ProposalResult>(new ProposalResult.Queued(storedId!, deduped, expiresAt));
    }

    /// <summary>
    /// Destructive tools, admin scopes, mail that leaves the server (<see cref="SendsMail"/>), or a
    /// task that asks for a passkey on every approval.
    /// </summary>
    public static bool NeedsPasskey(ToolEntry tool, TaskApprovals approvals) =>
        tool.Tool.ProtocolTool.Annotations?.DestructiveHint == true ||
        tool.Scope is ToolScope.DomainAdmin or ToolScope.SysAdmin ||
        SendsMail(tool.Name) ||
        approvals.PasskeyAlways;

    /// <summary>Name prefixes of tools that send mail; any tool so named is covered even if added later.</summary>
    public static readonly string[] OutboundMailPrefixes = ["send_", "forward_", "reply_"];

    /// <summary>
    /// Mailbox tools that send mail (or set up mail that leaves the server) without a send prefix:
    /// meeting responses and calendar invitations go to other people, and content filters can forward.
    /// Pinned by <c>ApprovalGateTests</c>.
    /// </summary>
    public static readonly IReadOnlySet<string> OutboundMailTools = new HashSet<string>(StringComparer.Ordinal)
    {
        "respond_to_meeting",
        "new_or_update_calendar_event",
        "create_content_filter",
        "create_content_filter_simple",
        "update_content_filter",
    };

    /// <summary>The call can send mail to someone (now or through a rule it creates).</summary>
    public static bool SendsMail(string toolName) =>
        OutboundMailTools.Contains(toolName) ||
        OutboundMailPrefixes.Any(p => toolName.StartsWith(p, StringComparison.Ordinal));

    /// <summary>
    /// <c>HMAC-SHA-256(HKDF(DATA_KEY, "sma-proposal-dedupe-v1"), profileId|taskId|argsHash)</c>: equal
    /// for the same task and the same exact call, useless without the server key.
    /// </summary>
    public static string Dedupe(byte[] dedupeKey, string profileId, string taskId, string argsHash) =>
        Base64Url.Encode(HMACSHA256.HashData(dedupeKey, Encoding.UTF8.GetBytes($"{profileId}|{taskId}|{argsHash}")));

    public static byte[] DedupeKey(byte[] dataKey) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, dataKey, 32, salt: new byte[32], info: "sma-proposal-dedupe-v1"u8.ToArray());

    /// <summary>Associated data of the display copy; the browser opens it with the same.</summary>
    public static string DisplayContext(string profileId, string proposalId) => $"task-proposal|{profileId}|{proposalId}";

    /// <summary>Associated data of an execution's result.</summary>
    public static string ResultContext(string profileId, string proposalId) => $"task-proposal-result|{profileId}|{proposalId}";

    public static Payload? OpenPayload(Sealer sealer, string profileId, string proposalId, string? sealedValue)
    {
        if (sealedValue is null)
            return null;
        var plaintext = sealer.OpenString(sealedValue, PayloadLabel, ProfileCrypto.Context(profileId, proposalId));
        if (plaintext is null)
            return null;
        try
        {
            return JsonSerializer.Deserialize<Payload>(plaintext, Json);
        }
        catch (JsonException)
        {
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }
}
