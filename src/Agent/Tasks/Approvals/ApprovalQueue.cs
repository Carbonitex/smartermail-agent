using SmarterMailAgent.Mcp;
using SmarterMailAgent.Profiles;
using SmarterMailAgent.Server;
using SmarterMailAgent.Storage;

namespace SmarterMailAgent.Tasks.Approvals;

/// <summary>
/// The approval queue as a task run sees it: the gate (with its proposal sink) a run executes under,
/// and what the run records and mails about its proposals afterwards. Registered with scheduled tasks.
/// </summary>
public sealed class ApprovalQueue(
    ProposalStore store,
    ProfileRegistry registry,
    ServerOptions options,
    IConfiguration configuration,
    ILogger<ApprovalQueue> logger)
{
    private readonly byte[] _dedupeKey = options.DataKey is { } key ? TaskProposalSink.DedupeKey(key) : new byte[32];

    /// <summary>
    /// The gate for one run: the allowlist and write budget as before, plus the task's approval writes,
    /// their per-run budget, and a sink that writes proposals for this run.
    /// </summary>
    public ToolGate GateFor(TaskDefinition definition, ProfileRow profile, string taskId, string runId, bool dryRun)
    {
        var approvals = definition.Approvals ?? new TaskApprovals();
        var allowed = definition.AllowedWrites.ToHashSet(StringComparer.Ordinal);
        var sink = registry.ServerSealer is { } sealer
            ? new TaskProposalSink(store, sealer, _dedupeKey, profile, taskId, runId, definition.Name, approvals,
                options.ApprovalMaxPending, logger)
            : null;
        return new ToolGate(allowed, definition.MaxWrites, dryRun)
        {
            ApprovalWrites = approvals.WriteList.Where(allowed.Contains).ToHashSet(StringComparer.Ordinal),
            MaxProposals = Math.Clamp(approvals.ProposalLimit, 0, options.TaskMaxProposals),
            Proposals = sink,
        };
    }

    /// <summary>Records how many proposals the run made; returns the report-email footer (null for none).</summary>
    public string? Finish(string runId, ToolGate gate, TimeZoneInfo zone)
    {
        if (gate.Proposals is not TaskProposalSink sink || sink.Count == 0)
            return null;
        store.SetRunProposals(runId, sink.Count);
        return ApprovalPrompt.EmailFooter(sink.Count, sink.EarliestExpiry, zone, AppUrl());
    }

    /// <summary><c>PUBLIC_ORIGIN</c> plus <c>PATH_BASE</c>, when the operator set an origin.</summary>
    private string? AppUrl()
    {
        if (options.PublicOrigin is not { } origin)
            return null;
        var pathBase = (configuration["PATH_BASE"] ?? "/").Trim();
        if (!pathBase.StartsWith('/'))
            pathBase = "/" + pathBase;
        if (!pathBase.EndsWith('/'))
            pathBase += "/";
        return origin.GetLeftPart(UriPartial.Authority) + pathBase;
    }
}
