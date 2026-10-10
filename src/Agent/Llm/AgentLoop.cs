using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using SmarterMailAgent.Llm.Artifacts;

namespace SmarterMailAgent.Llm;

/// <summary>
/// The tool loop for a scheduled task: a server-side port of the browser's <c>runTurn</c>
/// (<c>wwwroot/js/llm.js</c>), with the same limits — at most <c>maxRounds</c> tool rounds, tool
/// results clamped to 60,000 characters, the same handling of length / content-filter stops — minus
/// streaming and the UI callbacks. Tool calls run one after another, in the order the model asked.
/// With an <see cref="ArtifactAnalysis"/>, results over its threshold become artifact stubs and
/// <c>analyze_result</c> runs a nested loop on the analysis model (see <c>Llm/Artifacts/</c>).
/// </summary>
public sealed class AgentLoop(OpenRouterClient llm, ILogger<AgentLoop>? logger = null)
{
    private readonly ArtifactAnalyst _analyst = new(llm);

    public const int MaxToolResultChars = 60_000;

    /// <summary>What a tool call came back with.</summary>
    /// <param name="ProposalId">An approval write: queued as this proposal, not run.</param>
    public sealed record ToolResult(string Content, bool IsError, string? Account, bool Simulated, string? ProposalId = null);

    /// <summary>One entry of a run's transcript.</summary>
    /// <param name="Kind"><c>assistant</c>, <c>tool</c> or <c>notice</c>.</param>
    /// <param name="ProposalId">The call was queued for approval as this proposal.</param>
    public sealed record Step(
        string Kind, string? Content = null, string? Tool = null, string? Arguments = null, string? Account = null,
        bool IsError = false, bool Simulated = false, string? ProposalId = null)
    {
        /// <summary>The trigger's probe, handed in as the run's first tool result (not a call the model made).</summary>
        public bool Seed { get; init; }
    }

    /// <summary>
    /// A tool result to start the run with (a condition trigger's evidence): sent as a synthetic
    /// assistant call of <paramref name="Tool"/> and its tool message, never as prompt text.
    /// </summary>
    public sealed record Seed(string Tool, string Arguments, string Content, string? Account);

    /// <param name="Stop"><c>completed</c>, <c>max_rounds</c>, <c>length</c>, <c>content_filter</c>, <c>cancelled</c> or <c>error</c>.</param>
    /// <param name="CachedTokens">Of <paramref name="PromptTokens"/>, how many were read from the provider's prompt cache.</param>
    /// <param name="Cost">What OpenRouter charged for the run's requests, in credits.</param>
    public sealed record Result(
        string Stop, string? Final, IReadOnlyList<Step> Steps, int ToolCalls, long PromptTokens, long CompletionTokens,
        string? ErrorCode, string? ErrorMessage, long CachedTokens = 0, long CacheWriteTokens = 0, double Cost = 0);

