using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using SmarterMailAgent.Llm.Artifacts;

namespace SmarterMailAgent.Tests;

/// <summary>
/// The C# operators against the browser's: same fixture (tests/Agent.Tests/Fixtures/artifact-ops.json, its
/// expected values written by the JS operators), same output byte for byte.
/// </summary>
public sealed class ArtifactOperatorTests
{
    private static readonly Lazy<JsonElement> Fixture = new(() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "tests", "Agent.Tests", "Fixtures", "artifact-ops.json"))).RootElement);

    private static readonly Lazy<Dictionary<string, ArtifactData>> Data = new(() =>
    {
        var f = Fixture.Value;
        var handle = f.GetProperty("meta").GetProperty("handle").GetString();
        var tool = f.GetProperty("meta").GetProperty("tool").GetString();
        return new Dictionary<string, ArtifactData>
        {
            ["text"] = ArtifactOperators.Prepare("text", f.GetProperty("text").GetString()!, handle, tool),
            ["records"] = ArtifactOperators.Prepare("records", f.GetProperty("records").GetString()!, handle, tool),
        };
    });

    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        foreach (var c in Fixture.Value.GetProperty("cases").EnumerateArray())
            data.Add(c.GetProperty("name").GetString()!);
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Matches_the_browser(string name)
    {
        var c = Fixture.Value.GetProperty("cases").EnumerateArray().Single(x => x.GetProperty("name").GetString() == name);
        var args = c.GetProperty("args").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
        string actual;
        try
        {
            actual = ArtifactOperators.Run(Data.Value[c.GetProperty("data").GetString()!], c.GetProperty("op").GetString()!, args);
        }
        catch (OpException ex)
        {
            actual = $"Error: {ex.Message}";
        }
        Assert.Equal(c.GetProperty("expected").GetString(), actual);
    }

    [Fact]
    public void The_stub_matches_the_browser()
    {
        var stub = Fixture.Value.GetProperty("stub");
        var store = new ArtifactStore(threshold: 1000);
        var kept = store.Capture("search_log_files", "{\"type\":\"smtpLog\"}", stub.GetProperty("content").GetString()!, isError: false);
        Assert.NotNull(kept);
        Assert.Equal(stub.GetProperty("expected").GetString(), kept.Value.Stub);
    }

    [Fact]
    public void Patterns_are_linear_time_with_a_timeout()
    {
        var re = ArtifactOperators.Compile("a+", "i");
        Assert.True(re.Options.HasFlag(RegexOptions.NonBacktracking));
        Assert.True(re.Options.HasFlag(RegexOptions.IgnoreCase));
        Assert.Equal(ArtifactOperators.MatchTimeout, re.MatchTimeout);

        // The pattern that freezes a backtracking engine is linear here.
        var data = ArtifactOperators.Prepare("text", new string('a', 50_000) + "!\nok\n");
        var clock = Stopwatch.StartNew();
        var output = ArtifactOperators.Run(data, "artifact_grep", Args(new { pattern = "^(a+)+$" }));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"took {clock.Elapsed}");
        Assert.StartsWith("0 matching lines of 2", output);
    }

    [Theory]
    [InlineData("(a)\\1")]        // backreference: NonBacktracking refuses it
    [InlineData("(?<=a)b")]       // lookbehind
    [InlineData("(")]
    public void Unsupported_or_invalid_patterns_are_model_readable_errors(string pattern)
    {
        var data = ArtifactOperators.Prepare("text", "ab\n");
        var ex = Assert.Throws<OpException>(() => ArtifactOperators.Run(data, "artifact_grep", Args(new { pattern })));
        Assert.StartsWith("Invalid pattern", ex.Message);
    }

    [Fact]
    public void Unknown_operators_and_long_patterns_are_refused()
    {
        var data = ArtifactOperators.Prepare("text", "x\n");
        Assert.Throws<OpException>(() => ArtifactOperators.Run(data, "run_shell", Args(new { })));
        Assert.Throws<OpException>(() => ArtifactOperators.Run(data, "artifact_grep", Args(new { pattern = new string('x', 2000) })));
    }

    [Fact]
    public void Tools_are_sorted_and_named_like_the_browser()
    {
        var names = ArtifactOperators.Tools().Select(t => t!["function"]!["name"]!.GetValue<string>()).ToList();
        Assert.Equal(ArtifactOperators.Names, names);
        Assert.Equal(names.Order(StringComparer.Ordinal), names);
    }

    [Fact]
    public void Store_keeps_results_over_the_threshold_and_evicts_the_least_recently_used()
    {
        var store = new ArtifactStore(threshold: 10, maxTotal: 250, maxEach: 1000);
        Assert.Null(store.Capture("t", "{}", "short", isError: false));
        Assert.Null(store.Capture("t", "{}", new string('e', 100), isError: true));
        var r1 = store.Capture("t", "{}", new string('a', 100), false)!.Value.Artifact.Handle;
        var r2 = store.Capture("t", "{}", new string('b', 100), false)!.Value.Artifact.Handle;
        Assert.NotNull(store.Get(r1));
        var r3 = store.Capture("t", "{}", new string('c', 100), false)!.Value.Artifact.Handle;
        Assert.Null(store.Get(r2));
        Assert.True(store.Issued(r2));
        Assert.NotNull(store.Get(r1));
        Assert.NotNull(store.Get(r3));
        Assert.False(store.Issued("r9"));
        Assert.True(store.TotalChars <= 250);
    }

    [Fact]
    public void Store_truncates_an_oversize_result_at_a_line_end()
    {
        var store = new ArtifactStore(threshold: 10, maxEach: 1000);
        var body = string.Concat(Enumerable.Range(0, 100).Select(i => $"line {i} of the log\n"));
        var (stub, artifact) = store.Capture("search_log_files", "{}", body, false)!.Value;
        Assert.True(artifact.Truncated);
        Assert.True(artifact.Chars <= 1000);
        Assert.EndsWith("\n", artifact.Body);
        Assert.Contains("\"truncated\":\"only the first", stub);
    }

    internal static IReadOnlyDictionary<string, JsonElement> Args(object value) =>
        JsonSerializer.SerializeToElement(value).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());

    internal static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SmarterMail.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repo root not found.");
    }
}
