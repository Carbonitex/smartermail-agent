using System.Security.Cryptography;
using System.Text;

namespace SmarterMailAgent.Web;

/// <summary>
/// Content-versioned asset URLs. Cloudflare applies its own day-long browser TTL to .js/.css and
/// ignores the origin's <c>no-cache</c>, so a browser could keep yesterday's modules after a deploy.
/// Instead, index.html is served with its <c>./css/</c> and <c>./js/</c> references rewritten to
/// <c>./v/{version}/css/</c> and <c>./v/{version}/js/</c>, where the version is a hash of wwwroot.
/// The ES modules import each other with relative paths (<c>./api.js</c>), so every import lands
/// under the same versioned directory without touching the JS. A deploy that changes any asset
/// changes every asset URL; unchanged content keeps its URLs (and caches) across restarts.
/// </summary>
public static class AssetVersioning
{
    public const string Prefix = "/v/";

    /// <summary>Short hex hash over every shipped file's relative path and bytes (wwwroot/dev excluded).</summary>
    public static string ComputeVersion(string webRoot)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var files = Directory.EnumerateFiles(webRoot, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(webRoot, f).Replace('\\', '/'))
            .Where(rel => !rel.StartsWith("dev/", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal);
        foreach (var rel in files)
        {
            sha.AppendData(Encoding.UTF8.GetBytes(rel + "\n"));
            sha.AppendData(File.ReadAllBytes(Path.Combine(webRoot, rel)));
        }
        return Convert.ToHexString(sha.GetHashAndReset())[..12].ToLowerInvariant();
    }

    /// <summary>
    /// Points index.html's relative css/js references at the versioned directory, and fills (or
    /// drops) the optional <see cref="HomeLink"/> placeholder.
    /// </summary>
    public static string RewriteIndex(string html, string version, HomeLink? homeLink = null) => html
        .Replace("\"./css/", $"\"./v/{version}/css/", StringComparison.Ordinal)
        .Replace("\"./js/", $"\"./v/{version}/js/", StringComparison.Ordinal)
        .Replace(HomeLink.Marker, homeLink?.ToHtml() ?? "", StringComparison.Ordinal);

    /// <summary>
    /// <c>/v/{anything}/rest</c> → <c>/rest</c>. Any version segment is accepted and served with the
    /// current files: only a stale page asks for an old version, and new content beats a 404.
    /// </summary>
    public static bool TryStripVersion(string path, out string rest)
    {
        rest = path;
        if (!path.StartsWith(Prefix, StringComparison.Ordinal))
            return false;
        var slash = path.IndexOf('/', Prefix.Length);
        if (slash <= Prefix.Length)
            return false;
        rest = path[slash..];
        return true;
    }
}
