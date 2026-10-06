using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using SmarterMailMcp.Core;
using SmarterMailMcp.Core.Auth;

namespace SmarterMailMcp.Core.Models;

public class UserContext : IDisposable, IAsyncDisposable
{
    private HttpClient _httpClient;
    private readonly GlobalContext _globalContext;
    private string _accessToken = string.Empty;
    private string _baseUrl = string.Empty;
    private bool _isConnected;
    private readonly object _refreshGate = new();
    private Task<bool>? _inflightRefresh;
    private DateTime _lastTokenRefresh;
    private readonly TimeSpan _tokenRefreshInterval = TimeSpan.FromMinutes(10);
    private AuthenticationService? _authService;

    public UserContext(GlobalContext globalContext)
    {
        _globalContext = globalContext;
        _httpClient = new HttpClient(); // Initialize with empty client to avoid NullReferenceException
    }

    public string Username { get; set; } = string.Empty;
    public string Domain { get; set; } = string.Empty;
    public string EmailAddress { get; set; } = string.Empty;
    public bool ReadOnlyMode { get; set; } = true;
    public string UserType { get; set; } = "user";

    /// <summary>
    /// Sets the authentication service for fallback re-authentication when token refresh fails.
    /// </summary>
    public void SetAuthenticationService(AuthenticationService authService)
    {
        _authService = authService;
    }

    public void InitializeFromFile(GlobalContext globalContext)
    {
        Logging.Log("[DB] Initializing from file...");
        var tokenData = _globalContext.ReadTokenFile();
        if (tokenData != null)
        {
            Logging.Log("[DB] Token data found");
            ReadOnlyMode = tokenData.ReadOnlyMode;  
            UserType = tokenData.UserType ?? "user";
            EmailAddress = tokenData.Username ?? string.Empty;

            if (EmailAddress.Contains("@"))
            {
                var parts = EmailAddress.Split('@');
                Username = parts[0];
                Domain = parts[1];
            }
            else
            {
                Username = EmailAddress;
            }
            
            _accessToken = tokenData.AccessToken ?? string.Empty;
            _baseUrl = tokenData.BaseUrl?.TrimEnd('/') ?? string.Empty;
            _isConnected = true;
            _lastTokenRefresh = DateTime.MinValue;
            
            if (!string.IsNullOrEmpty(_baseUrl))
            {
                _httpClient = new HttpClient
                {
                    BaseAddress = new Uri(_baseUrl)
                };
            }
            
            Logging.Log($"[DB] Instance created for base URL: {_baseUrl}");
        } 
        else
        {
            Logging.Log("[DB] No token data found");
            _accessToken = string.Empty;
            _baseUrl = string.Empty;
            _isConnected = false;
            _lastTokenRefresh = DateTime.MinValue;
            
            // Create a dummy HttpClient to avoid null reference
            _httpClient = new HttpClient();
            
            Logging.Log("[DB] Instance created but not authenticated");
        }
    }
    
    /// <summary>
    /// Refresh now. Concurrent callers share one in-flight refresh instead of racing.
    /// </summary>
    public Task<bool> RefreshTokenAsync() => StartOrJoinRefreshAsync(force: true);

    private Task<bool> StartOrJoinRefreshAsync(bool force)
    {
        lock (_refreshGate)
        {
            if (!force && DateTime.Now - _lastTokenRefresh < _tokenRefreshInterval)
                return Task.FromResult(true);

            if (_inflightRefresh is { IsCompleted: false })
            {
                Logging.Log("[DB] Token refresh already in progress, waiting.");
                return _inflightRefresh;
            }

            _inflightRefresh = RefreshTokenCoreAsync();
            return _inflightRefresh;
        }
    }

