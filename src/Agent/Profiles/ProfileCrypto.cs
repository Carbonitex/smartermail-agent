using System.Security.Cryptography;
using System.Text;
using SmarterMailAgent.Auth;

namespace SmarterMailAgent.Profiles;

/// <summary>
/// The server's half of a profile's cryptography. The browser holds the profile key (PK) and derives
/// everything from it (<c>wwwroot/js/vault.js</c>); the server only ever sees:
/// <list type="bullet">
///   <item>the <b>accounts key</b> <c>HKDF(PK, "sma-accounts-v1")</c>, sent at unlock and held in
///   memory while the profile is unlocked. It seals the stored accounts (<see cref="AccountsLabel"/>).
///   The server keeps only <see cref="AccountsKeyCheck"/> of it;</item>
///   <item>the profile's ECDH <b>public key</b>, to which it seals task-run transcripts
///   (<see cref="SealToPublicKey"/>), so a run's result is unreadable at rest even with <c>DATA_KEY</c>;</item>
///   <item>opaque blobs it stores and hands back: passkey-wrapped PKs, the recovery-wrapped PK, the
///   encrypted ECDH private key, the encrypted settings.</item>
/// </list>
/// HKDF everywhere is SHA-256 with a 32-byte zero salt (as WebCrypto is called in vault.js), except
/// the transcript seal, whose salt is the ephemeral public key.
/// </summary>
public static class ProfileCrypto
{
    public const string AccountsLabel = "sma-profile-account-v1";
    public const string ServerAccountsLabel = "sma-server-account-v1";
    public const string TaskDefinitionLabel = "sma-task-definition-v1";
    public const string TaskLlmKeyLabel = "sma-task-llm-key-v1";
    public const string TaskInstructionsLabel = "sma-task-instructions-v1";
    private const string TranscriptInfo = "sma-run-transcript-v1";
    private const byte TranscriptFormat = 0x01;

    /// <summary>HMAC-SHA-256(accounts key, "sma-accounts-check-v1"): proves an unlock key without storing it.</summary>
    public static string AccountsKeyCheck(byte[] accountsKey) =>
        Base64Url.Encode(HMACSHA256.HashData(accountsKey, "sma-accounts-check-v1"u8));

    public static bool AccountsKeyMatches(byte[] accountsKey, string storedCheck) =>
        Base64Url.Decode(storedCheck) is { } expected &&
        CryptographicOperations.FixedTimeEquals(expected, HMACSHA256.HashData(accountsKey, "sma-accounts-check-v1"u8));

    /// <summary>SHA-256 of the recovery auth key (<c>HKDF(recovery secret, "sma-recovery-auth-v1")</c>).</summary>
    public static string RecoveryAuthHash(byte[] authKey) => Base64Url.Encode(SHA256.HashData(authKey));

    public static bool RecoveryAuthMatches(byte[] authKey, string? storedHash) =>
        Base64Url.Decode(storedHash) is { } expected &&
        CryptographicOperations.FixedTimeEquals(expected, SHA256.HashData(authKey));

    /// <summary>True when <paramref name="spki"/> is a P-256 public key in SubjectPublicKeyInfo form.</summary>
    public static bool IsValidPublicKey(string? spki)
    {
        if (Base64Url.Decode(spki) is not { Length: > 0 and < 512 } bytes)
            return false;
        try
        {
            using var key = ECDiffieHellman.Create();
            key.ImportSubjectPublicKeyInfo(bytes, out var read);
            return read == bytes.Length && key.KeySize == 256;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>
    /// Seals <paramref name="plaintext"/> so only the holder of the profile's ECDH private key can open
    /// it: ephemeral P-256 ECDH → HKDF-SHA-256 (salt = ephemeral public key, info
    /// <c>sma-run-transcript-v1</c>) → AES-256-GCM, associated data = <paramref name="context"/>.
    /// Format, base64url: <c>[0x01][ephemeral public key, 65 bytes uncompressed][nonce 12][ciphertext][tag 16]</c>.
    /// </summary>
    public static string SealToPublicKey(ReadOnlySpan<byte> plaintext, string recipientSpki, string context)
    {
        using var recipient = ECDiffieHellman.Create();
        recipient.ImportSubjectPublicKeyInfo(Base64Url.Decode(recipientSpki)
            ?? throw new ArgumentException("Invalid public key.", nameof(recipientSpki)), out _);

        using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var p = ephemeral.ExportParameters(false);
        var epk = new byte[65];
        epk[0] = 0x04;
        p.Q.X!.CopyTo(epk, 1);
        p.Q.Y!.CopyTo(epk, 33);

        var shared = ephemeral.DeriveRawSecretAgreement(recipient.PublicKey);
        var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, salt: epk, info: Encoding.ASCII.GetBytes(TranscriptInfo));
        CryptographicOperations.ZeroMemory(shared);

        var output = new byte[1 + 65 + Sealer.NonceSize + plaintext.Length + Sealer.TagSize];
        output[0] = TranscriptFormat;
        epk.CopyTo(output, 1);
        var nonce = output.AsSpan(66, Sealer.NonceSize);
        RandomNumberGenerator.Fill(nonce);

        using (var aes = new AesGcm(key, Sealer.TagSize))
        {
            aes.Encrypt(nonce, plaintext,
                output.AsSpan(66 + Sealer.NonceSize, plaintext.Length),
                output.AsSpan(66 + Sealer.NonceSize + plaintext.Length, Sealer.TagSize),
                Encoding.UTF8.GetBytes(context));
        }

        CryptographicOperations.ZeroMemory(key);
        return Base64Url.Encode(output);
    }

    /// <summary>The opposite of <see cref="SealToPublicKey"/>; only tests hold a private key server-side.</summary>
    internal static byte[] OpenWithPrivateKey(string sealedValue, ECDiffieHellman privateKey, string context)
    {
        var data = Base64Url.Decode(sealedValue) ?? throw new CryptographicException("Not base64url.");
        if (data.Length < 66 + Sealer.NonceSize + Sealer.TagSize || data[0] != TranscriptFormat)
            throw new CryptographicException("Malformed.");

        var epk = data.AsSpan(1, 65).ToArray();
        using var ephemeral = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = epk[1..33], Y = epk[33..65] },
        });

        var shared = privateKey.DeriveRawSecretAgreement(ephemeral.PublicKey);
        var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, salt: epk, info: Encoding.ASCII.GetBytes(TranscriptInfo));
        var cipherLength = data.Length - 66 - Sealer.NonceSize - Sealer.TagSize;
        var plaintext = new byte[cipherLength];
        using var aes = new AesGcm(key, Sealer.TagSize);
        aes.Decrypt(data.AsSpan(66, Sealer.NonceSize), data.AsSpan(66 + Sealer.NonceSize, cipherLength),
            data.AsSpan(66 + Sealer.NonceSize + cipherLength, Sealer.TagSize), plaintext, Encoding.UTF8.GetBytes(context));
        return plaintext;
    }

    /// <summary>Row-binding context for a sealed value: <c>profileId|rowId</c>.</summary>
    public static string Context(string profileId, string rowId) => $"{profileId}|{rowId}";

    /// <summary>A random id: <paramref name="bytes"/> bytes of base64url.</summary>
    public static string NewId(int bytes = 16)
    {
        Span<byte> buffer = stackalloc byte[bytes];
        RandomNumberGenerator.Fill(buffer);
        return Base64Url.Encode(buffer);
    }
}
