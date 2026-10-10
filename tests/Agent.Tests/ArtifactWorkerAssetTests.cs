using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace SmarterMailAgent.Tests;

/// <summary>
/// The analysis Worker is loaded with <c>new URL('./artifact-worker.js', import.meta.url)</c> from
/// subagent.js, so it resolves under the same content-versioned directory as the modules, and it imports
/// artifact-ops.js relatively from there. Both must be served from <c>/v/{version}/js/</c> as JavaScript.
/// </summary>
public sealed class ArtifactWorkerAssetTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task The_worker_and_its_import_are_served_under_the_versioned_path()
    {
        using var client = factory.CreateClient();
        var page = await client.GetStringAsync("/");
        var version = Regex.Match(page, "\"\\./v/([0-9a-f]{12})/js/chat\\.js\"").Groups[1].Value;
        Assert.NotEmpty(version);

        foreach (var file in new[] { "subagent.js", "artifact-worker.js", "artifact-ops.js", "artifacts.js" })
        {
            using var response = await client.GetAsync($"/v/{version}/js/{file}");
            Assert.True(response.IsSuccessStatusCode, $"{file}: {(int)response.StatusCode}");
            Assert.Equal("text/javascript", response.Content.Headers.ContentType?.MediaType);
            Assert.Contains("immutable", response.Headers.CacheControl?.ToString());
        }

        var subagent = await client.GetStringAsync($"/v/{version}/js/subagent.js");
        Assert.Contains("new Worker(new URL('./artifact-worker.js', import.meta.url), { type: 'module' })", subagent);
        var worker = await client.GetStringAsync($"/v/{version}/js/artifact-worker.js");
        Assert.Contains("from './artifact-ops.js'", worker);
    }
}
