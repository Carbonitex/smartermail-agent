using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SmarterMailAgent.Llm.Artifacts;

/// <summary>A refusal the model can act on: a bad pattern, a missing argument, an unknown operator.</summary>
public sealed class OpException(string message) : Exception(message);

/// <summary>An artifact split for the operators: lines, and for JSON records the records themselves.</summary>
public sealed class ArtifactData
{
    public required string Kind { get; init; }
    public required IReadOnlyList<string> Lines { get; init; }
    public IReadOnlyList<JsonNode?>? Records { get; init; }
    public required int Chars { get; init; }
    public string? Handle { get; init; }
    public string? Tool { get; init; }
    public bool Truncated { get; init; }
    public int OriginalChars { get; init; }
}

/// <summary>
/// The deterministic operators the analysis sub-agent runs over one artifact: a port of
/// <c>wwwroot/js/artifact-ops.js</c> for scheduled tasks, which must produce the same output byte for byte
/// (the shared fixture <c>tests/Agent.Tests/Fixtures/artifact-ops.json</c> checks both). No code from the
/// model runs: it only picks an operator and its arguments. Patterns compile with
/// <see cref="RegexOptions.NonBacktracking"/> (linear time; no backreferences or lookarounds) and a
/// 250 ms match timeout, so a hostile pattern costs at most that per line. Every output is capped at
/// <see cref="OutputCap"/> characters and says how much it left out.
/// </summary>
public static class ArtifactOperators
{
    public const int OutputCap = 8000;
    public const int LineClip = 500;
    public const int ValueClip = 200;
    public const int MaxPattern = 1000;
    public static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>JSON as <c>JSON.stringify</c> writes it, for record lines and stubs (no HTML-safe escaping).</summary>
    public static readonly JsonSerializerOptions JsJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly Regex LineTimeRegex = new(@"^(?:[0-9]{4}-[0-9]{2}-[0-9]{2}[T ])?([0-9]{2}):([0-9]{2}):([0-9]{2})", RegexOptions.CultureInvariant);
    private static readonly Regex ClockRegex = new(@"^([0-9]{1,2}):([0-9]{2})(?::([0-9]{2}))?$", RegexOptions.CultureInvariant);
    private static readonly Regex GroupNameRegex = new(@"\(\?<([A-Za-z_][A-Za-z0-9_]*)>", RegexOptions.CultureInvariant);
    private static readonly Regex SmarterMailLog = new(@"^[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]+)? \[[^\]]+\]", RegexOptions.CultureInvariant);
    private static readonly Regex TimedLine = new(@"^[0-9]{2}:[0-9]{2}:[0-9]{2}", RegexOptions.CultureInvariant);
    private static readonly Regex IsoLine = new(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}[T ][0-9]{2}:[0-9]{2}:[0-9]{2}", RegexOptions.CultureInvariant);

    public static readonly string[] Names =
        ["artifact_between", "artifact_count", "artifact_fields", "artifact_grep", "artifact_info", "artifact_session", "artifact_slice"];

