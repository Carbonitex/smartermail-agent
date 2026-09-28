using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using SmarterMailMcp.Core.Models;
using SmarterMailMcp.Core;

namespace SmarterMailMcp.Core.Auth;

public class AuthenticationService
{
    private readonly GlobalContext _globalContext;
    private readonly HttpClient _httpClient;

    public AuthenticationService(GlobalContext globalContext)
    {
        _globalContext = globalContext;
        _httpClient = new HttpClient();
    }

    /// <summary>
    /// SmarterMail stores one token per (user, clientId) pair: authenticating again with the same
    /// clientId invalidates the previous token. Every sign-in therefore gets its own clientId, so two
    /// instances on the same account (or a restart racing an old token) do not evict each other.
    /// Refresh does not depend on it: SmarterMail reads the clientId from the refresh token's claim.
    /// </summary>
    public static string NewClientId() =>
        "smartermail-mcp-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();

    public bool CheckAuthentication()
    {
        if (!File.Exists(_globalContext.TokenFilePath))
        {
            return false;
        }

        var tokenData = _globalContext.ReadTokenFile();
        if (tokenData == null)
        {
            return false;
        }

        // Check if required fields exist
        if (string.IsNullOrEmpty(tokenData.AccessToken) || string.IsNullOrEmpty(tokenData.BaseUrl))
        {
            return false;
        }

        // Check for refresh token expiration first - this is the critical check
        // If refresh token is expired, we can't recover and need to re-authenticate
        if (!string.IsNullOrEmpty(tokenData.RefreshExpiration))
        {
            try
            {
                var refreshExpiration = DateTime.Parse(tokenData.RefreshExpiration);
                var currentTime = DateTime.Now;

                if (currentTime >= refreshExpiration)
                {
                    Console.Error.WriteLine("Refresh token has expired. Need to re-authenticate.");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Warning: Could not parse refresh token expiration date: {ex.Message}");
            }
        }

        return true;
    }

    public async Task<(bool Success, string Message, TokenData? TokenData)> AuthenticateAsync(
        string baseUrl,
        string username,
        string password,
        bool readOnlyMode = true,
        string userType = "user")
    {
        try
        {
            var apiUrl = $"{baseUrl.TrimEnd('/')}/api/v1/auth/authenticate-user";
            var clientId = NewClientId();

            var payload = new
            {
                username,
                password,
                clientId
            };

            var response = await _httpClient.PostAsJsonAsync(apiUrl, payload);

            if (!response.IsSuccessStatusCode)
            {
                var errorMessage = await response.Content.ReadAsStringAsync();
                return (false, $"Authentication failed: {response.StatusCode} - {errorMessage}", null);
            }

            var data = await response.Content.ReadFromJsonAsync<JsonElement>();

            if (data.TryGetProperty("accessToken", out var accessTokenElement))
            {
                var tokenData = new TokenData
                {
                    AccessToken = accessTokenElement.GetString(),
                    BaseUrl = baseUrl,
                    Username = username,
                    ReadOnlyMode = readOnlyMode,
                    Method = "simple",
                    UserType = userType,
                    ClientId = clientId
                };

                if (data.TryGetProperty("refreshToken", out var refreshTokenElement))
                {
                    tokenData.RefreshToken = refreshTokenElement.GetString();
                }

                if (data.TryGetProperty("accessTokenExpiration", out var expirationElement))
                {
                    tokenData.Expiration = expirationElement.GetString();
                }

                if (data.TryGetProperty("refreshTokenExpiration", out var refreshExpirationElement))
                {
                    tokenData.RefreshExpiration = refreshExpirationElement.GetString();
                }

                // Save to file
                if (_globalContext.WriteTokenFile(tokenData))
                {
                    return (true, "Authentication successful", tokenData);
                }
                else
                {
                    return (false, "Failed to save token file", null);
                }
            }
            else
            {
                return (false, "No access token in response", null);
            }
        }
        catch (HttpRequestException ex)
        {
            var errorMessage = "Could not connect to the SmarterMail server. Check the Base URL and network connection.";
            Console.Error.WriteLine($"Authentication error: {ex.Message}");
            return (false, errorMessage, null);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Unexpected authentication error: {ex.Message}");
            return (false, $"An unexpected error occurred: {ex.Message}", null);
        }
    }

    /// <summary>
    /// Same request as <see cref="AuthenticateAsync"/>, but tells an authoritative rejection apart from
    /// a transient failure (see <see cref="AuthResponseClassifier"/>) so callers such as
    /// <see cref="StartupSignIn"/> know whether retrying makes sense. Unlike the legacy method it does
    /// not accept a two-factor / password-change AuthStep token as a login. Saves the token file on success.
    /// </summary>
    public async Task<AuthenticationResult> AuthenticateWithResultAsync(
        string baseUrl,
        string username,
        string password,
        bool readOnlyMode = true,
        string userType = "user",
        CancellationToken cancellationToken = default)
    {
        int status;
        JsonElement data = default;
        var clientId = NewClientId();
        try
        {
            var apiUrl = $"{baseUrl.TrimEnd('/')}/api/v1/auth/authenticate-user";
            var payload = new { username, password, clientId };

            using var response = await _httpClient.PostAsJsonAsync(apiUrl, payload, cancellationToken);
            status = (int)response.StatusCode;
            try
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!string.IsNullOrWhiteSpace(body))
                {
                    using var doc = JsonDocument.Parse(body);
                    data = doc.RootElement.Clone();
                }
            }
            catch (JsonException)
            {
                // Not JSON; the classifier treats that as transient.
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            return new AuthenticationResult(AuthResultKind.Transient,
                $"Could not connect to the SmarterMail server ({ex.Message})", Code: "CONNECTION_FAILED");
        }
        catch (TaskCanceledException)
        {
            return new AuthenticationResult(AuthResultKind.Transient,
                "The SmarterMail server did not respond in time", Code: "TIMEOUT");
        }
        catch (Exception ex)
        {
            return new AuthenticationResult(AuthResultKind.Transient,
                $"Unexpected authentication error: {ex.GetType().Name}: {ex.Message}", Code: "EXCEPTION");
        }

        var (kind, code) = AuthResponseClassifier.Classify(status, data);
        switch (kind)
        {
            case AuthResultKind.Rejected:
                return new AuthenticationResult(kind, $"SmarterMail rejected the sign-in: {code} (HTTP {status})",
                    HttpStatus: status, Code: code);
            case AuthResultKind.Transient:
                return new AuthenticationResult(kind, $"SmarterMail answered HTTP {status} ({code})",
                    HttpStatus: status, Code: code);
        }

        var tokenData = new TokenData
        {
            AccessToken = AuthResponseClassifier.Str(data, "accessToken"),
            BaseUrl = baseUrl,
            Username = username,
            ReadOnlyMode = readOnlyMode,
            Method = "simple",
            UserType = userType,
            ClientId = clientId,
            RefreshToken = AuthResponseClassifier.Str(data, "refreshToken"),
            Expiration = AuthResponseClassifier.Str(data, "accessTokenExpiration"),
            RefreshExpiration = AuthResponseClassifier.Str(data, "refreshTokenExpiration"),
        };

        return _globalContext.WriteTokenFile(tokenData)
            ? new AuthenticationResult(AuthResultKind.Success, "Authentication successful", tokenData, status)
            : new AuthenticationResult(AuthResultKind.Transient, "Failed to save token file", HttpStatus: status,
                Code: "TOKEN_FILE_WRITE_FAILED");
    }

