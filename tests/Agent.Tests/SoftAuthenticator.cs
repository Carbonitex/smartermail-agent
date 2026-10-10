using System.Buffers.Binary;
using System.Formats.Cbor;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SmarterMailAgent.Auth;

namespace SmarterMailAgent.Tests;

/// <summary>
/// A software passkey: ES256, "none" attestation, discoverable, user verification on. Produces the
/// same JSON a browser sends (base64url fields), so the server's real WebAuthn verification runs.
/// Its PRF is an HMAC of a per-credential secret, as an authenticator's hmac-secret would be.
/// </summary>
internal sealed class SoftAuthenticator
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly byte[] _prfSecret = RandomNumberGenerator.GetBytes(32);
    private uint _counter;

    public byte[] CredentialId { get; } = RandomNumberGenerator.GetBytes(16);
    public byte[]? UserHandle { get; private set; }

    public string Id => Base64Url.Encode(CredentialId);

    /// <summary>The PRF output for a salt (what the browser's prf extension would return).</summary>
    public byte[] Prf(byte[] salt) => HMACSHA256.HashData(_prfSecret, salt);

    /// <summary>A <c>navigator.credentials.create</c> answer for these options.</summary>
    public JsonObject Create(JsonElement options, string origin, string? overrideOrigin = null, JsonObject? extensionResults = null)
    {
        var rpId = options.GetProperty("rp").GetProperty("id").GetString()!;
        UserHandle = Base64Url.Decode(options.GetProperty("user").GetProperty("id").GetString());
        var clientData = ClientData("webauthn.create", options.GetProperty("challenge").GetString()!, overrideOrigin ?? origin);

        var parameters = _key.ExportParameters(false);
        var cose = new CborWriter(CborConformanceMode.Ctap2Canonical);
        cose.WriteStartMap(5);
        cose.WriteInt32(1); cose.WriteInt32(2);      // kty: EC2
        cose.WriteInt32(3); cose.WriteInt32(-7);     // alg: ES256
        cose.WriteInt32(-1); cose.WriteInt32(1);     // crv: P-256
        cose.WriteInt32(-2); cose.WriteByteString(parameters.Q.X!);
        cose.WriteInt32(-3); cose.WriteByteString(parameters.Q.Y!);
        cose.WriteEndMap();
        var coseKey = cose.Encode();

        var authData = new List<byte>();
        authData.AddRange(SHA256.HashData(Encoding.UTF8.GetBytes(rpId)));
        authData.Add(0x01 | 0x04 | 0x40);            // UP | UV | AT
        authData.AddRange(Counter(_counter));
        authData.AddRange(new byte[16]);             // AAGUID
        var length = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)CredentialId.Length);
        authData.AddRange(length);
        authData.AddRange(CredentialId);
        authData.AddRange(coseKey);

        var attestation = new CborWriter(CborConformanceMode.Ctap2Canonical);
        attestation.WriteStartMap(3);
        attestation.WriteTextString("fmt"); attestation.WriteTextString("none");
        attestation.WriteTextString("attStmt"); attestation.WriteStartMap(0); attestation.WriteEndMap();
        attestation.WriteTextString("authData"); attestation.WriteByteString(authData.ToArray());
        attestation.WriteEndMap();

        return new JsonObject
        {
            ["id"] = Id,
            ["rawId"] = Id,
            ["type"] = "public-key",
            ["response"] = new JsonObject
            {
                ["clientDataJSON"] = Base64Url.Encode(clientData),
                ["attestationObject"] = Base64Url.Encode(attestation.Encode()),
                ["transports"] = new JsonArray("internal"),
            },
            ["clientExtensionResults"] = extensionResults ?? new JsonObject(),
        };
    }

    /// <summary>A <c>navigator.credentials.get</c> answer for these options.</summary>
    public JsonObject Get(JsonElement options, string origin, bool advanceCounter = true, bool userVerified = true)
    {
        var rpId = options.GetProperty("rpId").GetString()!;
        var clientData = ClientData("webauthn.get", options.GetProperty("challenge").GetString()!, origin);
        if (advanceCounter)
            _counter++;

        var authData = new List<byte>();
        authData.AddRange(SHA256.HashData(Encoding.UTF8.GetBytes(rpId)));
        authData.Add((byte)(userVerified ? 0x01 | 0x04 : 0x01));   // UP | UV, or UP only
        authData.AddRange(Counter(_counter));
        var data = authData.ToArray();

        var signed = data.Concat(SHA256.HashData(clientData)).ToArray();
        var signature = _key.SignData(signed, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

        return new JsonObject
        {
            ["id"] = Id,
            ["rawId"] = Id,
            ["type"] = "public-key",
            ["response"] = new JsonObject
            {
                ["clientDataJSON"] = Base64Url.Encode(clientData),
                ["authenticatorData"] = Base64Url.Encode(data),
                ["signature"] = Base64Url.Encode(signature),
                ["userHandle"] = UserHandle is null ? null : Base64Url.Encode(UserHandle),
            },
            ["clientExtensionResults"] = new JsonObject(),
        };
    }

    /// <summary>Rewinds the signature counter (a cloned authenticator).</summary>
    public void RewindCounter(uint value) => _counter = value;

    private static byte[] ClientData(string type, string challenge, string origin) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type, challenge, origin, crossOrigin = false }));

    private static byte[] Counter(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return bytes;
    }
}
