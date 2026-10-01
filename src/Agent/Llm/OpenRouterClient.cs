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

    public sealed record Completion(
        string? Content, IReadOnlyList<ToolCall> ToolCalls, string? FinishReason, long PromptTokens, long CompletionTokens);

    public async Task<Completion> CompleteAsync(
        string apiKey, string model, JsonArray messages, JsonArray? tools, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = messages.DeepClone(),
            ["max_tokens"] = MaxTokens,
            ["stream"] = false,
        };
        if (tools is { Count: > 0 })
        {
            body["tools"] = tools.DeepClone();
            body["tool_choice"] = "auto";
        }

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

            var usage = json?["usage"];
            return new Completion(
                message?["content"]?.GetValueKind() == JsonValueKind.String ? message["content"]!.GetValue<string>() : null,
                calls,
                choice["finish_reason"]?.GetValue<string>() ?? (calls.Count > 0 ? "tool_calls" : null),
                usage?["prompt_tokens"]?.GetValue<long>() ?? 0,
                usage?["completion_tokens"]?.GetValue<long>() ?? 0);
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
