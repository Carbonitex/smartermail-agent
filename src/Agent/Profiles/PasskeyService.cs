using System.Collections.Concurrent;
using System.Text.Json;
using Fido2NetLib;
using Fido2NetLib.Objects;
using SmarterMailAgent.Auth;
using SmarterMailAgent.Server;
using SmarterMailAgent.Storage;

namespace SmarterMailAgent.Profiles;

/// <summary>
/// WebAuthn for profile passkeys, verified here on the server (fido2-net-lib): the passkey is the
/// profile's identity. Separately, the browser asks the same passkey for its PRF output, which never
/// leaves the browser; it derives the key that unwraps the profile key (see <c>wwwroot/js/vault.js</c>).
/// The browser must send the credential <b>without</b> <c>clientExtensionResults</c> (the PRF output
/// is in there); anything it sends in that field is discarded before verification.
/// <para>
/// Ceremonies (a challenge and its options) live in memory for two minutes and are single use.
/// The relying party is <c>PUBLIC_ORIGIN</c>'s host, or the request's host when that is unset.
/// </para>
/// </summary>
public sealed class PasskeyService(ServerOptions options, ProfileStore store, ILogger<PasskeyService> logger)
{
    public static readonly TimeSpan CeremonyLifetime = TimeSpan.FromMinutes(2);
    private const int MaxCeremonies = 10_000;

    private sealed record Ceremony(object Options, string Origin, string? Binding, DateTimeOffset ExpiresAt, string? ProfileId);

    private readonly ConcurrentDictionary<string, Ceremony> _ceremonies = new(StringComparer.Ordinal);

    /// <summary>The origin passkeys are bound to, for this request.</summary>
    public string OriginFor(HttpRequest request) =>
        options.PublicOrigin is { } origin
            ? origin.GetLeftPart(UriPartial.Authority)
            : $"{request.Scheme}://{request.Host.Value}";

    private Fido2 Fido(string origin) => new(new Fido2Configuration
    {
        RPID = new Uri(origin).Host,
        RPName = "SmarterMail Agent",
        Origins = new HashSet<string>(StringComparer.Ordinal) { origin },
        Timeout = (uint)CeremonyLifetime.TotalMilliseconds,
    });

    /// <summary>
    /// Options for <c>navigator.credentials.create</c>: a discoverable credential (so another browser
    /// can sign in with no username), user verification required, no attestation.
    /// <paramref name="binding"/> ties the ceremony to the session that asked for it.
    /// </summary>
    public (string CeremonyId, JsonElement Options) BeginRegistration(
        HttpRequest request, string profileId, string userName, IEnumerable<string> existingCredentialIds, string binding)
    {
        var origin = OriginFor(request);
        var created = Fido(origin).RequestNewCredential(new RequestNewCredentialParams
        {
            User = new Fido2User
            {
                Id = Base64Url.Decode(profileId) ?? throw new ArgumentException("Bad profile id.", nameof(profileId)),
                Name = userName,
                DisplayName = userName,
            },
            ExcludeCredentials = existingCredentialIds
                .Select(id => new PublicKeyCredentialDescriptor(Base64Url.Decode(id)!))
                .ToList(),
            AuthenticatorSelection = new AuthenticatorSelection
            {
                ResidentKey = ResidentKeyRequirement.Required,
                UserVerification = UserVerificationRequirement.Required,
            },
            AttestationPreference = AttestationConveyancePreference.None,
            PubKeyCredParams =
            [
                PubKeyCredParam.ES256,
                PubKeyCredParam.RS256,
                PubKeyCredParam.Ed25519,
            ],
        });

        var id = Remember(new Ceremony(created, origin, binding, DateTimeOffset.UtcNow + CeremonyLifetime, profileId));
        return (id, JsonSerializer.Deserialize<JsonElement>(created.ToJson()));
    }

