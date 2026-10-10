using System.Text.Json;
using System.Text.Json.Nodes;

namespace SmarterMailAgent.Llm;

/// <summary>
/// The tool loop for a scheduled task: a server-side port of the browser's <c>runTurn</c>
/// (<c>wwwroot/js/llm.js</c>), with the same limits — at most <c>maxRounds</c> tool rounds, tool
/// results clamped to 60,000 characters, the same handling of length / content-filter stops — minus
/// streaming and the UI callbacks. Tool calls run one after another, in the order the model asked.
/// </summary>
public sealed class AgentLoop(OpenRouterClient llm)
{
    public const int MaxToolResultChars = 60_000;

    /// <summary>What a tool call came back with.</summary>
    /// <param name="ProposalId">An approval write: queued as this proposal, not run.</param>
    public sealed record ToolResult(string Content, bool IsError, string? Account, bool Simulated, string? ProposalId = null);

    /// <summary>One entry of a run's transcript.</summary>
    /// <param name="Kind"><c>assistant</c>, <c>tool</c> or <c>notice</c>.</param>
    /// <param name="ProposalId">The call was queued for approval as this proposal.</param>
    public sealed record Step(
        string Kind, string? Content = null, string? Tool = null, string? Arguments = null, string? Account = null,
        bool IsError = false, bool Simulated = false, string? ProposalId = null);

    /// <param name="Stop"><c>completed</c>, <c>max_rounds</c>, <c>length</c>, <c>content_filter</c>, <c>cancelled</c> or <c>error</c>.</param>
    /// <param name="CachedTokens">Of <paramref name="PromptTokens"/>, how many were read from the provider's prompt cache.</param>
    /// <param name="Cost">What OpenRouter charged for the run's requests, in credits.</param>
    public sealed record Result(
        string Stop, string? Final, IReadOnlyList<Step> Steps, int ToolCalls, long PromptTokens, long CompletionTokens,
        string? ErrorCode, string? ErrorMessage, long CachedTokens = 0, long CacheWriteTokens = 0, double Cost = 0);

    public async Task<Result> RunAsync(
        string apiKey, string model, string systemPrompt, string userPrompt, JsonArray tools,
        Func<string, IReadOnlyDictionary<string, JsonElement>?, CancellationToken, Task<ToolResult>> callTool,
        int maxRounds, CancellationToken ct, string? sessionId = null)
    {
        // A run is one user turn, so nothing here is ever elided (the browser's elideOldToolResults works at
        // turn boundaries) and the request prefix only grows: every round can read the previous one's cache.
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = systemPrompt },
            new JsonObject { ["role"] = "user", ["content"] = userPrompt },
        };
        var steps = new List<Step>();
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

                    var content = Clamp(result.Content);
                    messages.Add(ToolMessage(call.Id, content));
                    steps.Add(new Step("tool", Clamp(result.Content, 4000), call.Name, Clamp(call.Arguments, 4000), result.Account,
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
