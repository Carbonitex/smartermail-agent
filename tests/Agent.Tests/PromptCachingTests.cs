using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using SmarterMailAgent.Llm;
using SmarterMailAgent.Server;

namespace SmarterMailAgent.Tests;

/// <summary>
/// Prompt caching on the server's OpenRouter requests (scheduled tasks): the same layout as the browser's
/// <c>buildRequestBody</c> — explicit system breakpoint plus top-level automatic caching for Anthropic, a plain
/// byte-stable body for everyone else — and cached-token accounting.
/// </summary>
public sealed class PromptCachingTests
{
    private static AgentLoop Loop(FakeLlm llm) => new(new OpenRouterClient(new HttpClient(llm),
        new ServerOptions { LlmBaseUrl = new Uri("https://llm.test/api/v1/") }, NullLogger<OpenRouterClient>.Instance));

    private static JsonArray Tools() =>
    [
        new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = "get_emails",
                ["description"] = "List mail.",
                ["parameters"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
            },
        },
    ];

    private static JsonArray Messages() =>
    [
        new JsonObject { ["role"] = "system", ["content"] = "You are the SmarterMail Agent." },
        new JsonObject { ["role"] = "user", ["content"] = "Summarise." },
    ];

    private static int CountBreakpoints(JsonNode node) =>
        node.ToJsonString().Split("\"cache_control\"").Length - 1;

    [Theory]
    [InlineData("anthropic/claude-haiku-5.5", true)]
    [InlineData("~anthropic/claude-sonnet-latest", true)]
    [InlineData("Anthropic/claude-opus-5.5", true)]
    [InlineData("openai/gpt-5.6", false)]
    [InlineData("google/gemini-2.5-pro", false)]
    [InlineData("openrouter/auto", false)]
    [InlineData("m", false)]
    public void Anthropic_models_are_recognised(string model, bool expected) =>
        Assert.Equal(expected, OpenRouterClient.IsAnthropicModel(model));

    [Fact]
    public void Anthropic_body_has_a_system_breakpoint_and_automatic_caching()
    {
        var messages = Messages();
        var body = OpenRouterClient.BuildBody("anthropic/claude-haiku-5.5", messages, Tools());

        Assert.Equal("ephemeral", body["cache_control"]!["type"]!.GetValue<string>());
        var system = body["messages"]![0]!;
        Assert.Equal("system", system["role"]!.GetValue<string>());
        var part = system["content"]!.AsArray().Single()!;
        Assert.Equal("text", part["type"]!.GetValue<string>());
        Assert.Equal("You are the SmarterMail Agent.", part["text"]!.GetValue<string>());
        Assert.Equal("ephemeral", part["cache_control"]!["type"]!.GetValue<string>());

        // Two breakpoints in all (of Anthropic's four); the user message and tools stay plain.
        Assert.Equal(2, CountBreakpoints(body));
        Assert.Equal("Summarise.", body["messages"]![1]!["content"]!.GetValue<string>());
        Assert.Equal("auto", body["tool_choice"]!.GetValue<string>());

        // The caller's history is untouched, so later rounds append to plain messages.
        Assert.Equal("You are the SmarterMail Agent.", messages[0]!["content"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("openai/gpt-5.6")]
    [InlineData("deepseek/deepseek-chat")]
    [InlineData("google/gemini-2.5-flash")]
    [InlineData("x-ai/grok-4")]
    public void Other_models_get_a_plain_body(string model)
    {
        var body = OpenRouterClient.BuildBody(model, Messages(), Tools());

        Assert.Equal(0, CountBreakpoints(body));
        Assert.Null(body["cache_control"]);
        Assert.Equal("You are the SmarterMail Agent.", body["messages"]![0]!["content"]!.GetValue<string>());
        Assert.Null(body["session_id"]);
    }

    [Fact]
    public void The_body_is_byte_stable_for_the_same_inputs()
    {
        var a = OpenRouterClient.BuildBody("anthropic/claude-haiku-5.5", Messages(), Tools(), "sma-task-run-1").ToJsonString();
        var b = OpenRouterClient.BuildBody("anthropic/claude-haiku-5.5", Messages(), Tools(), "sma-task-run-1").ToJsonString();
        Assert.Equal(a, b);
    }

    [Fact]
    public void Session_id_is_sent_and_capped()
    {
        Assert.Equal("sma-task-run-x", OpenRouterClient.BuildBody("m", Messages(), null, "sma-task-run-x")["session_id"]!.GetValue<string>());
        Assert.Equal(256, OpenRouterClient.BuildBody("m", Messages(), null, new string('x', 300))["session_id"]!.GetValue<string>().Length);
    }

    [Fact]
    public async Task A_run_sends_a_growing_prefix_and_sums_cached_tokens()
    {
        var llm = new FakeLlm()
            .Reply(new
            {
                choices = new[]
                {
                    new
                    {
                        finish_reason = "tool_calls",
                        message = new
                        {
                            role = "assistant",
                            content = (string?)null,
                            tool_calls = new[] { new { id = "call_0", type = "function", function = new { name = "get_emails", arguments = "{}" } } },
                        },
                    },
                },
                usage = new { prompt_tokens = 4000, completion_tokens = 10, cost = 0.004, prompt_tokens_details = new { cached_tokens = 0, cache_write_tokens = 3900 } },
            })
            .Reply(new
            {
                choices = new[] { new { finish_reason = "stop", message = new { role = "assistant", content = "Done." } } },
                usage = new { prompt_tokens = 4100, completion_tokens = 20, cost = 0.001, prompt_tokens_details = new { cached_tokens = 3900 } },
            });

        var result = await Loop(llm).RunAsync("key", "anthropic/claude-haiku-5.5", "system", "do it", Tools(),
            (_, _, _) => Task.FromResult(new AgentLoop.ToolResult("{\"items\":[]}", false, "me", false)), 15,
            CancellationToken.None, sessionId: "sma-task-run-r1");

        Assert.Equal("completed", result.Stop);
        Assert.Equal(8100, result.PromptTokens);
        Assert.Equal(3900, result.CachedTokens);
        Assert.Equal(3900, result.CacheWriteTokens);
        Assert.Equal(0.005, result.Cost, 6);

        Assert.Equal(2, llm.Requests.Count);
        foreach (var request in llm.Requests)
        {
            Assert.Equal("ephemeral", request["cache_control"]!["type"]!.GetValue<string>());
            Assert.Equal("sma-task-run-r1", request["session_id"]!.GetValue<string>());
            Assert.Equal(2, CountBreakpoints(request));
        }

        // Round two is round one plus the assistant's call and the tool's answer: same tools, same prefix.
        Assert.Equal(llm.Requests[0]["tools"]!.ToJsonString(), llm.Requests[1]["tools"]!.ToJsonString());
        var first = llm.Requests[0]["messages"]!.AsArray();
        var second = llm.Requests[1]["messages"]!.AsArray();
        Assert.Equal(first.Count + 2, second.Count);
        for (var i = 0; i < first.Count; i++)
            Assert.Equal(first[i]!.ToJsonString(), second[i]!.ToJsonString());
    }

    [Fact]
    public async Task Missing_or_odd_usage_fields_count_as_zero()
    {
        var llm = new FakeLlm().Reply(new
        {
            choices = new[] { new { finish_reason = "stop", message = new { role = "assistant", content = "Ok." } } },
            usage = new { prompt_tokens = 50, completion_tokens = 5, cost = (double?)null },
        });

        var result = await Loop(llm).RunAsync("key", "openai/gpt-5.6", "s", "u", [],
            (_, _, _) => Task.FromResult(new AgentLoop.ToolResult("", false, null, false)), 15, CancellationToken.None);

        Assert.Equal(50, result.PromptTokens);
        Assert.Equal(0, result.CachedTokens);
        Assert.Equal(0, result.Cost);
        Assert.Null(llm.Requests[0]["cache_control"]);
    }
}
