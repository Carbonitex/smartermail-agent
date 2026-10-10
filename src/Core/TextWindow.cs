namespace SmarterMailMcp.Core;

/// <summary>One bounded slice of a large text, with what a caller needs to page on.</summary>
public sealed record TextWindowResult(
    string Text, int TotalChars, int Offset, int ReturnedChars, bool HasMore, int? NextOffset, bool Tail);

/// <summary>
/// Cuts large text results into windows an LLM can handle. Windows end (or, in tail mode, begin) on a
/// line boundary where one exists in the nearer half of the window; a single line longer than the
/// window is cut mid-line.
/// </summary>
public static class TextWindow
{
    public const int DefaultMaxChars = 16_000;
    public const int MaxCharsCap = 100_000;
    public const int MinMaxChars = 200;

    public static int ClampMaxChars(int maxChars) =>
        maxChars <= 0 ? DefaultMaxChars : Math.Clamp(maxChars, MinMaxChars, MaxCharsCap);

    /// <summary>Keeps the lines containing <paramref name="contains"/> (case-insensitive); null/empty keeps all.</summary>
    public static (string Text, int MatchedLines, int TotalLines) FilterLines(string text, string? contains)
    {
        text ??= "";
        var lines = text.Split('\n');
        var total = lines.Length > 0 && lines[^1].Length == 0 ? lines.Length - 1 : lines.Length;
        if (string.IsNullOrEmpty(contains))
            return (text, total, total);
        var kept = lines.Take(total).Where(l => l.Contains(contains, StringComparison.OrdinalIgnoreCase)).ToList();
        return (kept.Count == 0 ? "" : string.Join('\n', kept) + "\n", kept.Count, total);
    }

    /// <summary>
    /// Forward mode: <paramref name="offset"/> is the first character. Tail mode: <paramref name="offset"/>
    /// counts characters back from the end (0 = the last window; <c>NextOffset</c> pages towards the start).
    /// </summary>
    public static TextWindowResult Slice(string text, int maxChars, int offset = 0, bool tail = false)
    {
        text ??= "";
        maxChars = ClampMaxChars(maxChars);
        offset = Math.Max(0, offset);
        var total = text.Length;

        int start, end;
        if (tail)
        {
            end = Math.Max(0, total - offset);
            start = Math.Max(0, end - maxChars);
            if (start > 0)
            {
                var nl = text.IndexOf('\n', start, end - start);
                if (nl >= 0 && nl - start < (end - start) / 2) start = nl + 1;
            }
        }
        else
        {
            start = Math.Min(offset, total);
            end = Math.Min(total, start + maxChars);
            if (end < total && end > start)
            {
                var nl = text.LastIndexOf('\n', end - 1, end - start);
                if (nl >= start + (end - start) / 2) end = nl + 1;
            }
        }

        var slice = text[start..end];
        if (tail)
        {
            var more = start > 0;
            return new(slice, total, total - end, slice.Length, more, more ? total - start : null, true);
        }
        var hasMore = end < total;
        return new(slice, total, start, slice.Length, hasMore, hasMore ? end : null, false);
    }
}
