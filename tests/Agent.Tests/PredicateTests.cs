using System.Diagnostics;
using System.Text.Json;
using SmarterMailAgent.Tasks.Triggers;

namespace SmarterMailAgent.Tests;

/// <summary>The predicate language of condition triggers: parsing, limits, semantics and evidence.</summary>
public sealed class PredicateTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static Predicate P(string json)
    {
        var p = Predicate.Parse(J(json), out var errors);
        Assert.True(p is not null, string.Join(" | ", errors));
        return p!;
    }

    private static IReadOnlyList<string> Errors(string json)
    {
        Assert.Null(Predicate.Parse(J(json), out var errors));
        Assert.NotEmpty(errors);
        return errors;
    }

    private static Predicate.Evaluation Eval(string predicate, string result, IReadOnlySet<string>? seen = null) =>
        P(predicate).Evaluate(J(result), seen, Now);

    private static bool True(string predicate, string result) => Eval(predicate, result).Value;

    // ------------------------------------------------------------------ paths

    [Theory]
    [InlineData("$", "$")]
    [InlineData("$.a.b", "$.a.b")]
    [InlineData("a.b", "$.a.b")]
    [InlineData("$[\"odd name\"][0]", "$[\"odd name\"][0]")]
    [InlineData("$.items[*].from-address", "$.items[*].from-address")]
    [InlineData("$['x']", "$.x")]
    public void Paths_parse_to_a_canonical_form(string text, string canonical)
    {
        Assert.True(JsonPath.TryParse(text, out var path, out var error), error);
        Assert.Equal(canonical, path.Text);
    }

    [Theory]
    [InlineData("$.a..b")]
    [InlineData("$.a[")]
    [InlineData("$.a[x]")]
    [InlineData("$.a[\"x]")]
    [InlineData("$ a")]
    [InlineData("$.a.b.c.d.e.f.g.h.i")]   // nine segments
    public void Bad_paths_are_refused(string text) => Assert.False(JsonPath.TryParse(text, out _, out _));

    // ------------------------------------------------------------------ compare

    [Theory]
    [InlineData("gt", 500, true)]
    [InlineData("gte", 501, true)]
    [InlineData("lt", 501, false)]
    [InlineData("lte", 501, true)]
    [InlineData("eq", 501, true)]
    [InlineData("ne", 501, false)]
    [InlineData("ne", 7, true)]
    public void Numbers_compare(string op, int value, bool expected) =>
        Assert.Equal(expected, True($"{{ \"path\": \"$.counts.waiting\", \"op\": \"{op}\", \"value\": {value} }}", "{\"counts\":{\"waiting\":501}}"));

    [Fact]
    public void Missing_paths_are_no_value_never_an_error()
    {
        var e = Eval("{ \"path\": \"$.nope.deeper\", \"op\": \"gt\", \"value\": 1 }", "{\"counts\":{}}");
        Assert.False(e.Value);
        Assert.Empty(e.Errors);
        Assert.False(True("{ \"path\": \"$.nope\", \"op\": \"ne\", \"value\": 1 }", "{}"));   // ne on nothing is false too
        Assert.False(True("{ \"path\": \"$[3]\", \"op\": \"eq\", \"value\": 1 }", "[1]"));
    }

    [Fact]
    public void Type_mismatches_are_false_both_ways()
    {
        const string result = "{\"n\":\"501\",\"s\":501,\"b\":true,\"z\":null}";
        Assert.False(True("{ \"path\": \"$.n\", \"op\": \"gt\", \"value\": 500 }", result));       // string vs number
        Assert.False(True("{ \"path\": \"$.n\", \"op\": \"eq\", \"value\": 501 }", result));
        Assert.False(True("{ \"path\": \"$.n\", \"op\": \"ne\", \"value\": 501 }", result));       // not "not equal" either
        Assert.False(True("{ \"path\": \"$.s\", \"op\": \"eq\", \"value\": \"501\" }", result));
        Assert.True(True("{ \"path\": \"$.b\", \"op\": \"eq\", \"value\": true }", result));
        Assert.True(True("{ \"path\": \"$.b\", \"op\": \"ne\", \"value\": false }", result));
        Assert.True(True("{ \"path\": \"$.z\", \"op\": \"eq\", \"value\": null }", result));
        Assert.True(True("{ \"path\": \"$.n\", \"op\": \"eq\", \"value\": \"501\" }", result));
    }

    [Fact]
    public void Wildcards_have_any_semantics_and_cover_object_values()
    {
        const string result = "{\"users\":[{\"n\":1},{\"n\":9},{\"n\":3}],\"byType\":{\"a\":2,\"b\":40}}";
        Assert.True(True("{ \"path\": \"$.users[*].n\", \"op\": \"gt\", \"value\": 8 }", result));
        Assert.False(True("{ \"path\": \"$.users[*].n\", \"op\": \"gt\", \"value\": 9 }", result));
        Assert.True(True("{ \"path\": \"$.byType[*]\", \"op\": \"gte\", \"value\": 40 }", result));

        var e = Eval("{ \"path\": \"$.users[*].n\", \"op\": \"gt\", \"value\": 2 }", result);
        Assert.Equal(["$.users[1].n", "$.users[2].n"], e.Matched.Select(m => m["path"]!.GetValue<string>()));
    }

    // ------------------------------------------------------------------ text, regex, dates, exists

    [Fact]
    public void Text_operators_are_case_insensitive_and_strings_only()
    {
        const string result = "{\"subject\":\"Your INVOICE 42\",\"n\":42}";
        Assert.True(True("{ \"path\": \"subject\", \"op\": \"contains\", \"value\": \"invoice\" }", result));
        Assert.True(True("{ \"path\": \"subject\", \"op\": \"startsWith\", \"value\": \"your\" }", result));
        Assert.True(True("{ \"path\": \"subject\", \"op\": \"endsWith\", \"value\": \" 42\" }", result));
        Assert.False(True("{ \"path\": \"n\", \"op\": \"contains\", \"value\": \"4\" }", result));
    }

    [Fact]
    public void Regex_is_case_insensitive()
    {
        Assert.True(True("{ \"path\": \"from\", \"op\": \"matches\", \"value\": \"@vendor\\\\.example$\" }", "{\"from\":\"Billing@Vendor.Example\"}"));
        Assert.False(True("{ \"path\": \"from\", \"op\": \"matches\", \"value\": \"@vendor\\\\.example$\" }", "{\"from\":\"billing@vendorXexample\"}"));
    }

    [Fact]
    public void A_catastrophic_pattern_runs_in_linear_time()
    {
        var input = new string('a', 50_000) + "!";
        var stopwatch = Stopwatch.StartNew();
        var e = Eval("{ \"path\": \"s\", \"op\": \"matches\", \"value\": \"^(a+)+$\" }", JsonSerializer.Serialize(new { s = input }));
        stopwatch.Stop();
        Assert.False(e.Value);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), stopwatch.Elapsed.ToString());
    }

    [Theory]
    [InlineData("(a)\\\\1")]          // backreference
    [InlineData("(?=a)a")]            // lookahead
    [InlineData("(?<=a)b")]           // lookbehind
    [InlineData("[")]                 // invalid
    public void Patterns_NonBacktracking_cannot_run_are_refused(string pattern) =>
        Assert.Contains(Errors($"{{ \"path\": \"s\", \"op\": \"matches\", \"value\": \"{pattern}\" }}"), e => e.Contains("pattern"));

    [Theory]
    [InlineData("2026-10-15T00:00:00Z", "daysUntilLt", 14, true)]
    [InlineData("2026-11-15T00:00:00Z", "daysUntilLt", 14, false)]
    [InlineData("2026-10-01T00:00:00Z", "daysUntilLt", 14, true)]          // already past counts
    [InlineData("2026-11-15T00:00:00Z", "daysUntilGt", 14, true)]
    [InlineData("2026-10-08T12:00:00+02:00", "daysAgoLt", 2, true)]
    [InlineData("2026-10-01", "daysAgoLt", 2, false)]                     // no offset: UTC
    [InlineData("2026-10-01", "daysAgoGt", 7, true)]
    [InlineData("2026-10-20T00:00:00Z", "daysAgoLt", 30, false)]          // future is not "ago"
    [InlineData("Thu, 15 Oct 2026 08:00:00 GMT", "daysUntilLt", 7, true)] // RFC 1123
    [InlineData("not a date", "daysUntilLt", 14, false)]
    [InlineData("", "daysUntilLt", 14, false)]
    public void Dates(string date, string op, int days, bool expected) =>
        Assert.Equal(expected, True($"{{ \"path\": \"d\", \"op\": \"{op}\", \"value\": {days} }}", JsonSerializer.Serialize(new { d = date })));

    [Fact]
    public void A_date_that_is_a_number_is_not_a_date() =>
        Assert.False(True("{ \"path\": \"d\", \"op\": \"daysUntilLt\", \"value\": 14 }", "{\"d\":1760000000}"));

    [Fact]
    public void Exists_ignores_null()
    {
        Assert.True(True("{ \"path\": \"$.error\", \"op\": \"exists\" }", "{\"error\":\"x\"}"));
        Assert.False(True("{ \"path\": \"$.error\", \"op\": \"exists\" }", "{\"error\":null}"));
        Assert.False(True("{ \"path\": \"$.error\", \"op\": \"exists\" }", "{}"));
    }

    // ------------------------------------------------------------------ count, logic

    [Fact]
    public void Count_with_where_and_its_evidence()
    {
        const string result = """
            {"certificates":[
              {"name":"a","expiration":"2026-10-12T00:00:00Z"},
              {"name":"b","expiration":"2027-01-01T00:00:00Z"},
              {"name":"c","expiration":"2026-10-20T00:00:00Z"}]}
            """;
        const string predicate = """
            { "count": { "items": "$.certificates[*]", "where": { "path": "expiration", "op": "daysUntilLt", "value": 14 } }, "op": "gte", "value": 2 }
            """;
        var e = Eval(predicate, result);
        Assert.True(e.Value);
        Assert.Equal(["a", "c"], e.Matched.Select(m => m["value"]!["name"]!.GetValue<string>()));
        Assert.False(Eval(predicate.Replace("\"value\": 2", "\"value\": 3"), result).Value);
        Assert.True(True("{ \"count\": { \"items\": \"$.certificates[*]\" }, \"op\": \"eq\", \"value\": 3 }", result));
        Assert.True(True("{ \"count\": { \"items\": \"$.nothing[*]\" }, \"op\": \"eq\", \"value\": 0 }", result));
    }

    [Fact]
    public void All_any_not()
    {
        const string result = "{\"a\":1,\"b\":2}";
        Assert.True(True("{ \"all\": [ { \"path\": \"a\", \"op\": \"eq\", \"value\": 1 }, { \"path\": \"b\", \"op\": \"eq\", \"value\": 2 } ] }", result));
        Assert.False(True("{ \"all\": [ { \"path\": \"a\", \"op\": \"eq\", \"value\": 1 }, { \"path\": \"b\", \"op\": \"eq\", \"value\": 3 } ] }", result));
        Assert.True(True("{ \"any\": [ { \"path\": \"a\", \"op\": \"eq\", \"value\": 9 }, { \"path\": \"b\", \"op\": \"eq\", \"value\": 2 } ] }", result));
        Assert.True(True("{ \"not\": { \"path\": \"a\", \"op\": \"eq\", \"value\": 9 } }", result));

        // Evidence of an "any" comes from its true branches only, in order.
        var e = Eval("{ \"any\": [ { \"path\": \"b\", \"op\": \"eq\", \"value\": 2 }, { \"path\": \"a\", \"op\": \"eq\", \"value\": 9 }, { \"path\": \"a\", \"op\": \"eq\", \"value\": 1 } ] }", result);
        Assert.Equal(["$.b", "$.a"], e.Matched.Select(m => m["path"]!.GetValue<string>()));
        Assert.Empty(Eval("{ \"not\": { \"path\": \"a\", \"op\": \"eq\", \"value\": 9 } }", result).Matched);
    }

    [Fact]
    public void Evaluation_is_deterministic()
    {
        const string predicate = "{ \"count\": { \"items\": \"$.x[*]\", \"where\": { \"path\": \"v\", \"op\": \"gt\", \"value\": 0 } }, \"op\": \"gte\", \"value\": 1 }";
        var result = JsonSerializer.Serialize(new { x = Enumerable.Range(0, 50).Select(i => new { v = i % 3, id = i }) });
        var first = Eval(predicate, result).Matched.Select(m => m.ToJsonString()).ToList();
        var second = Eval(predicate, result).Matched.Select(m => m.ToJsonString()).ToList();
        Assert.Equal(first, second);
    }

    [Fact]
    public void Describe_renders_the_definition()
    {
        Assert.Equal("$.counts.waiting > 500", P("{ \"path\": \"$.counts.waiting\", \"op\": \"gt\", \"value\": 500 }").Describe());
        Assert.Equal("the number of items in $.users[*] ≥ 1", P("{ \"count\": { \"items\": \"$.users[*]\" }, \"op\": \"gte\", \"value\": 1 }").Describe());
        Assert.Contains("a new item in $.emails[*] where ($.from matches /@x$/ or $.subject contains \"invoice\")",
            P("""{ "new": { "items": "$.emails[*]", "where": { "any": [ { "path": "from", "op": "matches", "value": "@x$" }, { "path": "subject", "op": "contains", "value": "invoice" } ] } } }""").Describe());
    }

    // ------------------------------------------------------------------ new

    private const string NewMail = """{ "new": { "items": "$.emails[*]", "key": "uid", "where": { "path": "subject", "op": "contains", "value": "invoice" } } }""";

    [Fact]
    public void New_items_by_key_and_absorption_of_non_matching_ones()
    {
        const string first = """{"emails":[{"uid":1,"subject":"invoice 1"},{"uid":2,"subject":"hello"}]}""";
        var e1 = Eval(NewMail, first);
        Assert.True(e1.Value);                       // with nothing seen, every matching item is new
        Assert.Equal(2, e1.Keys.Count);              // both items are recorded, matching or not
        Assert.Single(e1.NewKeys);

        var seen = e1.Keys.ToHashSet();
        Assert.False(Eval(NewMail, first, seen).Value);

        var e3 = Eval(NewMail, """{"emails":[{"uid":3,"subject":"Invoice 2"},{"uid":1,"subject":"invoice 1"},{"uid":4,"subject":"other"}]}""", seen);
        Assert.True(e3.Value);
        Assert.Single(e3.NewKeys);
        Assert.Equal(3, e3.Matched.Single()["value"]!["uid"]!.GetValue<int>());
    }

    [Fact]
    public void New_items_fall_back_to_an_id_then_date_and_subject()
    {
        const string predicate = """{ "new": { "items": "$.emails[*]" } }""";
        var byId = Eval(predicate, """{"emails":[{"messageId":"<a@x>","subject":"s"}]}""");
        var sameIdOtherSubject = Eval(predicate, """{"emails":[{"messageId":"<a@x>","subject":"changed"}]}""", byId.Keys.ToHashSet());
        Assert.False(sameIdOtherSubject.Value);

        var byDate = Eval(predicate, """{"emails":[{"dateSent":"2026-10-09T10:00:00Z","subject":"s","size":1}]}""");
        Assert.False(Eval(predicate, """{"emails":[{"dateSent":"2026-10-09T10:00:00Z","subject":"s","size":2}]}""", byDate.Keys.ToHashSet()).Value);
        Assert.True(Eval(predicate, """{"emails":[{"dateSent":"2026-10-09T10:00:00Z","subject":"t","size":1}]}""", byDate.Keys.ToHashSet()).Value);

        // A key path the item lacks falls back the same way.
        var fallback = Eval("""{ "new": { "items": "$.emails[*]", "key": "nope" } }""", """{"emails":[{"uid":5}]}""");
        Assert.True(fallback.Value);
    }

    [Fact]
    public void New_is_flagged()
    {
        Assert.True(P(NewMail).UsesNew);
        Assert.False(P("{ \"path\": \"a\", \"op\": \"exists\" }").UsesNew);
    }

    // ------------------------------------------------------------------ limits

    [Fact]
    public void Node_count_is_limited()
    {
        var leaves = string.Join(",", Enumerable.Range(0, Predicate.MaxNodes).Select(_ => "{ \"path\": \"a\", \"op\": \"exists\" }"));
        Assert.Contains(Errors($"{{ \"any\": [ {leaves} ] }}"), e => e.Contains("parts"));
        var fewer = string.Join(",", Enumerable.Range(0, Predicate.MaxNodes - 1).Select(_ => "{ \"path\": \"a\", \"op\": \"exists\" }"));
        P($"{{ \"any\": [ {fewer} ] }}");
    }

    [Fact]
    public void Depth_is_limited()
    {
        var json = "{ \"path\": \"a\", \"op\": \"exists\" }";
        for (var i = 0; i < Predicate.MaxDepth - 1; i++)
            json = $"{{ \"not\": {json} }}";
        P(json);
        Assert.Contains(Errors($"{{ \"not\": {json} }}"), e => e.Contains("deeper"));
    }

    [Fact]
    public void Path_and_regex_lengths_are_limited()
    {
        Assert.Contains(Errors("{ \"path\": \"a.b.c.d.e.f.g.h.i\", \"op\": \"exists\" }"), e => e.Contains("segments"));
        Assert.Contains(Errors($"{{ \"path\": \"a\", \"op\": \"matches\", \"value\": \"{new string('a', Predicate.MaxRegexLength + 1)}\" }}"), e => e.Contains("pattern"));
        P($"{{ \"path\": \"a\", \"op\": \"matches\", \"value\": \"{new string('a', Predicate.MaxRegexLength)}\" }}");
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{ \"path\": \"a\" }")]
    [InlineData("{ \"path\": \"a\", \"op\": \"gt\", \"value\": \"x\" }")]
    [InlineData("{ \"path\": \"a\", \"op\": \"eq\", \"value\": [1] }")]
    [InlineData("{ \"path\": \"a\", \"op\": \"exists\", \"value\": 1 }")]
    [InlineData("{ \"path\": \"a\", \"op\": \"bogus\", \"value\": 1 }")]
    [InlineData("{ \"path\": \"a\", \"op\": \"eq\", \"value\": 1, \"extra\": true }")]
    [InlineData("{ \"any\": [] }")]
    [InlineData("{ \"count\": { \"items\": \"$.a[*]\" }, \"op\": \"gt\" }")]
    [InlineData("{ \"new\": { \"where\": { \"path\": \"a\", \"op\": \"exists\" } } }")]
    [InlineData("{ \"path\": \"a\", \"op\": \"daysUntilLt\", \"value\": \"14\" }")]
    public void Malformed_conditions_are_refused(string json) => Errors(json);

    [Fact]
    public void Errors_name_the_node()
    {
        var errors = Errors("{ \"all\": [ { \"path\": \"a\", \"op\": \"exists\" }, { \"path\": \"b\", \"op\": \"gt\", \"value\": \"x\" } ] }");
        Assert.Contains(errors, e => e.StartsWith("when.all[1].value", StringComparison.Ordinal));
    }

    [Fact]
    public void The_work_budget_fails_the_evaluation_instead_of_spinning()
    {
        // 600 x 600 nested counts: far beyond the budget.
        var result = JsonSerializer.Serialize(new { a = Enumerable.Range(0, 600).ToArray(), b = Enumerable.Range(0, 600).ToArray() });
        var e = Eval("""
            { "count": { "items": "$.a[*]", "where": { "count": { "items": "$.b[*]" }, "op": "gte", "value": 0 } }, "op": "gte", "value": 1 }
            """.Replace("\"items\": \"$.b[*]\"", "\"items\": \"$\""), result);
        Assert.False(e.Failed);   // the item-relative path is cheap

        var p = P("""{ "count": { "items": "$.a[*]", "where": { "not": { "path": "$", "op": "exists" } } }, "op": "gte", "value": 0 }""");
        Assert.False(p.Evaluate(J(result), null, Now).Failed);

        var big = JsonSerializer.Serialize(Enumerable.Range(0, 400).Select(_ => Enumerable.Range(0, 400).ToArray()));
        var heavy = P("""{ "count": { "items": "$[*][*]", "where": { "path": "$", "op": "gt", "value": -1 } }, "op": "gte", "value": 0 }""");
        Assert.True(heavy.Evaluate(J(big), null, Now).Failed);
    }

    // ------------------------------------------------------------------ evidence clamping

    [Fact]
    public void Evidence_is_clamped_in_count_size_and_total()
    {
        var items = Enumerable.Range(0, 40).Select(i => new { id = i, body = new string('x', 5000), nested = new { deep = 1 } });
        var e = Eval("{ \"count\": { \"items\": \"$.items[*]\" }, \"op\": \"gte\", \"value\": 1 }", JsonSerializer.Serialize(new { items }));
        Assert.True(e.Value);
        Assert.True(e.Truncated);
        Assert.True(e.Matched.Count <= Predicate.MaxEvidenceItems);
        Assert.All(e.Matched, m => Assert.True(m.ToJsonString().Length <= Predicate.MaxEvidenceItemChars));
        Assert.True(e.Matched.Sum(m => m.ToJsonString().Length) <= Predicate.MaxEvidenceChars);

        var first = e.Matched[0];
        Assert.True(first["clamped"]!.GetValue<bool>());
        Assert.Equal(0, first["value"]!["id"]!.GetValue<int>());               // scalar fields survive
        Assert.Equal("…", first["value"]!["nested"]!.GetValue<string>());     // nested ones do not
    }
}