    /// <summary>The operators as OpenAI function tools, sorted by name (the same definitions as the browser).</summary>
    public static JsonArray Tools()
    {
        static JsonObject Prop(string type, string description) => new() { ["type"] = type, ["description"] = description };
        static JsonObject Tool(string name, string description, JsonObject properties, params string[] required)
        {
            var parameters = new JsonObject { ["type"] = "object", ["properties"] = properties };
            if (required.Length > 0)
                parameters["required"] = new JsonArray(required.Select(r => (JsonNode)r).ToArray());
            return new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = name, ["description"] = description, ["parameters"] = parameters },
            };
        }

        return
        [
            Tool("artifact_between",
                "Lines whose leading time of day (HH:mm:ss, or the time in an ISO date-time) falls between start and end, inclusive. Lines without a time belong to the line above. start > end wraps past midnight.",
                new JsonObject
                {
                    ["start"] = Prop("string", "HH:mm or HH:mm:ss"),
                    ["end"] = Prop("string", "HH:mm or HH:mm:ss (HH:mm includes that whole minute)"),
                    ["limit"] = Prop("integer", "Lines to show, default 100, at most 500"),
                }, "start", "end"),
            Tool("artifact_count",
                "Count the lines matching a regular expression. With \"by\", group the count by a capture group (number or name) or, for JSON records, by a field path (a.b), and list the most frequent values.",
                new JsonObject
                {
                    ["pattern"] = Prop("string", "Regular expression (no backreferences or lookarounds); \"\" matches every line"),
                    ["by"] = Prop("string", "Capture group number or name, or a record field path"),
                    ["flags"] = Prop("string", "Any of i (ignore case), m, s"),
                    ["top"] = Prop("integer", "Values to list, default 20, at most 200"),
                }, "pattern"),
            Tool("artifact_fields",
                "Extract the named capture groups (?<name>…) of a regular expression from every matching line: distinct counts per field plus the first rows as a table.",
                new JsonObject
                {
                    ["pattern"] = Prop("string", "Regular expression with at least one named group"),
                    ["flags"] = Prop("string", "Any of i, m, s"),
                    ["limit"] = Prop("integer", "Rows to show, default 20, at most 200"),
                }, "pattern"),
            Tool("artifact_grep",
                "Lines matching a regular expression, with their line numbers (#n). The total count is always reported, even when not every line is shown.",
                new JsonObject
                {
                    ["pattern"] = Prop("string", "Regular expression (no backreferences or lookarounds)"),
                    ["flags"] = Prop("string", "Any of i (ignore case), m, s"),
                    ["context"] = Prop("integer", "Lines of context around each match, 0-5, default 0"),
                    ["limit"] = Prop("integer", "Matches to show, default 50, at most 500"),
                    ["invert"] = Prop("boolean", "Show the lines that do NOT match"),
                }, "pattern"),
            Tool("artifact_info", "Size, line count, detected line format, time range and (for JSON records) field names.", new JsonObject()),
            Tool("artifact_session",
                "Every line carrying one session or message id, in order: SmarterMail log lines tag a connection as [id].",
                new JsonObject
                {
                    ["id"] = Prop("string", "The id, with or without the brackets"),
                    ["limit"] = Prop("integer", "Lines to show, default 200, at most 500"),
                }, "id"),
            Tool("artifact_slice",
                "Lines by number: from (1 = first line; negative counts from the end, -20 = the last 20) and count.",
                new JsonObject
                {
                    ["from"] = Prop("integer", "1-based line number; negative counts from the end"),
                    ["count"] = Prop("integer", "Lines to show, default 50, at most 500"),
                }, "from"),
        ];
    }

    /* ------------------------------------------------------------------ prepare */

    public static ArtifactData Prepare(string kind, string body, string? handle = null, string? tool = null, bool truncated = false, int originalChars = 0)
    {
        body ??= "";
        List<JsonNode?>? records = null;
        if (kind == "records")
        {
            try
            {
                var parsed = JsonNode.Parse(body);
                records = parsed is JsonArray array ? array.ToList() : [parsed];
            }
            catch (JsonException)
            {
                records = null;
            }
        }

        var lines = records is not null
            ? records.Select(r => r?.ToJsonString(JsJson) ?? "null").ToList()
            : SplitLines(body);
        return new ArtifactData
        {
            Kind = records is not null ? "records" : "text",
            Lines = lines,
            Records = records,
            Chars = body.Length,
            Handle = handle,
            Tool = tool,
            Truncated = truncated,
            OriginalChars = originalChars,
        };
    }

    /// <summary>\n-separated lines, a trailing \r dropped from each, no empty last line.</summary>
    public static List<string> SplitLines(string text)
    {
        if (string.IsNullOrEmpty(text))
            return [];
        var lines = text.Split('\n').ToList();
        if (lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].EndsWith('\r'))
                lines[i] = lines[i][..^1];
        }
        return lines;
    }

    /* ---------------------------------------------------------------- dispatch */

    /// <summary>Runs one operator; returns its output. Throws <see cref="OpException"/> for bad arguments.</summary>
    public static string Run(ArtifactData data, string name, IReadOnlyDictionary<string, JsonElement>? args)
    {
        args ??= new Dictionary<string, JsonElement>();
        return name switch
        {
            "artifact_info" => Info(data),
            "artifact_slice" => Slice(data, args),
            "artifact_grep" => Grep(data, args),
            "artifact_count" => Count(data, args),
            "artifact_fields" => Fields(data, args),
            "artifact_between" => Between(data, args),
            "artifact_session" => Session(data, args),
            _ => throw new OpException($"Unknown operator {name}. Use one of: {string.Join(", ", Names)}."),
        };
    }

    /* ------------------------------------------------------------------ helpers */

    private static string Num(long n) => n.ToString(CultureInfo.InvariantCulture);

    private static int Int(IReadOnlyDictionary<string, JsonElement> args, string key, int fallback, int min, int max)
    {
        if (!args.TryGetValue(key, out var v))
            return fallback;
        double n;
        if (v.ValueKind == JsonValueKind.Number)
            n = v.GetDouble();
        else if (v.ValueKind == JsonValueKind.String && v.GetString()!.Trim() is { Length: > 0 } s &&
                 double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            n = parsed;
        else
            return fallback;
        if (double.IsNaN(n) || double.IsInfinity(n))
            return fallback;
        return (int)Math.Min(max, Math.Max(min, Math.Truncate(n)));
    }

    private static string Str(IReadOnlyDictionary<string, JsonElement> args, string key) =>
        !args.TryGetValue(key, out var v) ? "" : v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Null or JsonValueKind.Undefined => "",
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => v.GetRawText(),
        };

    private static bool Bool(IReadOnlyDictionary<string, JsonElement> args, string key) =>
        args.TryGetValue(key, out var v) && (v.ValueKind == JsonValueKind.True || (v.ValueKind == JsonValueKind.String && v.GetString() == "true"));

    public static string Clip(string s, int max) => s.Length > max ? $"{s[..max]}…[+{Num(s.Length - max)} chars]" : s;

    private static string LineItem(ArtifactData data, int i, char sep = ':') => $"#{Num(i + 1)}{sep} {Clip(data.Lines[i], LineClip)}";

    private static string Emit(string header, IReadOnlyList<string> items, string noun, int? total = null, string reason = "", IReadOnlyList<int>? units = null)
    {
        var output = new StringBuilder(header);
        var shown = 0;
        var capped = false;
        for (var i = 0; i < items.Count; i++)
        {
            if (output.Length + 1 + items[i].Length > OutputCap)
            {
                capped = true;
                break;
            }
            output.Append('\n').Append(items[i]);
            shown += units is null ? 1 : units[i];
        }
        var left = (total ?? items.Count) - shown;
        if (left > 0)
        {
            var why = capped ? $"output cap {Num(OutputCap)} chars" : reason.Length > 0 ? reason : "limit";
            output.Append($"\n… {Num(left)} more {noun} not shown ({why})");
        }
        return output.ToString();
    }

    /// <summary>The pattern as a linear-time .NET regex; only the i, m and s flags are honoured.</summary>
    public static Regex Compile(string pattern, string flags)
    {
        if (pattern.Length > MaxPattern)
            throw new OpException($"Invalid pattern: longer than {Num(MaxPattern)} characters.");
        var options = RegexOptions.NonBacktracking | RegexOptions.CultureInvariant;
        if (flags.Contains('i')) options |= RegexOptions.IgnoreCase;
        if (flags.Contains('m')) options |= RegexOptions.Multiline;
        if (flags.Contains('s')) options |= RegexOptions.Singleline;
        try
        {
            return new Regex(pattern, options, MatchTimeout);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            throw new OpException($"Invalid pattern: {ex.Message}");
        }
    }

    private static T Timed<T>(Func<T> match)
    {
        try
        {
            return match();
        }
        catch (RegexMatchTimeoutException)
        {
            throw new OpException("The pattern took too long on one line and was stopped. Use a simpler, more specific pattern.");
        }
    }

    /// <summary>Named groups in pattern order, read from the pattern text (the same rule as the browser).</summary>
    public static List<string> GroupNames(string pattern)
    {
        var names = new List<string>();
        foreach (Match m in GroupNameRegex.Matches(pattern))
        {
            if (!names.Contains(m.Groups[1].Value))
                names.Add(m.Groups[1].Value);
        }
        return names;
    }

    /// <summary>Seconds since midnight from a line's leading time, or null.</summary>
    public static int? LineTime(string line)
    {
        var m = LineTimeRegex.Match(line);
        if (!m.Success)
            return null;
        int h = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), mi = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
            s = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        if (h > 23 || mi > 59 || s > 59)
            return null;
        return h * 3600 + mi * 60 + s;
    }

    private static int? ParseClock(string value, bool isEnd)
    {
        var m = ClockRegex.Match(value.Trim());
        if (!m.Success)
            return null;
        int h = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), mi = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        var s = m.Groups[3].Success ? int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) : isEnd ? 59 : 0;
        if (h > 23 || mi > 59 || s > 59)
            return null;
        return h * 3600 + mi * 60 + s;
    }

    private static string Clock(int sec) =>
        $"{sec / 3600:00}:{sec / 60 % 60:00}:{sec % 60:00}";

    private static string? FieldValue(JsonNode? record, string path)
    {
        var current = record;
        foreach (var part in path.Split('.'))
        {
            switch (current)
            {
                case JsonObject obj when obj.TryGetPropertyValue(part, out var next):
                    current = next;
                    break;
                case JsonArray array when int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index < array.Count:
                    current = array[index];
                    break;
                default:
                    return null;
            }
        }
        if (current is null)
            return "null";
        return current is JsonValue value && value.GetValueKind() == JsonValueKind.String
            ? value.GetValue<string>()
            : current.ToJsonString(JsJson);
    }

    private static int ByCount(KeyValuePair<string, int> a, KeyValuePair<string, int> b) =>
        b.Value != a.Value ? b.Value.CompareTo(a.Value) : string.CompareOrdinal(a.Key, b.Key);

    /* ---------------------------------------------------------------- operators */

    private static string Info(ArtifactData data)
    {
        var n = data.Lines.Count;
        var header = $"artifact {data.Handle ?? "?"} from {data.Tool ?? "a tool"}: {Num(n)} {(data.Kind == "records" ? "records" : "lines")}, {Num(data.Chars)} chars ({data.Kind})";
        var items = new List<string>();
        if (data.Truncated)
            items.Add($"truncated: only the first {Num(data.Chars)} of {(data.OriginalChars > 0 ? Num(data.OriginalChars) : "?")} chars were kept");
        items.Add($"line format: {DetectFormat(data)}");
        int? first = null, last = null;
        foreach (var line in data.Lines)
        {
            if (LineTime(line) is not { } t)
                continue;
            first ??= t;
            last = t;
        }
        if (first is { } f && last is { } l)
            items.Add($"time range: {Clock(f)} to {Clock(l)} (first and last timestamped lines)");
        if (data.Records is { } records)
        {
            var keys = new List<string>();
            foreach (var r in records.Take(500))
            {
                if (r is not JsonObject obj)
                    continue;
                foreach (var (k, _) in obj)
                    if (!keys.Contains(k))
                        keys.Add(k);
            }
            if (keys.Count > 0)
                items.Add($"fields: {string.Join(", ", keys.Take(60))}{(keys.Count > 60 ? $", … {Num(keys.Count - 60)} more" : "")}");
        }
        if (n > 0)
            items.Add($"first line: {Clip(data.Lines[0], 300)}");
        return Emit(header, items, "lines");
    }

    public static string DetectFormat(ArtifactData data)
    {
        if (data.Kind == "records")
            return "JSON records, one per line";
        var sample = data.Lines.Take(200).ToList();
        if (sample.Count == 0)
            return "empty";
        bool Share(Regex re) => sample.Count(l => re.IsMatch(l)) * 2 >= sample.Count;
        if (Share(SmarterMailLog)) return "SmarterMail log (HH:mm:ss.fff [session id] …)";
        if (Share(TimedLine)) return "timestamped lines (HH:mm:ss …)";
        if (Share(IsoLine)) return "ISO-timestamped lines";
        return "plain text";
    }

    private static string Slice(ArtifactData data, IReadOnlyDictionary<string, JsonElement> args)
    {
        var n = data.Lines.Count;
        var count = Int(args, "count", 50, 1, 500);
        var from = Int(args, "from", 1, -1_000_000_000, 1_000_000_000);
        var start = from < 0 ? Math.Max(0, n + from) : Math.Max(0, from - 1);
        var end = Math.Min(n, start + count);
        if (start >= n)
            return $"lines {Num(start + 1)}- of {Num(n)}: past the end";
        var items = new List<string>();
        for (var i = start; i < end; i++)
            items.Add(LineItem(data, i));
        return Emit($"lines {Num(start + 1)}-{Num(end)} of {Num(n)}", items, "lines");
    }

    private static string Grep(ArtifactData data, IReadOnlyDictionary<string, JsonElement> args)
    {
        var re = Compile(Str(args, "pattern"), Str(args, "flags"));
        var invert = Bool(args, "invert");
        var context = Int(args, "context", 0, 0, 5);
        var limit = Int(args, "limit", 50, 1, 500);
        var n = data.Lines.Count;

        var hits = new List<int>();
        for (var i = 0; i < n; i++)
        {
            var line = data.Lines[i];
            if (Timed(() => re.IsMatch(line)) != invert)
                hits.Add(i);
        }
        var isHit = context > 0 ? hits.ToHashSet() : null;

        var items = new List<string>();
        var units = new List<int>();
        var printed = -1;
        foreach (var i in hits.Take(limit))
        {
            var parts = new List<string>();
            var from = Math.Max(Math.Max(0, i - context), printed + 1);
            if (context > 0 && printed >= 0 && from > printed + 1)
                parts.Add("--");
            for (var j = from; j < i; j++)
                parts.Add(LineItem(data, j, '-'));
            if (i > printed)
                parts.Add(LineItem(data, i, ':'));
            var last = i;
            for (var j = i + 1; j <= Math.Min(n - 1, i + context); j++)
            {
                if (isHit!.Contains(j))
                    break;
                parts.Add(LineItem(data, j, '-'));
                last = j;
            }
            printed = Math.Max(printed, last);
            items.Add(string.Join('\n', parts));
            units.Add(1);
        }
        var noun = invert ? "non-matching lines" : "matching lines";
        return Emit($"{Num(hits.Count)} {(invert ? "non-matching" : "matching")} lines of {Num(n)}", items, noun, hits.Count, $"limit {Num(limit)}", units);
    }

    private static string Count(ArtifactData data, IReadOnlyDictionary<string, JsonElement> args)
    {
        var pattern = Str(args, "pattern");
        var re = Compile(pattern, Str(args, "flags"));
        var by = Str(args, "by").Trim();
        var top = Int(args, "top", 20, 1, 200);
        var n = data.Lines.Count;
        var names = GroupNames(pattern);

        string? mode = null;
        if (by.Length > 0)
        {
            if (by.All(c => c is >= '0' and <= '9')) mode = "index";
            else if (names.Contains(by)) mode = "name";
            else if (data.Records is not null) mode = "field";
            else
                throw new OpException($"\"by\" must be a capture group number or a named group of the pattern{(names.Count > 0 ? $" ({string.Join(", ", names)})" : "")}.");
        }

        var matches = 0;
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < n; i++)
        {
            var line = data.Lines[i];
            if (mode is null)
            {
                if (Timed(() => re.IsMatch(line)))
                    matches++;
                continue;
            }
            var m = Timed(() => re.Match(line));
            if (!m.Success)
                continue;
            matches++;
            string? value = mode switch
            {
                "index" => int.TryParse(by, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && m.Groups[index].Success ? m.Groups[index].Value : null,
                "name" => m.Groups[by].Success ? m.Groups[by].Value : null,
                _ => FieldValue(data.Records![i], by),
            };
            var key = value is null ? "(none)" : Clip(value, ValueClip);
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }

        var header = $"{Num(matches)} matching lines of {Num(n)}";
        if (mode is null)
            return header;
        var sorted = counts.ToList();
        sorted.Sort(ByCount);
        var items = sorted.Take(top).Select(kv => $"{Num(kv.Value)}\t{kv.Key}").ToList();
        return Emit($"{header}; {Num(sorted.Count)} distinct values of {by}", items, "values", sorted.Count, $"top {Num(top)}");
    }

    private static string Fields(ArtifactData data, IReadOnlyDictionary<string, JsonElement> args)
    {
        var pattern = Str(args, "pattern");
        var names = GroupNames(pattern);
        if (names.Count == 0)
            throw new OpException("The pattern has no named groups. Use (?<name>…) for each field to extract.");
        var re = Compile(pattern, Str(args, "flags"));
        var limit = Int(args, "limit", 20, 1, 200);
        var n = data.Lines.Count;

        var rows = new List<string>();
        var distinct = names.Select(_ => new Dictionary<string, int>(StringComparer.Ordinal)).ToList();
        var matches = 0;
        for (var i = 0; i < n; i++)
        {
            var line = data.Lines[i];
            var m = Timed(() => re.Match(line));
            if (!m.Success)
                continue;
            matches++;
            var values = names.Select(name => m.Groups[name].Success ? Clip(m.Groups[name].Value, ValueClip) : "").ToList();
            for (var k = 0; k < values.Count; k++)
                distinct[k][values[k]] = distinct[k].GetValueOrDefault(values[k]) + 1;
            if (rows.Count < limit)
                rows.Add($"#{Num(i + 1)}\t{string.Join('\t', values)}");
        }

        var summary = names.Select((name, k) =>
        {
            var sorted = distinct[k].ToList();
            sorted.Sort(ByCount);
            var shown = string.Join(", ", sorted.Take(5).Select(kv => $"{(kv.Key.Length == 0 ? "(empty)" : kv.Key)} x{Num(kv.Value)}"));
            return $"distinct {name}: {Num(sorted.Count)}{(shown.Length > 0 ? $" (top: {shown})" : "")}";
        }).ToList();

        var items = new List<string>(summary) { string.Join('\t', new[] { "line" }.Concat(names)) };
        items.AddRange(rows);
        var units = summary.Select(_ => 0).Append(0).Concat(rows.Select(_ => 1)).ToList();
        return Emit($"{Num(matches)} matching lines of {Num(n)}; fields: {string.Join(", ", names)}", items, "rows", matches, $"limit {Num(limit)}", units);
    }

    private static string Between(ArtifactData data, IReadOnlyDictionary<string, JsonElement> args)
    {
        var start = ParseClock(Str(args, "start"), false);
        var end = ParseClock(Str(args, "end"), true);
        if (start is not { } s || end is not { } e)
            throw new OpException("start and end must be times of day, HH:mm or HH:mm:ss.");
        var limit = Int(args, "limit", 100, 1, 500);
        var n = data.Lines.Count;
        bool InRange(int t) => s <= e ? t >= s && t <= e : t >= s || t <= e;

        var hits = new List<int>();
        int? current = null;
        for (var i = 0; i < n; i++)
        {
            if (LineTime(data.Lines[i]) is { } t)
                current = t;
            if (current is { } c && InRange(c))
                hits.Add(i);
        }
        var items = hits.Take(limit).Select(i => LineItem(data, i)).ToList();
        return Emit($"{Num(hits.Count)} lines between {Clock(s)} and {Clock(e)} of {Num(n)}", items, "lines", hits.Count, $"limit {Num(limit)}");
    }

    private static string Session(ArtifactData data, IReadOnlyDictionary<string, JsonElement> args)
    {
        var id = Str(args, "id").Trim();
        if (id.StartsWith('['))
            id = id[1..];
        if (id.EndsWith(']'))
            id = id[..^1];
        if (id.Length == 0)
            throw new OpException("id is required.");
        var limit = Int(args, "limit", 200, 1, 500);
        var tag = $"[{id}]";
        var n = data.Lines.Count;
        var hits = new List<int>();
        for (var i = 0; i < n; i++)
        {
            if (data.Lines[i].Contains(tag, StringComparison.Ordinal))
                hits.Add(i);
        }
        var items = hits.Take(limit).Select(i => LineItem(data, i)).ToList();
        return Emit($"{Num(hits.Count)} lines with {Clip(tag, ValueClip)} of {Num(n)}", items, "lines", hits.Count, $"limit {Num(limit)}");
    }
}
