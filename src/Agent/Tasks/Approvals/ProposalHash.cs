using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SmarterMailAgent.Auth;

namespace SmarterMailAgent.Tasks.Approvals;

/// <summary>
/// The exact call a proposal stands for, as one string and one hash. Only the server canonicalises:
/// object keys sorted by ordinal (UTF-16 code unit) order, recursively; no whitespace; numbers,
/// strings and literals copied from <see cref="JsonElement.GetRawText"/> unchanged; keys re-encoded
/// with the relaxed JSON encoder (non-ASCII kept as is). Both sealed copies of a proposal carry the same <c>argsJson</c>
/// string, and the browser hashes the string it displays, byte for byte, so server and browser never
/// need matching serialisers:
/// <code>argsHash = base64url(SHA-256(UTF-8("sma-proposal-v1\n" + tool + "\n" + accountId + "\n" + argsJson)))</code>
/// Canonicalising a canonical string gives the same string, which is what lets the executor's
/// one-shot gate recompute the hash from the re-parsed arguments.
/// </summary>
public static class ProposalHash
{
    /// <summary>Canonical arguments above this many UTF-8 bytes are not queued.</summary>
    public const int MaxArgsBytes = 64 * 1024;

    private const int MaxDepth = 64;

    /// <summary>Arguments that cannot be canonicalised (duplicate keys, too deep).</summary>
    public sealed class InvalidArgumentsException(string message) : Exception(message);

    /// <summary>The canonical form of a tool call's own arguments (no <c>account</c>, no <c>approvalNote</c>).</summary>
    public static string Canonical(IReadOnlyDictionary<string, JsonElement>? arguments)
    {
        var builder = new StringBuilder();
        builder.Append('{');
        var first = true;
        foreach (var (key, value) in (arguments ?? new Dictionary<string, JsonElement>())
                 .OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!first)
                builder.Append(',');
            first = false;
            builder.Append(Key(key)).Append(':');
            Write(builder, value, 1);
        }
        builder.Append('}');
        return builder.ToString();
    }

    /// <summary>The canonical form of a JSON object given as text (re-canonicalises a stored <c>argsJson</c>).</summary>
    public static string Canonical(string json) => Canonical(Parse(json));

    /// <summary>Parses a stored <c>argsJson</c> back into tool arguments.</summary>
    public static Dictionary<string, JsonElement> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = MaxDepth + 1 });
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidArgumentsException("The arguments are not a JSON object.");
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in doc.RootElement.EnumerateObject())
        {
            if (!result.TryAdd(property.Name, property.Value.Clone()))
                throw new InvalidArgumentsException("The arguments repeat a key.");
        }
        return result;
    }

    public static string Of(string tool, string accountId, string argsJson)
    {
        var bytes = Encoding.UTF8.GetBytes($"sma-proposal-v1\n{tool}\n{accountId}\n{argsJson}");
        return Base64Url.Encode(SHA256.HashData(bytes));
    }

    public static string Of(string tool, string accountId, IReadOnlyDictionary<string, JsonElement>? arguments) =>
        Of(tool, accountId, Canonical(arguments));

    /// <summary>Constant-time comparison of two hashes as sent over the wire.</summary>
    public static bool Matches(string? expected, string? actual) =>
        expected is not null && actual is not null &&
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(actual));

    /// <summary>A well-formed hash: 32 bytes of base64url.</summary>
    public static bool IsWellFormed(string? hash) => hash is { Length: 43 } && Base64Url.Decode(hash) is { Length: 32 };

    // Keys keep their characters (only quotes, backslashes and control characters are escaped), so
    // the raw JSON the reviewer can show reads as typed. The string only ever reaches the browser as
    // text, never as HTML.
    private static readonly JsonSerializerOptions KeyEncoding = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string Key(string name) => JsonSerializer.Serialize(name, KeyEncoding);

    private static void Write(StringBuilder builder, JsonElement element, int depth)
    {
        if (depth > MaxDepth)
            throw new InvalidArgumentsException("The arguments are nested too deeply.");

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var properties = element.EnumerateObject().ToList();
                if (properties.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != properties.Count)
                    throw new InvalidArgumentsException("The arguments repeat a key.");
                builder.Append('{');
                var first = true;
                foreach (var property in properties.OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    if (!first)
                        builder.Append(',');
                    first = false;
                    builder.Append(Key(property.Name)).Append(':');
                    Write(builder, property.Value, depth + 1);
                }
                builder.Append('}');
                break;

            case JsonValueKind.Array:
                builder.Append('[');
                var firstItem = true;
                foreach (var item in element.EnumerateArray())
                {
                    if (!firstItem)
                        builder.Append(',');
                    firstItem = false;
                    Write(builder, item, depth + 1);
                }
                builder.Append(']');
                break;

            case JsonValueKind.Undefined:
                builder.Append("null");
                break;

            default:
                // Strings, numbers, true, false, null: exactly as they arrived.
                builder.Append(element.GetRawText());
                break;
        }
    }
}
