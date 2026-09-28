using System.Reflection;
using SmarterMailAgent.Auth;
using SmarterMailMcp.Core.Models;

namespace SmarterMailAgent.Tests;

/// <summary>
/// UserContextFactory writes Core's private UserContext fields by reflection. These tests pin
/// that coupling to src/Core, so renaming one of those fields fails here instead of on the
/// agent's first login.
/// </summary>
public sealed class UserContextFactoryTests
{
    private static GlobalContext NeverWrittenGlobalContext() =>
        new(Path.Combine(Path.GetTempPath(), $"sma-never-{Guid.NewGuid():N}.json"), readOnlyMode: true);

    private static object? Private(UserContext userContext, string name) =>
        typeof(UserContext).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(userContext);

    [Fact]
    public void Create_WritesCoresPrivateState()
    {
        var token = new TokenData
        {
            AccessToken = "access-1",
            BaseUrl = "https://mail.example.com/",
            Username = "alice@example.com",
            ReadOnlyMode = false,
            UserType = "domainadmin",
        };

        using var userContext = UserContextFactory.Create(NeverWrittenGlobalContext(), token);

        Assert.Equal("access-1", Private(userContext, "_accessToken"));
        Assert.Equal("https://mail.example.com", Private(userContext, "_baseUrl"));
        Assert.Equal(true, Private(userContext, "_isConnected"));
        var http = Assert.IsType<HttpClient>(Private(userContext, "_httpClient"));
        Assert.Equal(new Uri("https://mail.example.com"), http.BaseAddress);
        Assert.Equal("alice", userContext.Username);
        Assert.Equal("example.com", userContext.Domain);
        Assert.False(userContext.ReadOnlyMode);
    }

    [Fact]
    public void ApplyRefreshedToken_ReplacesAccessTokenAndStampsRefreshClock()
    {
        using var userContext = UserContextFactory.Create(NeverWrittenGlobalContext(), new TokenData
        {
            AccessToken = "old",
            BaseUrl = "https://mail.example.com",
            Username = "bob@example.com",
        });
        var before = DateTime.Now;

        UserContextFactory.ApplyRefreshedToken(userContext, new TokenData { AccessToken = "new" });

        Assert.Equal("new", Private(userContext, "_accessToken"));
        Assert.True((DateTime)Private(userContext, "_lastTokenRefresh")! >= before);
    }
}
