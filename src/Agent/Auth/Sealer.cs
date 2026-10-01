using System.Security.Cryptography;
using System.Text;

namespace SmarterMailAgent.Auth;

/// <summary>
/// AES-256-GCM sealing with key rotation, shared by the remember-me bundle (<c>RESUME_KEY</c>), the
/// server key (<c>DATA_KEY</c>) and a profile's in-memory accounts key.
/// <para>
/// Format: <c>[0x01][key id][nonce 12][ciphertext][tag 16]</c>. The key id is the first byte of
/// SHA-256(key), so a previous key can keep opening what it sealed. The associated data is
/// <c>label + format + key id + context</c>: the label separates uses (a resume bundle can never be
/// opened as a task definition), and the context binds a sealed value to its row
/// (<c>profileId|rowId</c>), so one row's ciphertext pasted into another fails. An empty context gives
/// exactly the remember-me bundle's original associated data.
/// </para>
/// </summary>
public sealed class Sealer
{
    private const byte Format = 0x01;
    public const int NonceSize = 12;
    public const int TagSize = 16;
    public const int HeaderSize = 2;

    private readonly byte[] _current;
    private readonly byte _currentId;
    private readonly byte[][] _all;

    public Sealer(byte[] key, byte[]? previousKey = null)
    {
        if (key is not { Length: 32 })
            throw new ArgumentException("A sealing key must be 32 bytes.", nameof(key));
        if (previousKey is not null and not { Length: 32 })
            throw new ArgumentException("A previous sealing key must be 32 bytes.", nameof(previousKey));

        _current = key;
        _currentId = KeyId(key);
        _all = previousKey is null ? [key] : [key, previousKey];
    }

    public enum OpenFailure
    {
        /// <summary>Too short, or an unknown format byte.</summary>
        Malformed,

        /// <summary>Sealed under a key this process does not have.</summary>
        UnknownKey,

        /// <summary>The tag did not verify: tampered with, the wrong label or context, or a foreign key.</summary>
        Tampered,
    }

    public byte[] Seal(ReadOnlySpan<byte> plaintext, string label, string context = "")
    {
        var output = new byte[HeaderSize + NonceSize + plaintext.Length + TagSize];
        output[0] = Format;
        output[1] = _currentId;

        var nonce = output.AsSpan(HeaderSize, NonceSize);
        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(_current, TagSize);
        aes.Encrypt(nonce, plaintext,
            output.AsSpan(HeaderSize + NonceSize, plaintext.Length),
            output.AsSpan(HeaderSize + NonceSize + plaintext.Length, TagSize),
            Aad(label, output[0], output[1], context));
        return output;
    }

    /// <summary>The plaintext, or null with <paramref name="failure"/> set. The caller zeroes the plaintext when done.</summary>
    public byte[]? Open(ReadOnlySpan<byte> data, string label, out OpenFailure failure, string context = "")
    {
        failure = OpenFailure.Malformed;
        if (data.Length < HeaderSize + NonceSize + TagSize || data[0] != Format)
            return null;

        var keyId = data[1];
        var candidates = _all.Where(k => KeyId(k) == keyId).ToList();
        if (candidates.Count == 0)
        {
            failure = OpenFailure.UnknownKey;
            return null;
        }

        var nonce = data.Slice(HeaderSize, NonceSize);
        var cipherLength = data.Length - HeaderSize - NonceSize - TagSize;
        var ciphertext = data.Slice(HeaderSize + NonceSize, cipherLength);
        var tag = data.Slice(HeaderSize + NonceSize + cipherLength, TagSize);
        var plaintext = new byte[cipherLength];
        var aad = Aad(label, data[0], keyId, context);

        foreach (var key in candidates)
        {
            try
            {
                using var aes = new AesGcm(key, TagSize);
                aes.Decrypt(nonce, ciphertext, tag, plaintext, aad);
                return plaintext;
            }
            catch (CryptographicException)
            {
                // Tag mismatch; try the other key if both share the id byte.
            }
        }

        failure = OpenFailure.Tampered;
        return null;
    }

    /// <summary>Base64url convenience over <see cref="Seal"/>.</summary>
    public string SealString(ReadOnlySpan<byte> plaintext, string label, string context = "") =>
        Base64Url.Encode(Seal(plaintext, label, context));

    /// <summary>Base64url convenience over <see cref="Open"/>; null on any failure.</summary>
    public byte[]? OpenString(string? sealedValue, string label, string context = "") =>
        Base64Url.Decode(sealedValue) is { } data ? Open(data, label, out _, context) : null;

    private static byte KeyId(byte[] key) => SHA256.HashData(key)[0];

    private static byte[] Aad(string label, byte format, byte keyId, string context)
    {
        var labelBytes = Encoding.ASCII.GetBytes(label);
        var contextBytes = Encoding.UTF8.GetBytes(context);
        var aad = new byte[labelBytes.Length + HeaderSize + contextBytes.Length];
        labelBytes.CopyTo(aad, 0);
        aad[labelBytes.Length] = format;
        aad[labelBytes.Length + 1] = keyId;
        contextBytes.CopyTo(aad, labelBytes.Length + HeaderSize);
        return aad;
    }
}

/// <summary>Unpadded base64url, the form every value the browser and the server exchange uses.</summary>
public static class Base64Url
{
    public static string Encode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    /// <summary>Accepts base64url or standard base64, padded or not; null when it is neither.</summary>
    public static byte[]? Decode(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return null;

        var s = value.Trim().Replace('-', '+').Replace('_', '/');
        s = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
        try
        {
            return Convert.FromBase64String(s);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
