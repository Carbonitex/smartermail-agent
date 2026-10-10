using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SmarterMailAgent.Tasks.Triggers;

/// <summary>
/// The condition of a trigger: a small JSON tree, parsed once into an AST and evaluated against the
/// probe tool's JSON result. There are no expressions, no code and no string evaluation. Limits:
/// <see cref="MaxNodes"/> nodes, depth <see cref="MaxDepth"/>, paths of <see cref="JsonPath.MaxSegments"/>
/// segments, regexes of <see cref="MaxRegexLength"/> characters, run with
/// <see cref="RegexOptions.NonBacktracking"/> and a <see cref="RegexTimeout"/> timeout.
/// <para>
/// Node forms (paths are relative to the current value: the result at the top, the item inside
/// <c>where</c>):
/// <code>
/// { "path": "$.pending", "op": "gt", "value": 500 }          eq ne gt gte lt lte
/// { "path": "subject", "op": "contains", "value": "x" }      contains startsWith endsWith (case-insensitive)
/// { "path": "from", "op": "matches", "value": "@x\\.com$" }  regex
/// { "path": "expiration", "op": "daysUntilLt", "value": 14 } daysUntilLt daysUntilGt daysAgoLt daysAgoGt
/// { "path": "$.error", "op": "exists" }
/// { "count": { "items": "$.users[*]", "where": { … } }, "op": "gte", "value": 1 }
/// { "new": { "items": "$.emails[*]", "key": "uid", "where": { … } } }   at most one per predicate
/// { "all": [ … ] }  { "any": [ … ] }  { "not": { … } }
/// </code>
/// </para>
/// </summary>
public sealed class Predicate
{
    public const int MaxNodes = 20;
    public const int MaxDepth = 6;
    public const int MaxRegexLength = 200;
    public const int MaxTextLength = 500;
    public const int MaxJsonLength = 8192;
    public const int MaxEvidenceItems = 20;
    public const int MaxEvidenceItemChars = 2048;
    public const int MaxEvidenceChars = 16_384;
    public const int MaxNewItems = 500;
    public const int EvaluationBudget = 200_000;
    public static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(50);

    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };

    private readonly Node _root;

    private Predicate(Node root, bool usesNew)
    {
        _root = root;
        UsesNew = usesNew;
    }

    /// <summary>The tree contains a <c>new</c> node: firing follows new items, not edge / level.</summary>
    public bool UsesNew { get; }

    /// <summary>A plain-language rendering, for the run prompt, alert mails and the editor. Built from the definition only.</summary>
    public string Describe() => _root.Describe();

    public override string ToString() => Describe();

    // ------------------------------------------------------------------ parse

    /// <summary>Parses <paramref name="when"/>; null with every error (by node path) when it does not fit the language or its limits.</summary>
    public static Predicate? Parse(JsonElement when, out IReadOnlyList<string> errors)
    {
        var list = new List<string>();
        errors = list;
        if (when.ValueKind != JsonValueKind.Object)
        {
            list.Add("when: the condition must be a JSON object.");
            return null;
        }
        if (when.GetRawText().Length > MaxJsonLength)
        {
            list.Add($"when: the condition is longer than {MaxJsonLength} characters.");
            return null;
        }

        var parser = new Parser(list);
        var root = parser.Node(when, "when", 1);
        if (parser.Count > MaxNodes)
            list.Add($"when: the condition has {parser.Count} parts; at most {MaxNodes} are allowed.");
        return list.Count == 0 && root is not null ? new Predicate(root, parser.UsesNew) : null;
    }

    private sealed class Parser(List<string> errors)
    {
        public int Count;
        public bool UsesNew;
        public string? FirstNewAt;

        private static readonly string[] Comparisons = ["eq", "ne", "gt", "gte", "lt", "lte"];
        private static readonly string[] TextOps = ["contains", "startsWith", "endsWith"];
        private static readonly string[] DateOps = ["daysUntilLt", "daysUntilGt", "daysAgoLt", "daysAgoGt"];

        public Node? Node(JsonElement e, string at, int depth)
        {
            Count++;
            if (depth > MaxDepth)
            {
                errors.Add($"{at}: nested deeper than {MaxDepth} levels.");
                return null;
            }
            if (e.ValueKind != JsonValueKind.Object)
            {
                errors.Add($"{at}: expected an object.");
                return null;
            }

            var keys = e.EnumerateObject().Select(p => p.Name).ToList();
            if (e.TryGetProperty("all", out var all) || e.TryGetProperty("any", out all))
            {
                var isAll = e.TryGetProperty("all", out _);
                var name = isAll ? "all" : "any";
                if (!Only(e, at, name))
                    return null;
                if (all.ValueKind != JsonValueKind.Array || all.GetArrayLength() is 0 or > MaxNodes)
                {
                    errors.Add($"{at}.{name}: expected a list of 1 to {MaxNodes} conditions.");
                    return null;
                }
                var children = all.EnumerateArray().Select((c, i) => Node(c, $"{at}.{name}[{i}]", depth + 1)).ToList();
                return children.Any(c => c is null) ? null : new Logic(isAll, children!);
            }

            if (e.TryGetProperty("not", out var not))
            {
                if (!Only(e, at, "not"))
                    return null;
                return Node(not, $"{at}.not", depth + 1) is { } inner ? new Not(inner) : null;
            }

            if (e.TryGetProperty("count", out var count))
            {
                if (!Only(e, at, "count", "op", "value"))
                    return null;
                var items = Items(count, $"{at}.count", depth, out var where, allowKey: false, out _);
                var op = Op(e, at, Comparisons);
                var value = Number(e, at);
                return items is null || op is null || value is null || (where is null && count.TryGetProperty("where", out _))
                    ? null
                    : new Count(items, where, op, value.Value);
            }

            if (e.TryGetProperty("new", out var @new))
            {
                if (!Only(e, at, "new"))
                    return null;
                if (FirstNewAt is { } first)
                {
                    // One set of seen keys per task: two "new" nodes would share (and overrun) it.
                    errors.Add($"{at}: only one \"new\" condition is allowed (there is already one at {first}).");
                    return null;
                }
                FirstNewAt = at;
                UsesNew = true;
                var items = Items(@new, $"{at}.new", depth, out var where, allowKey: true, out var key);
                return items is null || (where is null && @new.TryGetProperty("where", out _)) || key is null
                    ? null
                    : new New(items, key, where);
            }

            if (!e.TryGetProperty("path", out var rawPath))
            {
                errors.Add($"{at}: expected one of path, all, any, not, count or new (got {string.Join(", ", keys)}).");
                return null;
            }
            if (!Only(e, at, "path", "op", "value"))
                return null;

            var path = Path(rawPath, $"{at}.path");
            var opName = Op(e, at, [.. Comparisons, .. TextOps, "matches", .. DateOps, "exists"]);
            if (path is null || opName is null)
                return null;

            if (opName == "exists")
                return e.TryGetProperty("value", out _) ? Fail($"{at}.value: exists takes no value.") : new Exists(path);

            if (!e.TryGetProperty("value", out var v))
                return Fail($"{at}.value: {opName} needs a value.");

            if (Comparisons.Contains(opName))
            {
                if (opName is "eq" or "ne")
                {
                    if (v.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                        return Fail($"{at}.value: eq / ne compare with a number, string, true, false or null.");
                    if (v.ValueKind == JsonValueKind.String && v.GetString()!.Length > MaxTextLength)
                        return Fail($"{at}.value: at most {MaxTextLength} characters.");
                }
                else if (v.ValueKind != JsonValueKind.Number)
                {
                    return Fail($"{at}.value: {opName} compares with a number.");
                }
                return new Compare(path, opName, v.Clone());
            }

            if (v.ValueKind != JsonValueKind.String && !DateOps.Contains(opName))
                return Fail($"{at}.value: {opName} needs a string.");

            if (TextOps.Contains(opName))
            {
                var text = v.GetString()!;
                return text.Length is 0 or > MaxTextLength
                    ? Fail($"{at}.value: 1 to {MaxTextLength} characters.")
                    : new Text(path, opName, text);
            }

            if (opName == "matches")
            {
                var pattern = v.GetString()!;
                if (pattern.Length is 0 or > MaxRegexLength)
                    return Fail($"{at}.value: a pattern of 1 to {MaxRegexLength} characters.");
                try
                {
                    return new Matches(path, pattern, new Regex(pattern,
                        RegexOptions.NonBacktracking | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout));
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
                {
                    return Fail($"{at}.value: not a supported pattern (no backreferences or lookarounds).");
                }
            }

            // Date operators.
            if (v.ValueKind != JsonValueKind.Number || !v.TryGetDouble(out var days) || days is < 0 or > 36_500)
                return Fail($"{at}.value: {opName} takes a number of days (0 to 36500).");
            return new DateCheck(path, opName, days);
        }

        private JsonPath? Items(JsonElement spec, string at, int depth, out Node? where, bool allowKey, out KeySpec? key)
        {
            where = null;
            key = null;
            if (spec.ValueKind != JsonValueKind.Object)
            {
                errors.Add($"{at}: expected {{ \"items\": \"…\" }}.");
                return null;
            }
            if (!Only(spec, at, allowKey ? ["items", "where", "key"] : ["items", "where"]))
                return null;
            if (!spec.TryGetProperty("items", out var rawItems))
            {
                errors.Add($"{at}.items: say which items, e.g. \"$.emails[*]\".");
                return null;
            }

            var items = Path(rawItems, $"{at}.items");
            if (spec.TryGetProperty("where", out var w))
                where = Node(w, $"{at}.where", depth + 1);

            if (allowKey)
            {
                var keyPaths = new List<JsonPath>();
                if (spec.TryGetProperty("key", out var k))
                {
                    var raw = k.ValueKind == JsonValueKind.Array ? k.EnumerateArray().ToList() : [k];
                    if (raw.Count is 0 or > 3)
                        errors.Add($"{at}.key: one path, or a list of up to 3.");
                    for (var i = 0; i < raw.Count && i < 3; i++)
                    {
                        if (Path(raw[i], $"{at}.key") is { } p)
                            keyPaths.Add(p);
                    }
                }
                key = new KeySpec(keyPaths);
            }

            return items;
        }

        private JsonPath? Path(JsonElement raw, string at)
        {
            if (raw.ValueKind != JsonValueKind.String)
            {
                errors.Add($"{at}: expected a path string such as \"$.items[*].name\".");
                return null;
            }
            if (!JsonPath.TryParse(raw.GetString(), out var path, out var error))
            {
                errors.Add($"{at}: {error}.");
                return null;
            }
            return path;
        }

        private string? Op(JsonElement e, string at, string[] allowed)
        {
            if (!e.TryGetProperty("op", out var op) || op.ValueKind != JsonValueKind.String)
            {
                errors.Add($"{at}.op: expected one of {string.Join(", ", allowed)}.");
                return null;
            }
            var name = allowed.FirstOrDefault(a => string.Equals(a, op.GetString(), StringComparison.OrdinalIgnoreCase));
            if (name is null)
                errors.Add($"{at}.op: '{Clip(op.GetString())}' is not one of {string.Join(", ", allowed)}.");
            return name;
        }

        private double? Number(JsonElement e, string at)
        {
            if (e.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d))
                return d;
            errors.Add($"{at}.value: expected a number.");
            return null;
        }

        private bool Only(JsonElement e, string at, params string[] allowed)
        {
            var extra = e.EnumerateObject().Select(p => p.Name).Where(n => !allowed.Contains(n)).ToList();
            if (extra.Count == 0)
                return true;
            errors.Add($"{at}: unexpected {string.Join(", ", extra.Select(Clip))}.");
            return false;
        }

        private Node? Fail(string message)
        {
            errors.Add(message);
            return null;
        }

        private static string Clip(string? s) => s is null ? "" : s.Length > 40 ? s[..40] + "…" : s;
    }

    // ------------------------------------------------------------------ evaluate

    /// <summary>The outcome of one evaluation.</summary>
    /// <param name="Value">Whether the condition holds.</param>
    /// <param name="Matched">Evidence: <c>{ path, value }</c> for each value or item that made it true, clamped.</param>
    /// <param name="Truncated">Evidence was dropped or clamped.</param>
    /// <param name="Errors">Non-fatal problems (a regex that timed out counts as false).</param>
    /// <param name="Keys">Hashes of every item a <c>new</c> node looked at, in order.</param>
    /// <param name="NewKeys">Of <paramref name="Keys"/>, the matching items that were not seen before.</param>
    /// <param name="Failed">The evaluation ran out of budget; <paramref name="Value"/> means nothing.</param>
    public sealed record Evaluation(
        bool Value, IReadOnlyList<JsonObject> Matched, bool Truncated, IReadOnlyList<string> Errors,
        IReadOnlyList<string> Keys, IReadOnlySet<string> NewKeys, bool Failed = false);

    /// <param name="seen">Key hashes already seen (trigger state); null = nothing seen, every item is new.</param>
    public Evaluation Evaluate(JsonElement result, IReadOnlySet<string>? seen, DateTimeOffset now)
    {
        var ctx = new Ctx(seen, now);
        var evidence = new List<JsonObject>();
        bool value;
        try
        {
            value = _root.Eval(ctx, result, "$", evidence);
        }
        catch (BudgetExceededException ex)
        {
            return new Evaluation(false, [], false, [ex.Message], [], new HashSet<string>(), Failed: true);
        }

        var (matched, truncated) = value ? ClampEvidence(evidence) : ([], false);
        return new Evaluation(value, matched, truncated, ctx.Errors, ctx.Keys, ctx.NewKeys);
    }

    /// <summary>At most <see cref="MaxEvidenceItems"/> entries, each at most <see cref="MaxEvidenceItemChars"/>, all together at most <see cref="MaxEvidenceChars"/>.</summary>
    internal static (List<JsonObject>, bool) ClampEvidence(IReadOnlyList<JsonObject> evidence)
    {
        var result = new List<JsonObject>();
        var truncated = evidence.Count > MaxEvidenceItems;
        var total = 0;
        foreach (var entry in evidence.Take(MaxEvidenceItems))
        {
            var text = entry.ToJsonString(Compact);
            var item = entry;
            if (text.Length > MaxEvidenceItemChars)
            {
                truncated = true;
                item = new JsonObject
                {
                    ["path"] = entry["path"]?.DeepClone(),
                    ["value"] = Shrink(entry["value"]),
                    ["clamped"] = true,
                };
                text = item.ToJsonString(Compact);
                if (text.Length > MaxEvidenceItemChars)
                {
                    item["value"] = text[..(MaxEvidenceItemChars / 2)] + "…";
                    text = item.ToJsonString(Compact);
                }
            }

            if (total + text.Length > MaxEvidenceChars)
            {
                truncated = true;
                break;
            }
            total += text.Length;
            result.Add(item);
        }
        return (result, truncated);
    }

    /// <summary>An object's scalar fields with long strings cut; nested objects and arrays become "…".</summary>
    private static JsonNode? Shrink(JsonNode? value)
    {
        if (value is JsonObject obj)
        {
            var shrunk = new JsonObject();
            var size = 2;
            foreach (var (name, child) in obj)
            {
                JsonNode? copy = child switch
                {
                    JsonObject or JsonArray => "…",
                    JsonValue v when v.TryGetValue<string>(out var s) && s.Length > 200 => s[..200] + "…",
                    _ => child?.DeepClone(),
                };
                size += name.Length + (copy?.ToJsonString(Compact).Length ?? 4) + 4;
                if (size > MaxEvidenceItemChars - 200)
                    break;
                shrunk[name] = copy;
            }
            return shrunk;
        }
        if (value is JsonValue sv && sv.TryGetValue<string>(out var str) && str.Length > 1000)
            return str[..1000] + "…";
        return value is JsonArray ? "…" : value?.DeepClone();
    }

    private sealed class Ctx(IReadOnlySet<string>? seen, DateTimeOffset now)
    {
        public readonly EvalBudget Budget = new(EvaluationBudget);
        public readonly List<string> Errors = [];
        public readonly List<string> Keys = [];
        private readonly HashSet<string> _keySet = new(StringComparer.Ordinal);
        public readonly HashSet<string> NewKeys = new(StringComparer.Ordinal);
        public IReadOnlySet<string>? Seen => seen;
        public DateTimeOffset Now => now;

        public void AddKey(string key)
        {
            if (_keySet.Add(key))
                Keys.Add(key);
        }

        public void Error(string message)
        {
            if (Errors.Count < 20 && !Errors.Contains(message))
                Errors.Add(message);
        }
    }

    private static JsonObject Evidence(string path, JsonElement value) =>
        new() { ["path"] = path, ["value"] = JsonNode.Parse(value.GetRawText()) };

    private abstract class Node
    {
        /// <param name="evidence">Where to add what made it true; null when nobody needs it (inside <c>where</c>, under <c>not</c>).</param>
        public abstract bool Eval(Ctx ctx, JsonElement current, string at, List<JsonObject>? evidence);

        public abstract string Describe();
    }

    /// <summary>A node that tests the values a path selects: true when any of them passes.</summary>
    private abstract class ValueNode(JsonPath path) : Node
    {
        protected JsonPath Path => path;

        public override bool Eval(Ctx ctx, JsonElement current, string at, List<JsonObject>? evidence)
        {
            var any = false;
            foreach (var (value, concrete) in path.Select(current, at, ctx.Budget))
            {
                ctx.Budget.Spend();
                if (!Test(ctx, value))
                    continue;
                any = true;
                if (evidence is null)
                    return true;   // nobody collects; the first hit decides
                if (evidence.Count <= MaxEvidenceItems)
                    evidence.Add(Evidence(concrete, value));
            }
            return any;
        }

        protected abstract bool Test(Ctx ctx, JsonElement value);
    }

    private sealed class Compare(JsonPath path, string op, JsonElement value) : ValueNode(path)
    {
        protected override bool Test(Ctx ctx, JsonElement v) => Compares(op, v, value);

        public override string Describe() => $"{Path} {Symbol(op)} {value.GetRawText()}";
    }

    internal static bool Compares(string op, JsonElement left, JsonElement right)
    {
        if (left.ValueKind == JsonValueKind.Number && right.ValueKind == JsonValueKind.Number &&
            left.TryGetDouble(out var a) && right.TryGetDouble(out var b))
        {
            return op switch
            {
                "eq" => a == b,
                "ne" => a != b,
                "gt" => a > b,
                "gte" => a >= b,
                "lt" => a < b,
                "lte" => a <= b,
                _ => false,
            };
        }

        if (op is not ("eq" or "ne"))
            return false;   // ordering needs two numbers; anything else is a type mismatch

        bool? equal = (left.ValueKind, right.ValueKind) switch
        {
            (JsonValueKind.String, JsonValueKind.String) => string.Equals(left.GetString(), right.GetString(), StringComparison.Ordinal),
            (JsonValueKind.True or JsonValueKind.False, JsonValueKind.True or JsonValueKind.False) => left.ValueKind == right.ValueKind,
            (JsonValueKind.Null, JsonValueKind.Null) => true,
            _ => null,   // different types: neither equal nor "not equal"
        };
        return equal is { } e && (op == "eq" ? e : !e);
    }

    private static string Symbol(string op) => op switch
    {
        "eq" => "=",
        "ne" => "≠",
        "gt" => ">",
        "gte" => "≥",
        "lt" => "<",
        "lte" => "≤",
        _ => op,
    };

    private sealed class Text(JsonPath path, string op, string text) : ValueNode(path)
    {
        protected override bool Test(Ctx ctx, JsonElement v) =>
            v.ValueKind == JsonValueKind.String && op switch
            {
                "contains" => v.GetString()!.Contains(text, StringComparison.OrdinalIgnoreCase),
                "startsWith" => v.GetString()!.StartsWith(text, StringComparison.OrdinalIgnoreCase),
                "endsWith" => v.GetString()!.EndsWith(text, StringComparison.OrdinalIgnoreCase),
                _ => false,
            };

        public override string Describe() => $"{Path} {op switch { "startsWith" => "starts with", "endsWith" => "ends with", _ => "contains" }} \"{text}\"";
    }

    private sealed class Matches(JsonPath path, string pattern, Regex regex) : ValueNode(path)
    {
        protected override bool Test(Ctx ctx, JsonElement v)
        {
            if (v.ValueKind != JsonValueKind.String)
                return false;
            var input = v.GetString()!;
            if (input.Length > 100_000)
                input = input[..100_000];
            try
            {
                return regex.IsMatch(input);
            }
            catch (RegexMatchTimeoutException)
            {
                ctx.Error($"{Path}: the pattern took too long on one value and counted as false.");
                return false;
            }
        }

        public override string Describe() => $"{Path} matches /{pattern}/";
    }

    private sealed class DateCheck(JsonPath path, string op, double days) : ValueNode(path)
    {
        protected override bool Test(Ctx ctx, JsonElement v)
        {
            if (!TryDate(v, out var date))
                return false;
            var ahead = (date - ctx.Now).TotalDays;
            return op switch
            {
                "daysUntilLt" => ahead < days,                 // already past counts: it is "less than N days away"
                "daysUntilGt" => ahead > days,
                "daysAgoLt" => ahead <= 0 && -ahead < days,
                "daysAgoGt" => -ahead > days,
                _ => false,
            };
        }

        public override string Describe() => op switch
        {
            "daysUntilLt" => $"{Path} is less than {days:0.##} days away (or past)",
            "daysUntilGt" => $"{Path} is more than {days:0.##} days away",
            "daysAgoLt" => $"{Path} is less than {days:0.##} days ago",
            _ => $"{Path} is more than {days:0.##} days ago",
        };
    }

    /// <summary>ISO-8601 or RFC 1123 text; a value without an offset is taken as UTC.</summary>
    internal static bool TryDate(JsonElement v, out DateTimeOffset date)
    {
        date = default;
        if (v.ValueKind != JsonValueKind.String)
            return false;
        var s = v.GetString()!.Trim();
        return s.Length is > 0 and <= 64 &&
               DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
                   DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces, out date);
    }

    private sealed class Exists(JsonPath path) : Node
    {
        public override bool Eval(Ctx ctx, JsonElement current, string at, List<JsonObject>? evidence)
        {
            var any = false;
            foreach (var (value, concrete) in path.Select(current, at, ctx.Budget))
            {
                if (value.ValueKind == JsonValueKind.Null)
                    continue;
                any = true;
                if (evidence is null)
                    return true;
                if (evidence.Count <= MaxEvidenceItems)
                    evidence.Add(Evidence(concrete, value));
            }
            return any;
        }

        public override string Describe() => $"{path} exists";
    }

    private sealed class Count(JsonPath items, Node? where, string op, double value) : Node
    {
        public override bool Eval(Ctx ctx, JsonElement current, string at, List<JsonObject>? evidence)
        {
            var matched = new List<(JsonElement, string)>();
            foreach (var (item, concrete) in items.Select(current, at, ctx.Budget))
            {
                ctx.Budget.Spend();
                if (where is null || where.Eval(ctx, item, concrete, null))
                    matched.Add((item, concrete));
            }

            var ok = op switch
            {
                "eq" => matched.Count == value,
                "ne" => matched.Count != value,
                "gt" => matched.Count > value,
                "gte" => matched.Count >= value,
                "lt" => matched.Count < value,
                "lte" => matched.Count <= value,
                _ => false,
            };
            if (ok && evidence is not null)
                evidence.AddRange(matched.Take(MaxEvidenceItems + 1).Select(m => Evidence(m.Item2, m.Item1)));
            return ok;
        }

        public override string Describe() =>
            $"the number of items in {items}{(where is null ? "" : $" where {where.Describe()}")} {Symbol(op)} {value:0.##}";
    }

    private sealed record KeySpec(IReadOnlyList<JsonPath> Paths);

    private sealed class New(JsonPath items, KeySpec key, Node? where) : Node
    {
        private static readonly string[] IdFields = ["id", "uid", "messageId", "guid"];
        private static readonly string[] DateFields = ["date", "dateReceived", "receivedDate", "dateSent", "sentDate", "created", "createdDate", "timestamp", "time"];
        private static readonly string[] TitleFields = ["subject", "name", "title", "from", "senderAddress"];

        public override bool Eval(Ctx ctx, JsonElement current, string at, List<JsonObject>? evidence)
        {
            var any = false;
            var n = 0;
            foreach (var (item, concrete) in items.Select(current, at, ctx.Budget))
            {
                if (n++ >= MaxNewItems)
                    break;
                ctx.Budget.Spend();
                var hash = Hash(items.Text, KeyOf(item, ctx));
                ctx.AddKey(hash);
                if (ctx.Seen?.Contains(hash) == true)
                    continue;
                if (where is not null && !where.Eval(ctx, item, concrete, null))
                    continue;
                ctx.NewKeys.Add(hash);
                any = true;
                if (evidence is { Count: <= MaxEvidenceItems })
                    evidence.Add(Evidence(concrete, item));
            }
            return any;
        }

        /// <summary>
        /// The item's identity: the <c>key</c> paths when given and present; otherwise the first of
        /// id / uid / messageId / guid; otherwise a date field plus a subject-like field; otherwise
        /// the whole item.
        /// </summary>
        private string KeyOf(JsonElement item, Ctx ctx)
        {
            if (key.Paths.Count > 0)
            {
                var parts = key.Paths.Select(p => p.Select(item, "$", ctx.Budget).Select(v => Raw(v.Value)).FirstOrDefault()).ToList();
                if (parts.Any(p => p is not null))
                    return "k:" + string.Join("\u001f", parts.Select(p => p ?? ""));
            }

            if (item.ValueKind == JsonValueKind.Object)
            {
                foreach (var field in IdFields)
                {
                    if (Field(item, field) is { } id)
                        return $"i:{field}:{id}";
                }

                var date = DateFields.Select(f => Field(item, f)).FirstOrDefault(v => v is not null);
                var title = TitleFields.Select(f => Field(item, f)).FirstOrDefault(v => v is not null);
                if (date is not null || title is not null)
                    return $"d:{date}\u001fs:{title}";
            }

            return "r:" + item.GetRawText();
        }

        private static string? Field(JsonElement item, string name)
        {
            foreach (var property in item.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Object or JsonValueKind.Array))
                    return Raw(property.Value);
            }
            return null;
        }

        private static string? Raw(JsonElement v) => v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => v.GetRawText(),
        };

        public override string Describe()
        {
            var keyText = key.Paths.Count > 0 ? $" (identified by {string.Join(" + ", key.Paths)})" : "";
            return $"a new item in {items}{(where is null ? "" : $" where {where.Describe()}")}{keyText}";
        }
    }

    /// <summary>A stored "seen" key: 12 bytes of SHA-256 over the items path and the item's key, base64url.</summary>
    internal static string Hash(string itemsPath, string key)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(itemsPath + "\n" + key));
        return Convert.ToBase64String(digest, 0, 12).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private sealed class Logic(bool all, IReadOnlyList<Node> children) : Node
    {
        public override bool Eval(Ctx ctx, JsonElement current, string at, List<JsonObject>? evidence)
        {
            // Every child is evaluated (no short-circuit): evidence and new-item keys must not depend on order.
            var collected = evidence is null ? null : new List<JsonObject>();
            var trues = 0;
            foreach (var child in children)
            {
                ctx.Budget.Spend();
                var mine = collected is null ? null : new List<JsonObject>();
                if (!child.Eval(ctx, current, at, mine))
                    continue;
                trues++;
                if (mine is not null)
                    collected!.AddRange(mine);
            }

            var ok = all ? trues == children.Count : trues > 0;
            if (ok && collected is not null)
                evidence!.AddRange(collected);
            return ok;
        }

        public override string Describe() =>
            children.Count == 1 ? children[0].Describe() : "(" + string.Join(all ? " and " : " or ", children.Select(c => c.Describe())) + ")";
    }

    private sealed class Not(Node inner) : Node
    {
        public override bool Eval(Ctx ctx, JsonElement current, string at, List<JsonObject>? evidence) =>
            !inner.Eval(ctx, current, at, null);

        public override string Describe() => $"not {inner.Describe()}";
    }
}
