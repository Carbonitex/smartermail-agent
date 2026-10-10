using System.Globalization;
using System.Text;
using System.Text.Json;

namespace SmarterMailAgent.Tasks.Triggers;

/// <summary>
/// The path syntax of the predicate language: <c>$</c> (optional) for the current value, then
/// segments <c>.name</c>, <c>["name"]</c>, <c>[n]</c> and <c>[*]</c> (every element of an array, or
/// every value of an object). At the top level the current value is the probe result; inside a
/// <c>where</c> (and for a <c>key</c>) it is the item. A path that selects nothing is "no value", never
/// an error. No filters, no recursion, no functions.
/// </summary>
public sealed class JsonPath
{
    public const int MaxSegments = 8;
    public const int MaxLength = 200;

    public abstract record Segment;
    public sealed record Name(string Value) : Segment;
    public sealed record Index(int Value) : Segment;
    public sealed record Wildcard : Segment;

    private JsonPath(string text, IReadOnlyList<Segment> segments)
    {
        Text = text;
        Segments = segments;
    }

    public string Text { get; }

    public IReadOnlyList<Segment> Segments { get; }

    public bool HasWildcard => Segments.Any(s => s is Wildcard);

    public override string ToString() => Text;

    public static bool TryParse(string? text, out JsonPath path, out string error)
    {
        path = null!;
        error = "";
        if (text is null)
        {
            error = "a path is required";
            return false;
        }

        var s = text.Trim();
        if (s.Length > MaxLength)
        {
            error = $"the path is longer than {MaxLength} characters";
            return false;
        }

        var segments = new List<Segment>();
        var i = 0;
        if (i < s.Length && s[i] == '$')
            i++;

        while (i < s.Length)
        {
            if (segments.Count >= MaxSegments)
            {
                error = $"the path has more than {MaxSegments} segments";
                return false;
            }

            var c = s[i];
            if (c == '.' || (segments.Count == 0 && i == 0 && IsNameChar(c)))
            {
                if (c == '.')
                    i++;
                var start = i;
                while (i < s.Length && IsNameChar(s[i]))
                    i++;
                if (i == start)
                {
                    error = $"expected a name at position {start + 1}";
                    return false;
                }
                segments.Add(new Name(s[start..i]));
            }
            else if (c == '[')
            {
                i++;
                if (i < s.Length && s[i] == '*')
                {
                    i++;
                    if (i >= s.Length || s[i] != ']')
                    {
                        error = "expected ] after [*";
                        return false;
                    }
                    i++;
                    segments.Add(new Wildcard());
                }
                else if (i < s.Length && (s[i] == '"' || s[i] == '\''))
                {
                    var quote = s[i++];
                    var name = new StringBuilder();
                    while (i < s.Length && s[i] != quote)
                    {
                        if (s[i] == '\\' && i + 1 < s.Length)
                            i++;
                        name.Append(s[i++]);
                    }
                    if (i + 1 >= s.Length || s[i] != quote || s[i + 1] != ']')
                    {
                        error = "an unterminated [\"name\"]";
                        return false;
                    }
                    i += 2;
                    segments.Add(new Name(name.ToString()));
                }
                else
                {
                    var start = i;
                    while (i < s.Length && char.IsAsciiDigit(s[i]))
                        i++;
                    if (i == start || i >= s.Length || s[i] != ']' || i - start > 6)
                    {
                        error = "expected [n], [*] or [\"name\"]";
                        return false;
                    }
                    segments.Add(new Index(int.Parse(s[start..i], CultureInfo.InvariantCulture)));
                    i++;
                }
            }
            else
            {
                error = $"unexpected '{c}' at position {i + 1}";
                return false;
            }
        }

        path = new JsonPath(Format(segments), segments);
        return true;
    }

    /// <summary>Every value the path selects from <paramref name="start"/>, with its concrete path, in document order.</summary>
    internal IEnumerable<(JsonElement Value, string Path)> Select(JsonElement start, string basePath, EvalBudget budget) =>
        Walk(start, basePath, 0, budget);

    private IEnumerable<(JsonElement, string)> Walk(JsonElement current, string at, int index, EvalBudget budget)
    {
        budget.Spend();
        if (index == Segments.Count)
        {
            yield return (current, at);
            yield break;
        }

        switch (Segments[index])
        {
            case Name name when current.ValueKind == JsonValueKind.Object:
                if (current.TryGetProperty(name.Value, out var child))
                {
                    foreach (var r in Walk(child, at + FormatName(name.Value), index + 1, budget))
                        yield return r;
                }
                break;

            case Index n when current.ValueKind == JsonValueKind.Array:
                if (n.Value < current.GetArrayLength())
                {
                    foreach (var r in Walk(current[n.Value], $"{at}[{n.Value}]", index + 1, budget))
                        yield return r;
                }
                break;

            case Wildcard when current.ValueKind == JsonValueKind.Array:
                var i = 0;
                foreach (var item in current.EnumerateArray())
                {
                    foreach (var r in Walk(item, $"{at}[{i}]", index + 1, budget))
                        yield return r;
                    i++;
                }
                break;

            case Wildcard when current.ValueKind == JsonValueKind.Object:
                foreach (var property in current.EnumerateObject())
                {
                    foreach (var r in Walk(property.Value, at + FormatName(property.Name), index + 1, budget))
                        yield return r;
                }
                break;
        }
    }

    private static bool IsNameChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '@';

    private static string Format(IEnumerable<Segment> segments)
    {
        var builder = new StringBuilder("$");
        foreach (var segment in segments)
        {
            builder.Append(segment switch
            {
                Name n => FormatName(n.Value),
                Index n => $"[{n.Value}]",
                _ => "[*]",
            });
        }
        return builder.ToString();
    }

    internal static string FormatName(string name) =>
        name.Length > 0 && name.All(IsNameChar) && !char.IsAsciiDigit(name[0])
            ? "." + name
            : "[" + JsonSerializer.Serialize(name) + "]";
}

/// <summary>
/// A cap on the work one evaluation may do (path steps and node visits), so a nested <c>count</c>
/// over a large result cannot pin a probe slot. Exceeding it fails the evaluation, it never truncates
/// silently.
/// </summary>
internal sealed class EvalBudget(int steps)
{
    private int _left = steps;

    public void Spend(int n = 1)
    {
        _left -= n;
        if (_left < 0)
            throw new BudgetExceededException();
    }
}

internal sealed class BudgetExceededException() : Exception("The condition needs too much work on this result.");