    /// <summary>Options for <c>navigator.credentials.get</c> with no allow-list: any of this site's passkeys.</summary>
    public (string CeremonyId, JsonElement Options) BeginLogin(HttpRequest request)
    {
        var origin = OriginFor(request);
        var assertion = Fido(origin).GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = [],
            UserVerification = UserVerificationRequirement.Required,
        });

        var id = Remember(new Ceremony(assertion, origin, null, DateTimeOffset.UtcNow + CeremonyLifetime, null));
        return (id, JsonSerializer.Deserialize<JsonElement>(assertion.ToJson()));
    }

    public sealed record Registered(string ProfileId, string CredentialId, byte[] PublicKey, uint SignCount, string Aaguid);

    /// <summary>Verifies a new credential. Null (and logged by failure class only) when anything is off.</summary>
    public async Task<Registered?> CompleteRegistrationAsync(string? ceremonyId, JsonElement credential, string binding, CancellationToken ct)
    {
        if (Take(ceremonyId) is not { Options: CredentialCreateOptions created, ProfileId: { } profileId } ceremony ||
            !string.Equals(ceremony.Binding, binding, StringComparison.Ordinal))
            return null;

        try
        {
            var response = JsonSerializer.Deserialize<AuthenticatorAttestationRawResponse>(Scrub(credential).GetRawText())
                           ?? throw new Fido2VerificationException("Empty credential.");
            var result = await Fido(ceremony.Origin).MakeNewCredentialAsync(new MakeNewCredentialParams
            {
                AttestationResponse = response,
                OriginalOptions = created,
                IsCredentialIdUniqueToUserCallback = (args, _) =>
                    Task.FromResult(store.GetPasskey(Base64Url.Encode(args.CredentialId)) is null),
            }, ct);

            return new Registered(profileId, Base64Url.Encode(result.Id), result.PublicKey, result.SignCount, result.AaGuid.ToString());
        }
        catch (Exception ex) when (ex is Fido2VerificationException or JsonException or ArgumentException or InvalidOperationException or FormatException)
        {
            logger.LogInformation("Passkey registration refused: {Kind}.", ex.GetType().Name);
            return null;
        }
    }

    /// <summary>Verifies a sign-in and advances the stored counter. Null when anything is off.</summary>
    public async Task<PasskeyRow?> CompleteLoginAsync(string? ceremonyId, JsonElement credential, CancellationToken ct)
    {
        // A step-up ceremony (bound, see BeginStepUp) never signs anyone in.
        if (Take(ceremonyId) is not { Options: AssertionOptions assertion, Binding: null } ceremony)
            return null;

        try
        {
            var response = JsonSerializer.Deserialize<AuthenticatorAssertionRawResponse>(Scrub(credential).GetRawText())
                           ?? throw new Fido2VerificationException("Empty credential.");
            var passkey = store.GetPasskey(Base64Url.Encode(response.RawId));
            if (passkey is null)
            {
                logger.LogInformation("Passkey sign-in refused: unknown credential.");
                return null;
            }

            var result = await Fido(ceremony.Origin).MakeAssertionAsync(new MakeAssertionParams
            {
                AssertionResponse = response,
                OriginalOptions = assertion,
                StoredPublicKey = passkey.PublicKey,
                StoredSignatureCounter = passkey.SignCount,
                IsUserHandleOwnerOfCredentialIdCallback = (args, _) => Task.FromResult(
                    args.UserHandle is { Length: > 0 } handle && Base64Url.Encode(handle) == passkey.ProfileId),
            }, ct);

            store.RecordPasskeyUse(passkey.CredentialId, result.SignCount);
            return passkey;
        }
        catch (Exception ex) when (ex is Fido2VerificationException or JsonException or ArgumentException or InvalidOperationException or FormatException)
        {
            logger.LogInformation("Passkey sign-in refused: {Kind}.", ex.GetType().Name);
            return null;
        }
    }

    /// <summary>
    /// Options for a step-up: <c>navigator.credentials.get</c> limited to this profile's passkeys, user
    /// verification required. <paramref name="binding"/> is held server-side (browsers show no
    /// transaction text): the ceremony only completes with the same binding, e.g. this session, this
    /// proposal and these exact arguments.
    /// </summary>
    public (string CeremonyId, JsonElement Options) BeginStepUp(
        HttpRequest request, string profileId, IEnumerable<string> credentialIds, string binding)
    {
        var origin = OriginFor(request);
        var assertion = Fido(origin).GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = credentialIds
                .Select(id => Base64Url.Decode(id))
                .Where(id => id is not null)
                .Select(id => new PublicKeyCredentialDescriptor(id!))
                .ToList(),
            UserVerification = UserVerificationRequirement.Required,
        });

        var id = Remember(new Ceremony(assertion, origin, binding, DateTimeOffset.UtcNow + CeremonyLifetime, profileId));
        return (id, JsonSerializer.Deserialize<JsonElement>(assertion.ToJson()));
    }

    /// <summary>
    /// Verifies a step-up assertion: same binding, a passkey of <paramref name="profileId"/>, user
    /// verified, signature and counter valid. Advances the counter. Null (logged by failure class
    /// only) when anything is off.
    /// </summary>
    public async Task<PasskeyRow?> CompleteStepUpAsync(
        string? ceremonyId, JsonElement credential, string binding, string profileId, CancellationToken ct)
    {
        if (Take(ceremonyId) is not { Options: AssertionOptions assertion, Binding: { } bound } ceremony ||
            !string.Equals(bound, binding, StringComparison.Ordinal) ||
            !string.Equals(ceremony.ProfileId, profileId, StringComparison.Ordinal))
        {
            logger.LogInformation("Passkey step-up refused: no matching ceremony.");
            return null;
        }

        try
        {
            var response = JsonSerializer.Deserialize<AuthenticatorAssertionRawResponse>(Scrub(credential).GetRawText())
                           ?? throw new Fido2VerificationException("Empty credential.");
            var passkey = store.GetPasskey(Base64Url.Encode(response.RawId));
            if (passkey is null || passkey.ProfileId != profileId)
            {
                logger.LogInformation("Passkey step-up refused: not a passkey of this profile.");
                return null;
            }

            var result = await Fido(ceremony.Origin).MakeAssertionAsync(new MakeAssertionParams
            {
                AssertionResponse = response,
                OriginalOptions = assertion,
                StoredPublicKey = passkey.PublicKey,
                StoredSignatureCounter = passkey.SignCount,
                IsUserHandleOwnerOfCredentialIdCallback = (args, _) => Task.FromResult(
                    args.UserHandle is not { Length: > 0 } handle || Base64Url.Encode(handle) == passkey.ProfileId),
            }, ct);

            store.RecordPasskeyUse(passkey.CredentialId, result.SignCount);
            return passkey;
        }
        catch (Exception ex) when (ex is Fido2VerificationException or JsonException or ArgumentException or InvalidOperationException or FormatException)
        {
            logger.LogInformation("Passkey step-up refused: {Kind}.", ex.GetType().Name);
            return null;
        }
    }

    /// <summary>Drops expired ceremonies; the sweeper calls it every minute.</summary>
    public int Sweep()
    {
        var now = DateTimeOffset.UtcNow;
        var removed = 0;
        foreach (var (id, ceremony) in _ceremonies)
        {
            if (ceremony.ExpiresAt <= now && _ceremonies.TryRemove(id, out _))
                removed++;
        }
        return removed;
    }

    private string Remember(Ceremony ceremony)
    {
        if (_ceremonies.Count >= MaxCeremonies)
            Sweep();
        var id = ProfileCrypto.NewId();
        _ceremonies[id] = ceremony;
        return id;
    }

    private Ceremony? Take(string? id) =>
        !string.IsNullOrEmpty(id) && _ceremonies.TryRemove(id, out var ceremony) && ceremony.ExpiresAt > DateTimeOffset.UtcNow
            ? ceremony
            : null;

    /// <summary>
    /// The credential without its client extension results: whatever the browser put there (the PRF
    /// output, if it slipped through) is never parsed or kept.
    /// </summary>
    private static JsonElement Scrub(JsonElement credential)
    {
        if (credential.ValueKind != JsonValueKind.Object)
            throw new JsonException("Credential is not an object.");

        var node = System.Text.Json.Nodes.JsonNode.Parse(credential.GetRawText())!.AsObject();
        node.Remove("clientExtensionResults");
        node.Remove("extensions");
        node["clientExtensionResults"] = new System.Text.Json.Nodes.JsonObject();
        return JsonSerializer.Deserialize<JsonElement>(node.ToJsonString());
    }
}
