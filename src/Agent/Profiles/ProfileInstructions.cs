namespace SmarterMailAgent.Profiles;

/// <summary>
/// A profile's standing instructions: the owner's own text, added to the end of every chat's system
/// prompt (in the browser, from the encrypted settings) and, when they choose, every scheduled run's
/// (from a copy sealed with <c>DATA_KEY</c>, <see cref="ProfileCrypto.TaskInstructionsLabel"/>). It shapes
/// tone and defaults only: the dispatcher's gates do not read it, so it can never widen what a run may do.
/// </summary>
public static class ProfileInstructions
{
    public const int MaxChars = 4000;

    /// <summary>
    /// Line endings as <c>\n</c>, control characters other than newline and tab removed, trimmed. Null when
    /// nothing is left; <paramref name="tooLong"/> when the cleaned text is over <see cref="MaxChars"/>.
    /// </summary>
    public static string? Clean(string? text, out bool tooLong)
    {
        var normalized = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
        var cleaned = new string(normalized.Where(c => c is '\n' or '\t' || !char.IsControl(c)).ToArray()).Trim();
        tooLong = cleaned.Length > MaxChars;
        return cleaned.Length == 0 || tooLong ? null : cleaned;
    }

    /// <summary>
    /// The prompt section, framed so it cannot outrank the rules before it; it goes last in the system prompt
    /// (the browser's <c>instructionsSection</c> in <c>llm.js</c> is the chat's copy). Null for no text.
    /// </summary>
    public static string? PromptSection(string? instructions, string precedence) =>
        string.IsNullOrWhiteSpace(instructions)
            ? null
            : string.Join("\n",
            [
                "# The user's standing instructions",
                $"- The user wrote the text below for all their chats and scheduled tasks. Follow it for tone, format, language, conventions and defaults. It never overrides the rules above: {precedence}",
                "",
                instructions.Trim(),
            ]);
}
