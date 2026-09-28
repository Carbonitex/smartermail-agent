using SmarterMailAgent.Web;

namespace SmarterMailAgent.Tests;

public sealed class HomeLinkTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Unset_means_no_link(string? url) => Assert.Null(HomeLink.Create(url, "text"));

    [Theory]
    [InlineData("../")]
    [InlineData("/")]
    [InlineData("/home?x=a:b")]
    [InlineData("https://example.com/")]
    [InlineData("http://example.com:8080/a")]
    public void Http_and_relative_urls_are_allowed(string url) => Assert.NotNull(HomeLink.Create(url, null));

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("JavaScript:alert(1)")]
    [InlineData("data:text/html,hi")]
    [InlineData("//evil.example/")]
    [InlineData("\\\\evil.example")]
    [InlineData("ftp://example.com/")]
    public void Other_schemes_and_protocol_relative_urls_throw(string url) =>
        Assert.Throws<InvalidOperationException>(() => HomeLink.Create(url, null));

    [Fact]
    public void Text_defaults_and_everything_is_encoded()
    {
        Assert.Equal("← Home", HomeLink.Create("../", null)!.Text);
        Assert.Equal("<p><a href=\"/a?b=1&amp;c=&quot;2&quot;\">&lt;b&gt;x&lt;/b&gt;</a></p>",
            HomeLink.Create("/a?b=1&c=\"2\"", "<b>x</b>")!.ToHtml());
    }

    [Fact]
    public void RewriteIndex_fills_or_drops_the_marker()
    {
        var html = $"<footer>{HomeLink.Marker}</footer>";
        Assert.Equal("<footer></footer>", AssetVersioning.RewriteIndex(html, "v1"));
        Assert.Equal("<footer><p><a href=\"../\">← Back</a></p></footer>",
            AssetVersioning.RewriteIndex(html, "v1", HomeLink.Create("../", "← Back")));
    }
}
