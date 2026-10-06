using Microsoft.AspNetCore.Routing;
using SmarterMailMcp.Core.Models;

namespace SmarterMailMcp.Hosting;

public enum McpTransport
{
    Http,
    Stdio,
}

/// <summary>What differs between the MCP servers; everything else lives in <see cref="McpHost"/>.</summary>
public sealed class McpHostDefinition
{
    /// <summary>MCP <c>serverInfo.name</c> and the <c>/health</c> text, e.g. <c>smartermail-mcp-user</c>.</summary>
    public required string ServerName { get; init; }

    /// <summary>Env var holding the SmarterMail login, e.g. <c>SMARTERMAIL_USER</c>.</summary>
    public required string UserVariable { get; init; }

    /// <summary>Env var holding the password, e.g. <c>SMARTERMAIL_PASSWORD</c>.</summary>
    public required string PasswordVariable { get; init; }

    /// <summary>Core's user type: <c>user</c> or <c>admin</c>.</summary>
    public required string UserType { get; init; }

    /// <summary>Token file used when <c>SMARTERMAIL_TOKEN_FILE</c> is unset.</summary>
    public required string DefaultTokenFile { get; init; }

    /// <summary>
    /// Extra HTTP endpoints next to <c>/mcp</c> (HTTP transport only). Each is mapped by the host
    /// behind the same API key as <c>/mcp</c>, so a mapper only adds routes.
    /// </summary>
    public Action<IEndpointRouteBuilder, UserContext>? MapHttpEndpoints { get; init; }

    /// <summary>MCP <c>instructions</c> sent to clients at initialize, or null for none.</summary>
    public Func<McpHostSettings, string?>? Instructions { get; init; }
}

/// <summary>
/// The environment, validated up front so a misconfigured server exits with one clear message instead
/// of signing in (and possibly tripping SmarterMail's brute-force rules) first.
/// </summary>
public sealed record McpHostSettings(
    McpTransport Transport,
    string SmarterMailUrl,
    string Username,
    string Password,
    bool ReadOnly,
    string TokenFile,
    string? ApiKey,
    bool LocalFiles = false)
{
    public const string TransportVariable = "MCP_TRANSPORT";
    public const string StdioArgument = "--stdio";
    public const string UrlVariable = "SMARTERMAIL_URL";
    public const string ReadOnlyVariable = "SMARTERMAIL_READ_ONLY";
    public const string TokenFileVariable = "SMARTERMAIL_TOKEN_FILE";
    public const string ApiKeyVariable = "API_KEY";
    public const string LocalFilesVariable = "SMARTERMAIL_LOCAL_FILES";

    /// <summary>Parses the settings, or returns every problem found.</summary>
    public static (McpHostSettings? Settings, IReadOnlyList<string> Errors) Parse(
        McpHostDefinition definition, IReadOnlyList<string> args, Func<string, string?> env)
    {
        var errors = new List<string>();

        var transport = McpTransport.Http;
        var transportValue = env(TransportVariable)?.Trim().ToLowerInvariant();
        if (args.Contains(StdioArgument, StringComparer.OrdinalIgnoreCase))
            transport = McpTransport.Stdio;
        else if (transportValue is "stdio")
            transport = McpTransport.Stdio;
        else if (transportValue is not (null or "" or "http"))
            errors.Add($"{TransportVariable}='{transportValue}' is not http|stdio.");

        var url = env(UrlVariable);
        var user = env(definition.UserVariable);
        var password = env(definition.PasswordVariable);
        var missing = new[] { (UrlVariable, url), (definition.UserVariable, user), (definition.PasswordVariable, password) }
            .Where(x => string.IsNullOrWhiteSpace(x.Item2))
            .Select(x => x.Item1)
            .ToList();
        if (missing.Count > 0)
            errors.Add($"Missing {string.Join(", ", missing)}.");

        // Read-only unless explicitly turned off: a first run should never be able to change anything.
        var readOnly = true;
        var readOnlyValue = env(ReadOnlyVariable)?.Trim().ToLowerInvariant();
        if (readOnlyValue is "false" or "0" or "no")
            readOnly = false;
        else if (readOnlyValue is not (null or "" or "true" or "1" or "yes"))
            errors.Add($"{ReadOnlyVariable}='{readOnlyValue}' is not true|false.");

        var apiKey = env(ApiKeyVariable);
        if (transport == McpTransport.Http && string.IsNullOrWhiteSpace(apiKey))
            errors.Add($"{ApiKeyVariable} is required for the HTTP transport (generate one with: openssl rand -hex 32). " +
                       $"For a local client that launches the server itself, use {StdioArgument} instead.");

        // Tools may touch this process's filesystem only where the caller shares it: a stdio server the
        // client launched. Over HTTP the caller is elsewhere, so a path would name the server's files.
        var localFiles = transport == McpTransport.Stdio;
        var localFilesValue = env(LocalFilesVariable)?.Trim().ToLowerInvariant();
        if (localFilesValue is "true" or "1" or "yes")
            localFiles = true;
        else if (localFilesValue is "false" or "0" or "no")
            localFiles = false;
        else if (localFilesValue is not (null or ""))
            errors.Add($"{LocalFilesVariable}='{localFilesValue}' is not true|false.");

        var tokenFile = env(TokenFileVariable);
        if (string.IsNullOrWhiteSpace(tokenFile))
            tokenFile = definition.DefaultTokenFile;

        if (errors.Count > 0)
            return (null, errors);

        return (new McpHostSettings(transport, url!.Trim(), user!.Trim(), password!, readOnly, tokenFile,
            string.IsNullOrWhiteSpace(apiKey) ? null : apiKey, localFiles), errors);
    }
}
