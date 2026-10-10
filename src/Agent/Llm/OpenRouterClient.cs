using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SmarterMailAgent.Server;

namespace SmarterMailAgent.Llm;

/// <summary>
/// OpenRouter's chat-completions API, non-streaming, for scheduled tasks: the only place the server
/// itself talks to an LLM. The interactive chat still runs in the browser with the browser's key.
/// </summary>
public sealed class OpenRouterClient(HttpClient http, ServerOptions options, ILogger<OpenRouterClient> logger)
{
    public const int MaxTokens = 8192;

    /// <param name="Code">Stable error code: <c>LLM_KEY_REJECTED</c>, <c>NO_CREDITS</c>, <c>RATE_LIMITED</c>, <c>LLM_UNAVAILABLE</c>, <c>LLM_BAD_REQUEST</c>, <c>LLM_ERROR</c>.</param>
    public sealed class LlmException(string code, string message, bool retryable) : Exception(message)
    {
        public string Code { get; } = code;
        public bool Retryable { get; } = retryable;
    }

    public sealed record ToolCall(string Id, string Name, string Arguments);

    /// <param name="CachedTokens">Prompt tokens read from the provider's cache (<c>prompt_tokens_details.cached_tokens</c>).</param>
    /// <param name="CacheWriteTokens">Prompt tokens written to the cache (<c>prompt_tokens_details.cache_write_tokens</c>).</param>
    /// <param name="Cost">What OpenRouter charged for the request (<c>usage.cost</c>, credits).</param>
    public sealed record Completion(
        string? Content, IReadOnlyList<ToolCall> ToolCalls, string? FinishReason, long PromptTokens, long CompletionTokens,
        long CachedTokens = 0, long CacheWriteTokens = 0, double Cost = 0);

