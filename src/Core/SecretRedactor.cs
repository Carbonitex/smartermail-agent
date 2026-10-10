using System.Text.Json;
using System.Text.Json.Nodes;

namespace SmarterMailMcp.Core;

/// <summary>
/// Blanks secrets in API responses before they become tool results (tool results go on to the
/// user's LLM provider). A non-empty string under a key containing one of the fragments
/// (case-insensitive, at any depth) becomes <c>[redacted]</c>. Matching is by substring on the key,
/// so a fragment such as <c>privatekey</c> does not touch <c>publicKey</c> (a DKIM public key is
/// published in DNS and is not a secret).
/// </summary>
public static class SecretRedactor
{
    public const string Placeholder = "[redacted]";

    /// <summary>The fragments used for administrative responses.</summary>
    public static readonly string[] DefaultFragments =
        ["password", "secret", "apikey", "privatekey", "token", "credential", "activationkey"];

    public static JsonElement Redact(JsonElement element, IReadOnlyCollection<string>? fragments = null, bool pairValues = false)
    {
        if (element.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
            return element;
        var node = JsonNode.Parse(element.GetRawText());
        RedactNode(node, fragments, pairValues);
        return JsonSerializer.SerializeToElement(node);
    }

    /// <summary>Redacts in place. With <paramref name="pairValues"/>, the <c>value</c> of a
    /// <c>{ key, value }</c> pair whose key looks secret is blanked as well.</summary>
    public static void RedactNode(JsonNode? node, IReadOnlyCollection<string>? fragments = null, bool pairValues = false)
    {
        fragments ??= DefaultFragments;
        switch (node)
        {
            case JsonObject obj:
                var pairKey = pairValues && obj["key"] is JsonValue k && k.GetValueKind() == JsonValueKind.String
                    ? k.GetValue<string>() : null;
                foreach (var key in obj.Select(p => p.Key).ToList())
                {
                    var value = obj[key];
                    var secret = IsSecretKey(key, fragments) ||
                                 (pairKey is not null && key.Equals("value", StringComparison.OrdinalIgnoreCase) && IsSecretKey(pairKey, fragments));
                    if (secret && value is JsonValue v && v.GetValueKind() == JsonValueKind.String &&
                        !string.IsNullOrEmpty(v.GetValue<string>()))
                        obj[key] = Placeholder;
                    else
                        RedactNode(value, fragments, pairValues);
                }
                break;
            case JsonArray array:
                foreach (var item in array)
                    RedactNode(item, fragments, pairValues);
                break;
        }
    }

    public static bool IsSecretKey(string key, IReadOnlyCollection<string> fragments) =>
        fragments.Any(f => key.Contains(f, StringComparison.OrdinalIgnoreCase));
}
