using System.Text.Json.Serialization;

namespace SmarterMailMcp.Core.Models;

public class TokenData
{
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; set; }

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; set; }

    [JsonPropertyName("base_url")]
    public string? BaseUrl { get; set; }

    [JsonPropertyName("username")]
    public string? Username { get; set; }

    [JsonPropertyName("expiration")]
    public string? Expiration { get; set; }

    [JsonPropertyName("refresh_expiration")]
    public string? RefreshExpiration { get; set; }

    [JsonPropertyName("read_only_mode")]
    public bool ReadOnlyMode { get; set; } = true;

    [JsonPropertyName("user_type")]
    public string? UserType { get; set; } = "user";

    [JsonPropertyName("method")]
    public string? Method { get; set; }

    [JsonPropertyName("api_key")]
    public string? ApiKey { get; set; }

    [JsonPropertyName("client_id")]
    public string? ClientId { get; set; }
}