    public async Task<bool> RefreshTokenAsync(TokenData tokenData)
    {
        if (string.IsNullOrEmpty(tokenData.RefreshToken) || string.IsNullOrEmpty(tokenData.BaseUrl))
        {
            Console.Error.WriteLine("Cannot refresh token: Missing refresh token or base URL.");
            return false;
        }

        // Check if refresh token is expired
        if (!string.IsNullOrEmpty(tokenData.RefreshExpiration))
        {
            try
            {
                var refreshExpiration = DateTime.Parse(tokenData.RefreshExpiration);
                var currentTime = DateTime.Now;

                if (currentTime >= refreshExpiration)
                {
                    Console.Error.WriteLine("Refresh token has expired. Need to re-authenticate.");
                    return false;
                }
            }
            catch (Exception)
            {
                // If parsing fails, try the refresh anyway
            }
        }

        try
        {
            var apiUrl = $"{tokenData.BaseUrl}/api/v1/auth/refresh-token";
            var payload = new
            {
                token = tokenData.RefreshToken,
                clientId = tokenData.ClientId
            };

            var response = await _httpClient.PostAsJsonAsync(apiUrl, payload);
            response.EnsureSuccessStatusCode();

            var data = await response.Content.ReadFromJsonAsync<JsonElement>();

            if (data.TryGetProperty("accessToken", out var accessTokenElement))
            {
                Logging.Log("Received new access token from server");

                // Update token data with new tokens
                tokenData.AccessToken = accessTokenElement.GetString();

                if (data.TryGetProperty("refreshToken", out var refreshTokenElement))
                {
                    tokenData.RefreshToken = refreshTokenElement.GetString();
                }

                if (data.TryGetProperty("accessTokenExpiration", out var expirationElement))
                {
                    tokenData.Expiration = expirationElement.GetString();
                }

                if (data.TryGetProperty("refreshTokenExpiration", out var refreshExpirationElement))
                {
                    tokenData.RefreshExpiration = refreshExpirationElement.GetString();
                }

                // Save updated tokens
                if (_globalContext.WriteTokenFile(tokenData))
                {
                    Logging.Log("Token refreshed successfully.");
                    return true;
                }
                else
                {
                    Console.Error.WriteLine("Failed to write refreshed token to file.");
                    return false;
                }
            }
            else
            {
                Console.Error.WriteLine("Token refresh failed: No access token in response.");
                return false;
            }
        }
        catch (HttpRequestException ex)
        {
            Console.Error.WriteLine($"Token refresh failed: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"An unexpected error occurred during token refresh: {ex.Message}");
            return false;
        }
    }
}
