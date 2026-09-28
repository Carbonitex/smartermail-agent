using System.Text.Json;
using System.Text.RegularExpressions;
using SmarterMailMcp.Core.Auth;
using SmarterMailMcp.Core.Models;

namespace SmarterMail.Tests;

/// <summary>
/// SmarterMail keeps one token per (user, clientId), so every Core sign-in must use a fresh clientId
/// (<see cref="AuthenticationService.NewClientId"/>) and remember it in TokenData.
/// </summary>
public sealed partial class ClientIdTests : IDisposable
{
    private const string LoginOk = """{"success":true,"accessToken":"t1","refreshToken":"r1"}""";
    private readonly string _tokenFile = Path.Combine(Path.GetTempPath(), $"sma-clientid-test-{Guid.NewGuid():N}.json");

    public void Dispose() => File.Delete(_tokenFile);

    [GeneratedRegex("^smartermail-mcp-[0-9a-f]{16}$")]
    private static partial Regex ClientIdFormat();

    [Fact]
    public void New_client_ids_are_prefixed_lowercase_hex_and_unique()
    {
        var ids = Enumerable.Range(0, 1000).Select(_ => AuthenticationService.NewClientId()).ToList();

        Assert.All(ids, id => Assert.Matches(ClientIdFormat(), id));
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public async Task AuthenticateWithResult_sends_a_fresh_client_id_and_stores_it()
    {
        using var server = new FakeSmarterMail((_, _) => (200, LoginOk));
        var globalContext = new GlobalContext(_tokenFile);
        var auth = new AuthenticationService(globalContext);

        var first = await auth.AuthenticateWithResultAsync(server.BaseUrl, "user@example.com", "pw");
        var second = await auth.AuthenticateWithResultAsync(server.BaseUrl, "user@example.com", "pw");

        Assert.Equal(AuthResultKind.Success, first.Kind);
        Assert.Equal(AuthResultKind.Success, second.Kind);
        var sent = SentClientIds(server, "/authenticate-user");
        Assert.Equal(new List<string?> { first.TokenData!.ClientId, second.TokenData!.ClientId }, sent);
        Assert.All(sent, id => Assert.Matches(ClientIdFormat(), id));
        Assert.NotEqual(sent[0], sent[1]);
        Assert.Equal(second.TokenData.ClientId, globalContext.ReadTokenFile()!.ClientId);
    }

    [Fact]
    public async Task Legacy_Authenticate_sends_a_fresh_client_id_and_stores_it()
    {
        using var server = new FakeSmarterMail((_, _) => (200, LoginOk));
        var globalContext = new GlobalContext(_tokenFile);

        var (success, _, tokenData) = await new AuthenticationService(globalContext)
            .AuthenticateAsync(server.BaseUrl, "user@example.com", "pw");

        Assert.True(success);
        var sent = Assert.Single(SentClientIds(server, "/authenticate-user"));
        Assert.Matches(ClientIdFormat(), sent);
        Assert.Equal(sent, tokenData!.ClientId);
        Assert.Equal(sent, globalContext.ReadTokenFile()!.ClientId);
    }

    [Theory]
    [InlineData("smartermail-mcp")] // token file written before per-sign-in client ids
    [InlineData(null)]
    public async Task Refresh_of_a_persisted_token_passes_its_stored_client_id_through(string? stored)
    {
        using var server = new FakeSmarterMail((_, _) => (200, """{"accessToken":"t2","refreshToken":"r2"}"""));
        var globalContext = new GlobalContext(_tokenFile);
        Assert.True(globalContext.WriteTokenFile(new TokenData
        {
            AccessToken = "t1", RefreshToken = "r1", BaseUrl = server.BaseUrl, ClientId = stored,
        }));
        var tokenData = globalContext.ReadTokenFile()!;

        Assert.True(await new AuthenticationService(globalContext).RefreshTokenAsync(tokenData));

        Assert.Equal(stored, Assert.Single(SentClientIds(server, "/refresh-token")));
        var reloaded = globalContext.ReadTokenFile()!;
        Assert.Equal("t2", reloaded.AccessToken);
        Assert.Equal(stored, reloaded.ClientId);
    }

    private static List<string?> SentClientIds(FakeSmarterMail server, string pathSuffix) =>
        server.Requests
            .Where(r => r.Path.EndsWith(pathSuffix))
            .Select(r => JsonDocument.Parse(r.Body).RootElement.GetProperty("clientId").GetString())
            .ToList();
}
