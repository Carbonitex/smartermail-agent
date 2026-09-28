using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SmarterMailMcp.Hosting;

/// <summary>Options for <see cref="ApiKeyAuthenticationHandler"/>: the one key this server accepts.</summary>
public sealed class ApiKeyAuthenticationOptions : AuthenticationSchemeOptions
{
    public string ApiKey { get; set; } = "";
}

/// <summary>
/// HTTP transport only. The key is accepted, in this order, from <c>X-API-Key</c>,
/// <c>Authorization: Bearer</c>, or the <c>apiKey</c> query parameter (last resort: query strings
/// end up in proxy and access logs). Only the first one present is checked.
/// </summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<ApiKeyAuthenticationOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<ApiKeyAuthenticationOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? providedKey = null;
        if (Request.Headers.TryGetValue("X-API-Key", out var apiKeyHeader))
            providedKey = apiKeyHeader.ToString();
        else if (Request.Headers.TryGetValue("Authorization", out var authHeader))
        {
            var header = authHeader.ToString();
            if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                providedKey = header["Bearer ".Length..].Trim();
        }
        else if (Request.Query.TryGetValue("apiKey", out var apiKeyQuery))
            providedKey = apiKeyQuery.ToString();

        if (string.IsNullOrEmpty(providedKey))
            return Task.FromResult(AuthenticateResult.NoResult());

        // McpHost refuses to start in HTTP mode without a key; this is a second check.
        if (string.IsNullOrEmpty(Options.ApiKey))
            return Task.FromResult(AuthenticateResult.Fail("API Key not configured"));

        if (!CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(providedKey),
            Encoding.UTF8.GetBytes(Options.ApiKey)))
            return Task.FromResult(AuthenticateResult.Fail("Invalid API key"));

        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "mcp"), new Claim(ClaimTypes.Role, "Client")], Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}
