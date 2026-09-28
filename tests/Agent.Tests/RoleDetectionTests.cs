using System.Text;
using System.Text.Json;
using SmarterMailAgent.Auth;

namespace SmarterMailAgent.Tests;

public sealed class RoleDetectionTests
{
    private static string Jwt(object payload)
    {
        static string B64(byte[] bytes) => Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        return $"{B64("""{"alg":"HS256"}"""u8.ToArray())}.{B64(JsonSerializer.SerializeToUtf8Bytes(payload))}.sig";
    }

    private static JsonElement Body(string json) => JsonDocument.Parse(json).RootElement;

    private static readonly JsonElement NoFlags = Body("""{"success":true}""");

    [Theory]
    [InlineData("SysAdmin")]
    [InlineData("PrimarySysAdmin")]
    public void Jwt_role_claim_marks_a_sysadmin(string role) =>
        Assert.Equal(AccountRole.SysAdmin,
            SmarterMailAuth.DetectRole("admin@example.com", Jwt(new { role }), NoFlags));

    [Fact]
    public void Jwt_role_array_and_ms_claim_name_are_read()
    {
        Assert.Equal(AccountRole.SysAdmin, SmarterMailAuth.DetectRole(
            "a@example.com", Jwt(new { role = new[] { "User", "SysAdmin" } }), NoFlags));

        var ms = new Dictionary<string, object>
        {
            ["http://schemas.microsoft.com/ws/2008/06/identity/claims/role"] = "PrimarySysAdmin",
        };
        Assert.Equal(AccountRole.SysAdmin, SmarterMailAuth.DetectRole("a@example.com", Jwt(ms), NoFlags));
    }

    [Fact]
    public void Body_isAdmin_marks_a_sysadmin() =>
        Assert.Equal(AccountRole.SysAdmin,
            SmarterMailAuth.DetectRole("a@example.com", null, Body("""{"isAdmin":true,"isDomainAdmin":false}""")));

    [Fact]
    public void Username_without_at_sign_is_a_sysadmin() =>
        Assert.Equal(AccountRole.SysAdmin, SmarterMailAuth.DetectRole("admin", null, NoFlags));

    [Fact]
    public void Body_isDomainAdmin_marks_a_domain_admin() =>
        Assert.Equal(AccountRole.DomainAdmin, SmarterMailAuth.DetectRole(
            "boss@example.com", null, Body("""{"isAdmin":false,"isDomainAdmin":true}""")));

    [Fact]
    public void Jwt_DomainAdmin_role_marks_a_domain_admin() =>
        Assert.Equal(AccountRole.DomainAdmin, SmarterMailAuth.DetectRole(
            "boss@example.com", Jwt(new { role = "DomainAdmin" }), NoFlags));

    [Fact]
    public void Explicit_false_flag_is_a_user_without_a_probe() =>
        Assert.Equal(AccountRole.User, SmarterMailAuth.DetectRole(
            "me@example.com", Jwt(new { role = "User" }), Body("""{"isAdmin":false,"isDomainAdmin":false}""")));

    [Fact]
    public void Missing_flags_need_the_probe()
    {
        Assert.Null(SmarterMailAuth.DetectRole("me@example.com", Jwt(new { sub = "x" }), NoFlags));
        Assert.Null(SmarterMailAuth.DetectRole("me@example.com", "not-a-jwt", default));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("a.b")]
    [InlineData("a.!!!.c")]
    [InlineData("a.bnVsbA.c")]   // payload "null"
    public void Malformed_tokens_have_no_roles(string? token) =>
        Assert.Empty(SmarterMailAuth.JwtRoles(token));
}
