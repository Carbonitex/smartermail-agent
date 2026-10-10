using System.Text.Json;
using System.Text.Json.Nodes;

namespace SmarterMailAgent.Llm.Artifacts;

/// <summary>One large tool result kept out of the model's context.</summary>
public sealed class Artifact
{
    public required string Handle { get; init; }
    public required string Tool { get; init; }
    public required string Arguments { get; init; }
    public required string Kind { get; init; }
    public string? Field { get; init; }
    public JsonObject? Meta { get; init; }
    public required string Body { get; init; }
    public int Chars => Body.Length;
    public required int OriginalChars { get; init; }
    public required bool Truncated { get; init; }
    public required int Count { get; init; }
    public required IReadOnlyList<string> Head { get; init; }
    public required IReadOnlyList<string> Tail { get; init; }
}

/// <summary>
/// A scheduled run's artifacts: the server-side twin of <c>wwwroot/js/artifacts.js</c>. A tool result over
/// the threshold is kept here, in memory, for the run only (never on disk, never in the sealed
/// transcript), and the model gets the same stub the browser builds. Least recently used first out when
/// the run's total cap is reached; a single result over the per-artifact cap is kept truncated.
/// </summary>
public sealed class ArtifactStore(int threshold = ArtifactStore.DefaultThreshold, int maxTotal = 32 * 1024 * 1024, int maxEach = 16 * 1024 * 1024)
{
    public const int DefaultThreshold = 20_000;
    public const string AnalyzeResultName = "analyze_result";

    private const int HeadLines = 30, HeadChars = 2500, TailLines = 10, TailChars = 800, StubLineClip = 200, MetaFields = 20, MetaClip = 300;

    private readonly LinkedList<Artifact> _lru = new();
    private readonly Dictionary<string, LinkedListNode<Artifact>> _byHandle = new(StringComparer.Ordinal);
    private long _total;
    private int _next = 1;

    public int Threshold => threshold;
    public int Count => _byHandle.Count;
    public long TotalChars => _total;

    /// <summary>The model-facing content for a finished call: a stub when the result was kept, else null.</summary>
    public (string Stub, Artifact Artifact)? Capture(string tool, string arguments, string content, bool isError)
    {
        if (isError || content.Length <= threshold || tool == AnalyzeResultName)
            return null;
        var artifact = Add(tool, arguments, content);
        return (BuildStub(artifact, "run"), artifact);
    }

    public Artifact Add(string tool, string arguments, string text)
    {
        var (kind, body, field, meta) = Unwrap(text);
        var originalChars = body.Length;
        var truncated = false;
        List<string> lines;
        if (kind == "records")
        {
            var records = ParseRecords(body);
            if (body.Length > maxEach)
            {
                records = TruncateRecords(records, maxEach);
                body = new JsonArray(records.Select(r => r?.DeepClone()).ToArray()).ToJsonString(ArtifactOperators.JsJson);
                truncated = true;
            }
            lines = records.Select(r => r?.ToJsonString(ArtifactOperators.JsJson) ?? "null").ToList();
        }
        else
        {
            if (body.Length > maxEach)
            {
                var nl = body.LastIndexOf('\n', maxEach - 1);
                body = body[..(nl > maxEach / 2 ? nl + 1 : maxEach)];
                truncated = true;
            }
            lines = ArtifactOperators.SplitLines(body);
        }

        var head = Take(lines, HeadLines, HeadChars);
        var rest = lines.Skip(head.Count).ToList();
        var tailSource = rest.Skip(Math.Max(0, rest.Count - TailLines)).Reverse().ToList();
        var tail = Take(tailSource, TailLines, TailChars);
        tail.Reverse();

        var artifact = new Artifact
        {
            Handle = $"r{_next++}",
            Tool = tool,
            Arguments = arguments,
            Kind = kind,
            Field = field,
            Meta = meta,
            Body = body,
            OriginalChars = originalChars,
            Truncated = truncated,
            Count = lines.Count,
            Head = head,
            Tail = tail,
        };

        while (_lru.Count > 0 && _total + artifact.Chars > maxTotal)
        {
            var oldest = _lru.First!;
            _lru.RemoveFirst();
            _byHandle.Remove(oldest.Value.Handle);
            _total -= oldest.Value.Chars;
        }
        _byHandle[artifact.Handle] = _lru.AddLast(artifact);
        _total += artifact.Chars;
        return artifact;
    }

    /// <summary>The artifact for a handle (marked recently used), or null when unknown or evicted.</summary>
    public Artifact? Get(string handle)
    {
        if (!_byHandle.TryGetValue(handle.Trim(), out var node))
            return null;
        _lru.Remove(node);
        _lru.AddLast(node);
        return node.Value;
    }