    /// <param name="reasoningEffort">OpenRouter's <c>reasoning.effort</c>; only the analysis sub-agent sets it.</param>
    /// <param name="toolChoice">Overrides <c>tool_choice: auto</c> (the sub-agent's final answer sends <c>none</c>).</param>
    public async Task<Completion> CompleteAsync(
        string apiKey, string model, JsonArray messages, JsonArray? tools, CancellationToken ct, string? sessionId = null,
        string? reasoningEffort = null, string? toolChoice = null)
    {
        var body = BuildBody(model, messages, tools, sessionId, reasoningEffort, toolChoice);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await SendAsync(apiKey, body, ct);
            }
            catch (LlmException ex) when (ex.Retryable && attempt < 3)
            {
                logger.LogInformation("LLM call failed ({Code}); retrying.", ex.Code);
                await Task.Delay(TimeSpan.FromSeconds(attempt * 5), ct);
            }
        }
    }

    /// <summary>An Anthropic model on OpenRouter: <c>anthropic/…</c> or a <c>~anthropic/…</c> alias.</summary>
    public static bool IsAnthropicModel(string? model) =>
        model is not null && (model.StartsWith("anthropic/", StringComparison.OrdinalIgnoreCase) ||
                              model.StartsWith("~anthropic/", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The request body, with prompt caching laid out the way the browser does it (<c>buildRequestBody</c> in
    /// <c>wwwroot/js/llm.js</c>; see the comment there). Automatic-caching providers (OpenAI, DeepSeek, Grok,
    /// Gemini 2.5+…) need only a byte-stable prefix: the tool list is in catalog (name) order, the system
    /// prompt is built once per run, and messages are only appended. Anthropic needs <c>cache_control</c>: an
    /// explicit breakpoint on the system prompt (tools → system → messages, so it covers the tool schemas) plus
    /// the top-level automatic one, which follows the end of the conversation round by round. Two of Anthropic's
    /// four slots. Nobody else sees either. <paramref name="messages"/> is not modified.
    /// </summary>
    public static JsonObject BuildBody(
        string model, JsonArray messages, JsonArray? tools, string? sessionId = null, string? reasoningEffort = null, string? toolChoice = null)
    {
        var anthropic = IsAnthropicModel(model);
        var sent = (JsonArray)messages.DeepClone();
        if (anthropic)
        {
            var system = sent.OfType<JsonObject>().FirstOrDefault(m => m["role"]?.GetValue<string>() == "system");
            if (system?["content"] is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0)
            {
                system["content"] = new JsonArray(new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = text,
                    ["cache_control"] = Ephemeral(),
                });
            }
        }

        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = sent,
            ["max_tokens"] = MaxTokens,
            ["stream"] = false,
        };
        if (tools is { Count: > 0 })
        {
            body["tools"] = tools.DeepClone();
            body["tool_choice"] = toolChoice ?? "auto";
        }
        if (!string.IsNullOrEmpty(reasoningEffort))
            body["reasoning"] = new JsonObject { ["effort"] = reasoningEffort };
        if (anthropic)
            body["cache_control"] = Ephemeral();
        // Sticky provider routing from the first request, not only after the first cache hit.
        if (!string.IsNullOrEmpty(sessionId))
            body["session_id"] = sessionId.Length > 256 ? sessionId[..256] : sessionId;
        return body;
    }

    private static JsonObject Ephemeral() => new() { ["type"] = "ephemeral" };

    private static long Long(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.Number && v.TryGetValue<double>(out var d) ? (long)d : 0;

    private static double Double(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.Number && v.TryGetValue<double>(out var d) ? d : 0;

    private async Task<Completion> SendAsync(string apiKey, JsonObject body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(options.LlmBaseUrl, "chat/completions"))
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.Add("X-Title", "SmarterMail Agent");
        if (options.PublicOrigin is { } origin)
            request.Headers.Add("HTTP-Referer", origin.GetLeftPart(UriPartial.Authority));

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new LlmException("LLM_UNAVAILABLE", $"Could not reach the model provider: {ex.Message}", retryable: true);
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            JsonNode? json;
            try
            {
                json = JsonNode.Parse(text);
            }
            catch (JsonException)
            {
                json = null;
            }

            var errorMessage = json?["error"]?["message"]?.GetValue<string>();
            if (!response.IsSuccessStatusCode || json?["error"] is not null)
                throw Classify(response.StatusCode, errorMessage ?? $"HTTP {(int)response.StatusCode}");

            var choice = json?["choices"]?[0] ?? throw new LlmException("LLM_ERROR", "The model returned no choices.", retryable: true);
            var message = choice["message"];
            var calls = new List<ToolCall>();
            if (message?["tool_calls"] is JsonArray toolCalls)
            {
                var n = 0;
                foreach (var call in toolCalls)
                {
                    n++;
                    var name = call?["function"]?["name"]?.GetValue<string>();
                    if (string.IsNullOrEmpty(name))
                        continue;
                    calls.Add(new ToolCall(
                        call?["id"]?.GetValue<string>() is { Length: > 0 } id ? id : $"call_{n}_{name}",
                        name,
                        call?["function"]?["arguments"]?.GetValue<string>() ?? "{}"));
                }
            }

            // OpenRouter always sends usage now (`usage: { include: true }` is deprecated and a no-op).
            var usage = json?["usage"];
            return new Completion(
                message?["content"]?.GetValueKind() == JsonValueKind.String ? message["content"]!.GetValue<string>() : null,
                calls,
                choice["finish_reason"]?.GetValue<string>() ?? (calls.Count > 0 ? "tool_calls" : null),
                Long(usage?["prompt_tokens"]),
                Long(usage?["completion_tokens"]),
                Long(usage?["prompt_tokens_details"]?["cached_tokens"]),
                Long(usage?["prompt_tokens_details"]?["cache_write_tokens"]),
                Double(usage?["cost"]));
        }
    }

    private static LlmException Classify(HttpStatusCode status, string message) => (int)status switch
    {
        401 => new("LLM_KEY_REJECTED", "The OpenRouter key for scheduled tasks was rejected.", false),
        402 => new("NO_CREDITS", "The OpenRouter account for scheduled tasks is out of credits.", false),
        403 => new("LLM_BAD_REQUEST", $"The model provider refused the request: {message}", false),
        408 or 429 => new("RATE_LIMITED", $"The model provider is rate limiting: {message}", true),
        400 or 404 or 422 => new("LLM_BAD_REQUEST", $"The model provider rejected the request: {message}", false),
        >= 500 => new("LLM_UNAVAILABLE", $"The model provider failed: {message}", true),
        _ => new("LLM_ERROR", message, false),
    };
}
