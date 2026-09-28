using System.Net;

namespace SmarterMailAgent.Web;

/// <summary>
/// Optional "back to my site" link under the login form, for an agent hosted inside a larger site.
/// <c>HOME_LINK_URL</c> (http(s) or a relative path; unset = no link) and <c>HOME_LINK_TEXT</c>
/// (default "← Home"). Rendered into index.html once at startup, HTML-encoded.
/// </summary>
public sealed record HomeLink(string Url, string Text)
{
    /// <summary>Placeholder in wwwroot/index.html that <see cref="AssetVersioning.RewriteIndex"/> fills.</summary>
    public const string Marker = "<!--home-link-->";

    /// <summary>Null when no URL is set. A URL that is neither http(s) nor relative throws.</summary>
    public static HomeLink? Create(string? url, string? text)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;
        url = url.Trim();
        if (!IsAllowed(url))
            throw new InvalidOperationException($"HOME_LINK_URL must be an http(s) URL or a relative path, not '{url}'.");
        return new HomeLink(url, string.IsNullOrWhiteSpace(text) ? "← Home" : text.Trim());
    }

    public string ToHtml() =>
        $"<p><a href=\"{WebUtility.HtmlEncode(Url)}\">{WebUtility.HtmlEncode(Text)}</a></p>";

    private static bool IsAllowed(string url)
    {
        // Relative: no scheme (javascript:, data:) and not protocol-relative (//evil.example).
        if (url.StartsWith("//", StringComparison.Ordinal) || url.StartsWith('\\'))
            return false;
        var end = url.IndexOfAny(['/', '?', '#']);
        var head = end < 0 ? url : url[..end];
        if (!head.Contains(':'))
            return true;

        return Uri.TryCreate(url, UriKind.Absolute, out var absolute) &&
               (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps);
    }
}