    /// <summary>Whether this store ever issued the handle (so a miss means it was evicted).</summary>
    public bool Issued(string handle) =>
        handle.Trim() is { Length: > 1 } h && h[0] == 'r' && int.TryParse(h[1..], out var n) && n > 0 && n < _next;

    /// <summary>The same unwrap as the browser: a dominant string field becomes text, a dominant array records.</summary>
    public static (string Kind, string Body, string? Field, JsonObject? Meta) Unwrap(string content)
    {
        var trimmed = content.TrimStart();
        if (trimmed.Length == 0 || (trimmed[0] != '{' && trimmed[0] != '['))
            return ("text", content, null, null);
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(content);
        }
        catch (JsonException)
        {
            return ("text", content, null, null);
        }

        if (parsed is JsonArray)
            return ("records", content, null, null);
        if (parsed is not JsonObject obj)
            return ("text", content, null, null);

        string? best = null;
        var bestSize = 0;
        foreach (var (key, value) in obj)
        {
            var size = value switch
            {
                JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>().Length,
                JsonArray a => a.ToJsonString(ArtifactOperators.JsJson).Length,
                _ => -1,
            };
            if (size > bestSize)
            {
                best = key;
                bestSize = size;
            }
        }
        if (best is null || bestSize < content.Length / 2.0)
            return ("text", obj.ToJsonString(new System.Text.Json.JsonSerializerOptions(ArtifactOperators.JsJson) { WriteIndented = true }), null, null);

        var meta = new JsonObject();
        foreach (var (key, value) in obj)
        {
            if (key == best || meta.Count >= MetaFields)
                continue;
            switch (value)
            {
                case null:
                    meta[key] = null;
                    break;
                case JsonValue v when v.GetValueKind() == JsonValueKind.String:
                    meta[key] = ArtifactOperators.Clip(v.GetValue<string>(), MetaClip);
                    break;
                case JsonValue v:
                    meta[key] = v.DeepClone();
                    break;
                default:
                    if (value.ToJsonString(ArtifactOperators.JsJson).Length <= MetaClip)
                        meta[key] = value.DeepClone();
                    break;
            }
        }

        var bulk = obj[best];
        return bulk is JsonArray array
            ? ("records", array.ToJsonString(ArtifactOperators.JsJson), best, meta)
            : ("text", bulk!.GetValue<string>(), best, meta);
    }

    /// <summary>The model-facing stand-in, keys in the browser's order: one line of JSON, a few KB at most.</summary>
    public static string BuildStub(Artifact artifact, string scope)
    {
        var stub = new JsonObject
        {
            ["artifact"] = artifact.Handle,
            ["tool"] = artifact.Tool,
            ["kind"] = artifact.Kind,
            ["chars"] = artifact.Chars,
            [artifact.Kind == "records" ? "records" : "lines"] = artifact.Count,
        };
        if (artifact.Field is not null)
            stub["field"] = artifact.Field;
        if (artifact.Truncated)
            stub["truncated"] = $"only the first {artifact.Chars} of {artifact.OriginalChars} chars were kept";
        if (artifact.Meta is { Count: > 0 } meta)
            stub["meta"] = meta.DeepClone();
        stub["head"] = new JsonArray(artifact.Head.Select(l => (JsonNode)l).ToArray());
        if (artifact.Tail.Count > 0)
            stub["tail"] = new JsonArray(artifact.Tail.Select(l => (JsonNode)l).ToArray());
        stub["note"] = $"The full result ({artifact.Chars} chars) is kept {(scope == "run" ? "for this run" : "in this browser tab")} as artifact {artifact.Handle}; " +
                       "only the lines above are shown here. To answer from all of it, call analyze_result with this artifact and a question.";
        return stub.ToJsonString(ArtifactOperators.JsJson);
    }

    private static List<JsonNode?> ParseRecords(string body)
    {
        try
        {
            return JsonNode.Parse(body) is JsonArray array ? array.ToList() : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static List<JsonNode?> TruncateRecords(List<JsonNode?> records, int max)
    {
        var kept = new List<JsonNode?>();
        var size = 2;
        foreach (var r in records)
        {
            var length = (r?.ToJsonString(ArtifactOperators.JsJson) ?? "null").Length;
            if (size + length + 1 > max)
                break;
            kept.Add(r);
            size += length + 1;
        }
        return kept;
    }

    private static List<string> Take(IEnumerable<string> lines, int maxLines, int maxChars)
    {
        var output = new List<string>();
        var used = 0;
        foreach (var line in lines)
        {
            if (output.Count >= maxLines)
                break;
            var clipped = ArtifactOperators.Clip(line, StubLineClip);
            if (used + clipped.Length > maxChars)
                break;
            output.Add(clipped);
            used += clipped.Length;
        }
        return output;
    }
}