    private async Task<bool> RefreshTokenCoreAsync()
    {
        var tokenData = _globalContext.ReadTokenFile();
        if (tokenData == null || string.IsNullOrEmpty(tokenData.RefreshToken))
        {
            Console.Error.WriteLine("[DB] No valid token data for refresh.");
            return await TryReauthenticateAsync();
        }

        // Call the refresh token endpoint directly without going through ExecuteAsync
        // to avoid triggering EnsureTokenFreshAsync and causing a deadlock
        var payload = new
        {
            token = tokenData.RefreshToken,
            clientId = tokenData.ClientId
        };

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/refresh-token")
            {
                Content = JsonContent.Create(payload)
            };
            using var response = await _httpClient.SendAsync(request);

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<JsonElement>();

                if (result.TryGetProperty("accessToken", out var newAccessToken))
                {
                    _accessToken = newAccessToken.GetString() ?? string.Empty;

                    tokenData.AccessToken = _accessToken;

                    if (result.TryGetProperty("refreshToken", out var newRefreshToken))
                    {
                        tokenData.RefreshToken = newRefreshToken.GetString();
                    }
                    if (result.TryGetProperty("accessTokenExpiration", out var newExpiration))
                    {
                        tokenData.Expiration = newExpiration.GetString();
                    }
                    if (result.TryGetProperty("refreshTokenExpiration", out var newRefreshExpiration))
                    {
                        tokenData.RefreshExpiration = newRefreshExpiration.GetString();
                    }

                    _globalContext.WriteTokenFile(tokenData);

                    _lastTokenRefresh = DateTime.Now;
                    Logging.Log("[DB] Token refreshed successfully.");
                    return true;
                }
            }

