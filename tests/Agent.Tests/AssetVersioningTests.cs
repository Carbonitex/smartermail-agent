using SmarterMailAgent.Web;

namespace SmarterMailAgent.Tests;

public sealed class AssetVersioningTests
{
    [Fact]
    public void RewriteIndex_PointsCssAndJsAtTheVersionedDirectory()
    {
        var html = """
            <link rel="stylesheet" href="./css/mail-agent.css">
            <link rel="icon" href="/favicon.svg">
            <a href="../">back</a>
            <script type="module" src="./js/chat.js"></script>
            """;

        var rewritten = AssetVersioning.RewriteIndex(html, "abc123");

        Assert.Contains("href=\"./v/abc123/css/mail-agent.css\"", rewritten);
        Assert.Contains("src=\"./v/abc123/js/chat.js\"", rewritten);
        Assert.Contains("href=\"/favicon.svg\"", rewritten);
        Assert.Contains("href=\"../\"", rewritten);
    }

    [Theory]
    [InlineData("/v/abc123/js/chat.js", true, "/js/chat.js")]
    [InlineData("/v/old999/css/mail-agent.css", true, "/css/mail-agent.css")]
    [InlineData("/js/chat.js", false, "/js/chat.js")]
    [InlineData("/v/", false, "/v/")]
    [InlineData("/v//js/chat.js", false, "/v//js/chat.js")]
    [InlineData("/v/abc123", false, "/v/abc123")]
    [InlineData("/api/tools", false, "/api/tools")]
    public void TryStripVersion(string path, bool stripped, string expected)
    {
        Assert.Equal(stripped, AssetVersioning.TryStripVersion(path, out var rest));
        Assert.Equal(expected, rest);
    }

    [Fact]
    public void ComputeVersion_TracksContentAndIgnoresDev()
    {
        var root = Directory.CreateTempSubdirectory("sma-assets-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "js"));
            Directory.CreateDirectory(Path.Combine(root, "dev"));
            File.WriteAllText(Path.Combine(root, "index.html"), "<html>");
            File.WriteAllText(Path.Combine(root, "js", "chat.js"), "one");

            var first = AssetVersioning.ComputeVersion(root);
            Assert.Equal(12, first.Length);
            Assert.Equal(first, AssetVersioning.ComputeVersion(root));

            File.WriteAllText(Path.Combine(root, "dev", "stub.mjs"), "ignored");
            Assert.Equal(first, AssetVersioning.ComputeVersion(root));

            File.WriteAllText(Path.Combine(root, "js", "chat.js"), "two");
            Assert.NotEqual(first, AssetVersioning.ComputeVersion(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