    public async Task<Result> RunAsync(
        string apiKey, string model, string systemPrompt, string userPrompt, JsonArray tools,
        Func<string, IReadOnlyDictionary<string, JsonElement>?, CancellationToken, Task<ToolResult>> callTool,
        int maxRounds, CancellationToken ct, string? sessionId = null, ArtifactAnalysis? analysis = null, Seed? seed = null)
    {
        // Artifacts: the analysis lines and analyze_result are fixed for the whole run (appended once, after the
        // catalog's tools), so every round still sends a byte-identical prefix.
        ArtifactStore? artifacts = null;
        if (analysis is not null)
        {
            artifacts = new ArtifactStore(analysis.ThresholdChars, analysis.MaxRunChars, analysis.MaxArtifactChars);
            systemPrompt = $"{systemPrompt}\n\n{ArtifactAnalyst.PromptLines}";
            tools = (JsonArray)tools.DeepClone();
            tools.Add(ArtifactAnalyst.AnalyzeResultTool());
        }
        long analysisPromptTokens = 0;
        var analysisCalls = 0;

        // A run is one user turn, so nothing here is ever elided (the browser's elideOldToolResults works at
        // turn boundaries) and the request prefix only grows: every round can read the previous one's cache.
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = systemPrompt },
            new JsonObject { ["role"] = "user", ["content"] = userPrompt },
        };
        var steps = new List<Step>();
        if (seed is not null)
            AddSeed(messages, steps, seed);
        long promptTokens = 0, completionTokens = 0, cachedTokens = 0, cacheWriteTokens = 0;
        double cost = 0;
        var toolCalls = 0;
        string? final = null;

        try
        {
            for (var round = 0; ; round++)
            {
                var completion = await llm.CompleteAsync(apiKey, model, messages, tools, ct, sessionId);
                promptTokens += completion.PromptTokens;
                completionTokens += completion.CompletionTokens;
                cachedTokens += completion.CachedTokens;
                cacheWriteTokens += completion.CacheWriteTokens;
                cost += completion.Cost;

                if (!string.IsNullOrWhiteSpace(completion.Content) || completion.ToolCalls.Count > 0)
                {
                    var assistant = new JsonObject { ["role"] = "assistant", ["content"] = completion.Content ?? "" };
                    if (completion.ToolCalls.Count > 0)
                    {
                        assistant["tool_calls"] = new JsonArray(completion.ToolCalls.Select(c => (JsonNode)new JsonObject
                        {
                            ["id"] = c.Id,
                            ["type"] = "function",
                            ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.Arguments },
                        }).ToArray());
                    }
                    messages.Add(assistant);
                }

                if (!string.IsNullOrWhiteSpace(completion.Content))
                {
                    final = completion.Content;
                    steps.Add(new Step("assistant", completion.Content));
                }

                if (completion.FinishReason == "length")
                    return Done("length", "The model stopped at its output limit.");
                if (completion.FinishReason == "content_filter")
                    return Done("content_filter", "The provider's content filter stopped the answer.");
                if (completion.ToolCalls.Count == 0)
                    return Done("completed", null);

                if (round >= maxRounds)
                {
                    foreach (var call in completion.ToolCalls)
                        messages.Add(ToolMessage(call.Id, "Cancelled: tool-round limit reached."));
                    return Done("max_rounds", $"Stopped after {maxRounds} tool rounds.");
                }

                foreach (var call in completion.ToolCalls)
                {
                    ct.ThrowIfCancellationRequested();
                    toolCalls++;
                    ToolResult result;
                    if (!TryParseArguments(call.Arguments, out var arguments))
                    {
                        result = new ToolResult(
                            $"Error: the arguments for {call.Name} were not valid JSON. Retry with a JSON object.", true, null, false);
                    }
                    else if (artifacts is not null && call.Name == ArtifactStore.AnalyzeResultName)
                    {
                        var outcome = await AnalyzeAsync(apiKey, artifacts, analysis!, arguments!, analysisCalls++, analysisPromptTokens, sessionId, ct);
                        analysisPromptTokens += outcome.PromptTokens;
                        promptTokens += outcome.PromptTokens;
                        completionTokens += outcome.CompletionTokens;
                        cachedTokens += outcome.CachedTokens;
                        cacheWriteTokens += outcome.CacheWriteTokens;
                        cost += outcome.Cost;
                        foreach (var sub in outcome.Steps)
                            steps.Add(new Step("tool", Clamp(sub.Output, 4000), $"analyze_result: {sub.Operator}", Clamp(sub.Arguments, 4000), null, sub.IsError));
                        result = new ToolResult(outcome.Content, outcome.IsError, null, false);
                    }
                    else
                    {
                        try
                        {
                            result = await callTool(call.Name, arguments, ct);
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            result = new ToolResult($"Error: {ex.Message}", true, null, false);
                        }
                    }

                    // A large result stays in this run's memory; the model, and the transcript, get the stub.
                    var kept = artifacts?.Capture(call.Name, call.Arguments, result.Content, result.IsError);
                    if (kept is { } k)
                        logger?.LogInformation("Tool {Tool} result kept as an artifact: {Chars} chars.", call.Name, k.Artifact.Chars);
                    var content = kept?.Stub ?? Clamp(result.Content);
                    messages.Add(ToolMessage(call.Id, content));
                    steps.Add(new Step("tool", Clamp(kept?.Stub ?? result.Content, 4000), call.Name, Clamp(call.Arguments, 4000), result.Account,
                        result.IsError, result.Simulated, result.ProposalId));
                }
            }
        }
        catch (OperationCanceledException)
        {
            return Done("cancelled", "The run hit its time limit.", "TIMEOUT");
        }
        catch (OpenRouterClient.LlmException ex)
        {
            return Done("error", ex.Message, ex.Code);
        }

        Result Done(string stop, string? notice, string? code = null)
        {
            if (notice is not null)
                steps.Add(new Step("notice", notice));
            return new Result(stop, final, steps, toolCalls, promptTokens, completionTokens, code,
                code is null ? null : notice, cachedTokens, cacheWriteTokens, cost);
        }
    }

    /// <summary>One analyze_result call: validated, budgeted, logged by size and counts only.</summary>
    private async Task<ArtifactAnalyst.Outcome> AnalyzeAsync(
        string apiKey, ArtifactStore artifacts, ArtifactAnalysis analysis, IReadOnlyDictionary<string, JsonElement> arguments,
        int callIndex, long spent, string? sessionId, CancellationToken ct)
    {
        string Text(string key) => arguments.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : "";
        var handle = Text("artifact");
        var question = Text("question");

        ArtifactAnalyst.Outcome Refuse(string message) =>
            new(true, message, "none", 0, [], "error", 0, 0, 0, 0, 0);

        if (handle.Length == 0 || question.Length == 0)
            return Refuse("analyze_result needs both \"artifact\" (a handle such as \"r1\" from a result stub) and \"question\".");
        if (artifacts.Get(handle) is not { } artifact)
        {
            return Refuse(artifacts.Issued(handle)
                ? $"Artifact {handle} is no longer kept (this run's artifacts are capped). Run the original tool again."
                : $"There is no artifact {handle}. Use a handle from a result stub ({{\"artifact\":\"r1\",…}}).");
        }
        if (callIndex >= analysis.MaxCalls)
            return Refuse($"This run has used its {analysis.MaxCalls} analyze_result calls. Answer from what you have.");
        if (spent >= analysis.RunPromptTokens)
            return Refuse("This run's analysis token budget is spent. Answer from what you have.");

        var clock = Stopwatch.StartNew();
        var remaining = analysis with { MaxPromptTokens = Math.Min(analysis.MaxPromptTokens, analysis.RunPromptTokens - spent) };
        var outcome = await _analyst.AnalyzeAsync(apiKey, artifact, question, remaining,
            sessionId is null ? null : $"{sessionId}-analysis", ct);
        // Sizes and counts only: never the question, a pattern or any output.
        logger?.LogInformation(
            "analyze_result on a {Chars}-char artifact of {Tool}: {Mode}, {Stop}, {Rounds} round(s), {Calls} operator call(s), {PromptTokens} prompt token(s), {Ms} ms.",
            artifact.Chars, artifact.Tool, outcome.Mode, outcome.Stop, outcome.Rounds, outcome.Steps.Count, outcome.PromptTokens,
            clock.ElapsedMilliseconds);
        return outcome;
    }

    private static void AddSeed(JsonArray messages, List<Step> steps, Seed seed)
    {
        const string id = "call_seed_0";
        messages.Add(new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = "",
            ["tool_calls"] = new JsonArray(new JsonObject
            {
                ["id"] = id,
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = seed.Tool, ["arguments"] = seed.Arguments },
            }),
        });
        messages.Add(ToolMessage(id, Clamp(seed.Content)));
        steps.Add(new Step("tool", Clamp(seed.Content, 4000), seed.Tool, Clamp(seed.Arguments, 4000), seed.Account) { Seed = true });
    }

    private static JsonObject ToolMessage(string id, string content) =>
        new() { ["role"] = "tool", ["tool_call_id"] = id, ["content"] = content };

    public static string Clamp(string text, int max = MaxToolResultChars) =>
        text.Length <= max ? text : text[..max] + $"\n\n[truncated: {text.Length - max} more characters]";

    private static bool TryParseArguments(string raw, out IReadOnlyDictionary<string, JsonElement>? arguments)
    {
        arguments = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            arguments = new Dictionary<string, JsonElement>();
            return true;
        }

        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return false;
            arguments = doc.RootElement.EnumerateObject()
                .ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