            Console.Error.WriteLine("[DB] Token refresh failed, attempting re-authentication fallback.");
            return await TryReauthenticateAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[DB] Token refresh error: {ex.Message}");
            Console.Error.WriteLine("[DB] Attempting re-authentication fallback.");
            return await TryReauthenticateAsync();
        }
    }

    /// <summary>
    /// Fallback: attempt full re-authentication using environment variables when token refresh fails.
    /// </summary>
    private async Task<bool> TryReauthenticateAsync()
    {
        if (_authService == null)
        {
            Console.Error.WriteLine("[DB] No AuthenticationService available for re-authentication fallback.");
            return false;
        }

        var baseUrl = Environment.GetEnvironmentVariable("SMARTERMAIL_URL");
        var user = Environment.GetEnvironmentVariable("SMARTERMAIL_USER")
                   ?? Environment.GetEnvironmentVariable("SMARTERMAIL_ADMIN_USER");
        var pass = Environment.GetEnvironmentVariable("SMARTERMAIL_PASSWORD")
                   ?? Environment.GetEnvironmentVariable("SMARTERMAIL_ADMIN_PASSWORD");

        if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(user) || string.IsNullOrEmpty(pass))
        {
            Console.Error.WriteLine("[DB] Re-authentication fallback failed: missing environment variables (SMARTERMAIL_URL, SMARTERMAIL_USER/SMARTERMAIL_ADMIN_USER, SMARTERMAIL_PASSWORD/SMARTERMAIL_ADMIN_PASSWORD).");
            return false;
        }

        try
        {
            Logging.Log("[DB] Attempting full re-authentication...");
            // Keep the mode this context was signed in with (the host decided it at startup).
            var (success, message, newTokenData) = await _authService.AuthenticateAsync(
                baseUrl, user, pass, readOnlyMode: ReadOnlyMode, userType: UserType);

            if (success && newTokenData != null)
            {
                _accessToken = newTokenData.AccessToken ?? string.Empty;
                _baseUrl = newTokenData.BaseUrl?.TrimEnd('/') ?? string.Empty;
                if (!string.IsNullOrEmpty(_baseUrl))
                    _httpClient.BaseAddress = new Uri(_baseUrl);
                _lastTokenRefresh = DateTime.Now;
                Logging.Log("[DB] Re-authentication successful. Token recovered.");
                return true;
            }

            Console.Error.WriteLine($"[DB] Re-authentication failed: {message}");
            return false;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[DB] Re-authentication error: {ex.Message}");
            return false;
        }
    }

    private async Task EnsureTokenFreshAsync()
    {
        var success = await StartOrJoinRefreshAsync(force: false);
        if (!success)
        {
            Console.Error.WriteLine("[DB] Auto token refresh failed");
        }
    }

    /// <summary>
    /// Sends an authenticated request. Concurrent callers wait for any in-flight token
    /// refresh. On 401, refresh once (or reuse a refresh another caller already did)
    /// and retry the request.
    /// </summary>
    private async Task<HttpResponseMessage> ExecuteAsync(
        Func<HttpRequestMessage> createRequest,
        string methodLabel,
        string endpoint,
        bool retryOnUnauthorized = true)
    {
        await EnsureTokenFreshAsync();

        var tokenUsed = _accessToken;
        var response = await SendOnceAsync(createRequest, tokenUsed);

        if (response.StatusCode == HttpStatusCode.Unauthorized && retryOnUnauthorized)
        {
            var firstBody = await response.Content.ReadAsStringAsync();
            response.Dispose();
            Logging.Log($"[DB] {methodLabel} {endpoint} returned 401, refreshing token and retrying.");

            if (_accessToken == tokenUsed)
            {
                var refreshed = await StartOrJoinRefreshAsync(force: true);
                if (!refreshed)
                {
                    throw new SmarterMailApiException(
                        "API call failed: Unauthorized (token refresh failed)",
                        HttpStatusCode.Unauthorized,
                        firstBody,
                        endpoint);
                }
            }

            response = await SendOnceAsync(createRequest, _accessToken);
        }

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            var status = response.StatusCode;
            response.Dispose();
            Console.Error.WriteLine($"[DB] {methodLabel} request failed: {status}");
            Console.Error.WriteLine($"[DB] Error response body: {errorBody}");
            throw new SmarterMailApiException(
                $"API call failed: {status}",
                status,
                errorBody,
                endpoint);
        }

        return response;
    }

    private async Task<HttpResponseMessage> SendOnceAsync(Func<HttpRequestMessage> createRequest, string token)
    {
        using var request = createRequest();
        if (!string.IsNullOrEmpty(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _httpClient.SendAsync(request);
    }

    public void Logout()
    {
        _globalContext.DeleteTokenFile();
        _accessToken = string.Empty;
    }

    public async Task<T?> GetAsync<T>(string endpoint)
    {
        using var response = await ExecuteAsync(
            () => new HttpRequestMessage(HttpMethod.Get, endpoint),
            "GET", endpoint);
        return await response.Content.ReadFromJsonAsync<T>();
    }

    public async Task<T?> PostAsync<T>(string endpoint, object? data = null)
    {
        using var response = await ExecuteAsync(
            () => new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = JsonContent.Create(data ?? new { })
            },
            "POST", endpoint);
        return await response.Content.ReadFromJsonAsync<T>();
    }

    public async Task<string> PostAsyncRaw(string endpoint, object? data = null)
    {
        using var response = await ExecuteAsync(
            () => new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = JsonContent.Create(data ?? new { })
            },
            "POST", endpoint);
        return await response.Content.ReadAsStringAsync();
    }

    public async Task<T?> PutAsync<T>(string endpoint, object data)
    {
        using var response = await ExecuteAsync(
            () => new HttpRequestMessage(HttpMethod.Put, endpoint)
            {
                Content = JsonContent.Create(data)
            },
            "PUT", endpoint);
        return await response.Content.ReadFromJsonAsync<T>();
    }

    public async Task<bool> PostWithoutResponseAsync(string endpoint, object? data = null)
    {
        using var response = await ExecuteAsync(
            () => new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = JsonContent.Create(data ?? new { })
            },
            "POST", endpoint);
        return true;
    }

    public async Task<bool> DeleteAsync(string endpoint)
    {
        using var response = await ExecuteAsync(
            () => new HttpRequestMessage(HttpMethod.Delete, endpoint),
            "DELETE", endpoint);
        return true;
    }

    public async Task<(byte[] Data, string ContentType)> GetBytesAsync(string endpoint)
    {
        using var response = await ExecuteAsync(
            () => new HttpRequestMessage(HttpMethod.Get, endpoint),
            "GET", endpoint);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
        return (bytes, contentType);
    }

    public async Task<(byte[] Data, string ContentType)> PostBytesAsync(string endpoint, object? data = null)
    {
        using var response = await ExecuteAsync(
            () => new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = JsonContent.Create(data ?? new { })
            },
            "POST", endpoint);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
        return (bytes, contentType);
    }

    public async Task<T?> PostMultipartAsync<T>(string endpoint, MultipartFormDataContent content)
    {
        // Multipart content cannot be resent after a 401, so wait for a fresh token
        // up front and skip the retry used by other verbs.
        await EnsureTokenFreshAsync();

        var baseUrl = _baseUrl.TrimEnd('/');
        var path = endpoint.TrimStart('/');
        var fullUri = new Uri($"{baseUrl}/{path}");

        using var request = new HttpRequestMessage(HttpMethod.Post, fullUri)
        {
            Content = content
        };
        if (!string.IsNullOrEmpty(_accessToken))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);

        var response = await _httpClient.SendAsync(request);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            Logging.Log($"[DB] Multipart POST {endpoint} returned 401, refreshing token for subsequent calls.");
            await StartOrJoinRefreshAsync(force: true);
        }

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            Console.Error.WriteLine($"[DB] Multipart POST request failed: {response.StatusCode}");
            Console.Error.WriteLine($"[DB] Error response body: {errorBody}");
            throw new SmarterMailApiException(
                $"API call failed: {response.StatusCode}",
                response.StatusCode,
                errorBody,
                endpoint);
        }

        // SmarterMail's resumable /api/upload answers every chunk but the last with an empty body.
        var body = await response.Content.ReadAsStringAsync();
        return string.IsNullOrWhiteSpace(body) ? default : JsonSerializer.Deserialize<T>(body, JsonSerializerOptions.Web);
    }

    public async Task<JsonElement> GetUserInfoAsync()
    {
        var response = await GetAsync<JsonElement>("/api/v1/settings/user-startup-data");
        if (response.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            throw new SmarterMailApiException(
                "Empty response from user-startup-data",
                HttpStatusCode.OK,
                string.Empty,
                "/api/v1/settings/user-startup-data");
        }

        return response;
    }

    public async Task<string?> GetFolderPathFromFolderIdAsync(string folderId, string username, string domain)
    {
        try
        {
            if (string.IsNullOrEmpty(folderId))
            {
                Console.Error.WriteLine("[DB] Invalid folder_id provided");
                return null;
            }

            // If folderId does not have a /, append the current user's username
            if (!folderId.Contains("/"))
            {
                folderId = $"{username}/{folderId}";
            }

            var parts = folderId.Split('/');
            var ownerUsername = parts[0];
            var folderIdPart = parts[1];

            // If folderIdPart is not numeric, it's already a folder path
            if (!long.TryParse(folderIdPart, out var numericFolderId))
            {
                return $"{ownerUsername}/{folderIdPart}";
            }

            // Strip domain off owner_username if it exists
            if (ownerUsername.Contains("@"))
            {
                ownerUsername = ownerUsername.Split('@')[0];
            }

            var ownerEmail = $"{ownerUsername}@{domain}";

            if (ownerUsername == "@domain")
            {
                ownerUsername = "~";
            }

            Logging.Log($"[DB] Getting folder path from folder ID: {folderIdPart} for owner: {ownerEmail}");

            // Fetch the email folder list to find the path for this folder ID
            var response = await GetAsync<JsonElement>("/api/v1/folders/list-email-folders");

            if (response.TryGetProperty("success", out var success) && success.GetBoolean() &&
                response.TryGetProperty("folderList", out var folderList))
            {
                foreach (var folder in folderList.EnumerateArray())
                {
                    var id = folder.TryGetProperty("id", out var idProp) ? idProp.GetInt64() : 0;
                    var folderOwnerEmail = folder.TryGetProperty("ownerEmailAddress", out var email)
                        ? email.GetString()
                        : "";
                    var folderOwnerUsername = folderOwnerEmail?.Split('@')[0] ?? "";

                    if (id == numericFolderId &&
                        (folderOwnerUsername.Equals(ownerUsername, StringComparison.OrdinalIgnoreCase) ||
                         ownerUsername == "~"))
                    {
                        var path = folder.TryGetProperty("path", out var pathProp) ? pathProp.GetString() : null;
                        if (!string.IsNullOrEmpty(path))
                        {
                            return $"{folderOwnerUsername}/{path}";
                        }
                    }
                }
            }

            Console.Error.WriteLine($"[DB] No folder path found for folder ID: {folderIdPart}");
            return null;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[DB] Error getting folder path from folder ID: {ex.Message}");
            return null;
        }
    }

    public async Task<string?> GetSourceIdPathFromFolderIdAsync(string folderId, string username, string domain)
    {
        try
        {
            if (string.IsNullOrEmpty(folderId))
            {
                Console.Error.WriteLine("[DB] Invalid folder_id provided");
                return null;
            }

            // If folderId does not have a /, append the current user's username
            if (!folderId.Contains("/"))
            {
                folderId = $"{username}/{folderId}";
            }

            var parts = folderId.Split('/');
            var ownerUsername = parts[0];
            var folderIdPart = parts[1];

            // Strip domain off owner_username if it exists
            if (ownerUsername.Contains("@"))
            {
                ownerUsername = ownerUsername.Split('@')[0];
            }

            if (ownerUsername == "@domain")
            {
                ownerUsername = "~";
            }

            // If folderIdPart is not numeric, check if it's a GUID or a display name
            if (!long.TryParse(folderIdPart, out var numericFolderId))
            {
                // If it's already a GUID, return as-is
                if (Guid.TryParse(folderIdPart, out _))
                    return $"{ownerUsername}/{folderIdPart}";

                // Otherwise treat as display name and resolve to GUID
                return await ResolveSourceByNameAsync(folderIdPart, ownerUsername);
            }

            Logging.Log($"[DB] Getting source ID path from folder ID: {folderIdPart} for owner: {ownerUsername}");

            // Try email folders first
            try
            {
                var emailResponse = await GetAsync<JsonElement>("/api/v1/folders/list-email-folders");
                if (emailResponse.TryGetProperty("success", out var emailSuccess) && emailSuccess.GetBoolean() &&
                    emailResponse.TryGetProperty("folderList", out var folderList))
                {
                    foreach (var folder in folderList.EnumerateArray())
                    {
                        var id = folder.TryGetProperty("id", out var idProp) ? idProp.GetInt64() : 0;
                        var folderOwnerEmail = folder.TryGetProperty("ownerEmailAddress", out var email) ? email.GetString() : "";
                        var folderOwnerUsername = folderOwnerEmail?.Split('@')[0] ?? "";

                        if (id == numericFolderId &&
                            (folderOwnerUsername.Equals(ownerUsername, StringComparison.OrdinalIgnoreCase) || ownerUsername == "~"))
                        {
                            var guid = folder.TryGetProperty("guid", out var guidProp) ? guidProp.GetString() : null;
                            if (!string.IsNullOrEmpty(guid))
                                return $"{folderOwnerUsername}/{guid}";
                        }
                    }
                }
            }
            catch { /* Continue to other sources */ }

            // Try contact sources
            try
            {
                var contactResponse = await GetAsync<JsonElement>("/api/v1/contacts/sources");
                if (contactResponse.TryGetProperty("success", out var contactSuccess) && contactSuccess.GetBoolean() &&
                    contactResponse.TryGetProperty("sharedLists", out var contactLists))
                {
                    foreach (var source in contactLists.EnumerateArray())
                    {
                        var id = source.TryGetProperty("folderId", out var idProp) ? idProp.GetInt64() : 0;
                        var sourceOwner = source.TryGetProperty("ownerUsername", out var owner) ? owner.GetString() : "";

                        if (id == numericFolderId &&
                            (sourceOwner?.Equals(ownerUsername, StringComparison.OrdinalIgnoreCase) == true || ownerUsername == "~"))
                        {
                            // Contact sources use "itemID" for the GUID
                            var guid = source.TryGetProperty("itemID", out var guidProp) ? guidProp.GetString() : null;
                            if (!string.IsNullOrEmpty(guid))
                                return $"{sourceOwner}/{guid}";
                        }
                    }
                }
            }
            catch { /* Continue to other sources */ }

            // Try note sources
            try
            {
                var noteResponse = await GetAsync<JsonElement>("/api/v1/notes/sources");
                if (noteResponse.TryGetProperty("success", out var noteSuccess) && noteSuccess.GetBoolean() &&
                    noteResponse.TryGetProperty("sharedLists", out var noteLists))
                {
                    foreach (var source in noteLists.EnumerateArray())
                    {
                        var id = source.TryGetProperty("folderId", out var idProp) ? idProp.GetInt64() : 0;
                        var sourceOwner = source.TryGetProperty("ownerUsername", out var owner) ? owner.GetString() : "";

                        if (id == numericFolderId &&
                            (sourceOwner?.Equals(ownerUsername, StringComparison.OrdinalIgnoreCase) == true || ownerUsername == "~"))
                        {
                            // Note sources use "itemID" for the GUID
                            var guid = source.TryGetProperty("itemID", out var guidProp) ? guidProp.GetString() : null;
                            if (!string.IsNullOrEmpty(guid))
                                return $"{sourceOwner}/{guid}";
                        }
                    }
                }
            }
            catch { /* Continue to other sources */ }

            // Try calendar/task sources
            try
            {
                var calResponse = await GetAsync<JsonElement>("/api/v1/calendars/sources");

                // Check calendars
                if (calResponse.TryGetProperty("calendars", out var calendars))
                {
                    foreach (var source in calendars.EnumerateArray())
                    {
                        var id = source.TryGetProperty("folderId", out var idProp)
                            ? (idProp.ValueKind == JsonValueKind.Number ? idProp.GetInt64() : 0)
                            : 0;
                        var sourceOwner = source.TryGetProperty("owner", out var owner) ? owner.GetString() : "";

                        if (id == numericFolderId &&
                            (sourceOwner?.Equals(ownerUsername, StringComparison.OrdinalIgnoreCase) == true || ownerUsername == "~"))
                        {
                            var guid = source.TryGetProperty("id", out var guidProp) ? guidProp.GetString() : null;
                            if (!string.IsNullOrEmpty(guid))
                                return $"{sourceOwner}/{guid}";
                        }
                    }
                }

                // Check tasks
                if (calResponse.TryGetProperty("tasks", out var tasks))
                {
                    foreach (var source in tasks.EnumerateArray())
                    {
                        var id = source.TryGetProperty("folderId", out var idProp)
                            ? (idProp.ValueKind == JsonValueKind.Number ? idProp.GetInt64() : 0)
                            : 0;
                        var sourceOwner = source.TryGetProperty("owner", out var owner) ? owner.GetString() : "";

                        if (id == numericFolderId &&
                            (sourceOwner?.Equals(ownerUsername, StringComparison.OrdinalIgnoreCase) == true || ownerUsername == "~"))
                        {
                            var guid = source.TryGetProperty("id", out var guidProp) ? guidProp.GetString() : null;
                            if (!string.IsNullOrEmpty(guid))
                                return $"{sourceOwner}/{guid}";
                        }
                    }
                }
            }
            catch { /* Continue */ }

            Console.Error.WriteLine($"[DB] No source ID found for folder ID: {folderIdPart}");
            return null;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[DB] Error getting source ID from folder ID: {ex.Message}");
            return null;
        }
    }

    private async Task<string?> ResolveSourceByNameAsync(string folderName, string ownerUsername)
    {
        Logging.Log($"[DB] Resolving source by display name: '{folderName}' for owner: {ownerUsername}");

        // Try email folders
        try
        {
            var emailResponse = await GetAsync<JsonElement>("/api/v1/folders/list-email-folders");
            if (emailResponse.TryGetProperty("success", out var emailSuccess) && emailSuccess.GetBoolean() &&
                emailResponse.TryGetProperty("folderList", out var folderList))
            {
                foreach (var folder in folderList.EnumerateArray())
                {
                    var name = folder.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : "";
                    var folderOwnerEmail = folder.TryGetProperty("ownerEmailAddress", out var email) ? email.GetString() : "";
                    var folderOwnerUsername = folderOwnerEmail?.Split('@')[0] ?? "";

                    if (string.Equals(name, folderName, StringComparison.OrdinalIgnoreCase) &&
                        (folderOwnerUsername.Equals(ownerUsername, StringComparison.OrdinalIgnoreCase) || ownerUsername == "~"))
                    {
                        var guid = folder.TryGetProperty("guid", out var guidProp) ? guidProp.GetString() : null;
                        if (!string.IsNullOrEmpty(guid))
                            return $"{folderOwnerUsername}/{guid}";
                    }
                }
            }
        }
        catch { /* Continue to other sources */ }

        // Try contact sources
        try
        {
            var contactResponse = await GetAsync<JsonElement>("/api/v1/contacts/sources");
            if (contactResponse.TryGetProperty("success", out var contactSuccess) && contactSuccess.GetBoolean() &&
                contactResponse.TryGetProperty("sharedLists", out var contactLists))
            {
                foreach (var source in contactLists.EnumerateArray())
                {
                    var displayName = source.TryGetProperty("displayName", out var dnProp) ? dnProp.GetString() : "";
                    var sourceOwner = source.TryGetProperty("ownerUsername", out var owner) ? owner.GetString() : "";

                    if (string.Equals(displayName, folderName, StringComparison.OrdinalIgnoreCase) &&
                        (sourceOwner?.Equals(ownerUsername, StringComparison.OrdinalIgnoreCase) == true || ownerUsername == "~"))
                    {
                        var guid = source.TryGetProperty("itemID", out var guidProp) ? guidProp.GetString() : null;
                        if (!string.IsNullOrEmpty(guid))
                            return $"{sourceOwner}/{guid}";
                    }
                }
            }
        }
        catch { /* Continue to other sources */ }

        // Try note sources
        try
        {
            var noteResponse = await GetAsync<JsonElement>("/api/v1/notes/sources");
            if (noteResponse.TryGetProperty("success", out var noteSuccess) && noteSuccess.GetBoolean() &&
                noteResponse.TryGetProperty("sharedLists", out var noteLists))
            {
                foreach (var source in noteLists.EnumerateArray())
                {
                    var displayName = source.TryGetProperty("displayName", out var dnProp) ? dnProp.GetString() : "";
                    var sourceOwner = source.TryGetProperty("ownerUsername", out var owner) ? owner.GetString() : "";

                    if (string.Equals(displayName, folderName, StringComparison.OrdinalIgnoreCase) &&
                        (sourceOwner?.Equals(ownerUsername, StringComparison.OrdinalIgnoreCase) == true || ownerUsername == "~"))
                    {
                        var guid = source.TryGetProperty("itemID", out var guidProp) ? guidProp.GetString() : null;
                        if (!string.IsNullOrEmpty(guid))
                            return $"{sourceOwner}/{guid}";
                    }
                }
            }
        }
        catch { /* Continue to other sources */ }

        // Try calendar/task sources
        try
        {
            var calResponse = await GetAsync<JsonElement>("/api/v1/calendars/sources");

            if (calResponse.TryGetProperty("calendars", out var calendars))
            {
                foreach (var source in calendars.EnumerateArray())
                {
                    var name = source.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : "";
                    var sourceOwner = source.TryGetProperty("owner", out var owner) ? owner.GetString() : "";

                    if (string.Equals(name, folderName, StringComparison.OrdinalIgnoreCase) &&
                        (sourceOwner?.Equals(ownerUsername, StringComparison.OrdinalIgnoreCase) == true || ownerUsername == "~"))
                    {
                        var guid = source.TryGetProperty("id", out var guidProp) ? guidProp.GetString() : null;
                        if (!string.IsNullOrEmpty(guid))
                            return $"{sourceOwner}/{guid}";
                    }
                }
            }

            if (calResponse.TryGetProperty("tasks", out var tasks))
            {
                foreach (var source in tasks.EnumerateArray())
                {
                    var name = source.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : "";
                    var sourceOwner = source.TryGetProperty("owner", out var owner) ? owner.GetString() : "";

                    if (string.Equals(name, folderName, StringComparison.OrdinalIgnoreCase) &&
                        (sourceOwner?.Equals(ownerUsername, StringComparison.OrdinalIgnoreCase) == true || ownerUsername == "~"))
                    {
                        var guid = source.TryGetProperty("id", out var guidProp) ? guidProp.GetString() : null;
                        if (!string.IsNullOrEmpty(guid))
                            return $"{sourceOwner}/{guid}";
                    }
                }
            }
        }
        catch { /* Continue */ }

        Console.Error.WriteLine($"[DB] No source found matching name: '{folderName}' for owner: {ownerUsername}");
        return null;
    }

    public async Task DisconnectAsync()
    {
        if (!_isConnected)
        {
            Logging.Log("[DB] Already disconnected.");
            return;
        }

        Logging.Log("[DB] Disconnecting and closing HTTP session...");
        await Task.Delay(50); // Simulate disconnection delay
        _isConnected = false;
        Logging.Log("[DB] Disconnected.");
    }

    public void Dispose()
    {
        _httpClient?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _httpClient?.Dispose();
    }
}
