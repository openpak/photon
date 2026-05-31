using System.Numerics;
using System.Security.Cryptography;

namespace BlackIce.Photon.Crypto;

/// <summary>
/// Server side of Photon's Diffie-Hellman key exchange. Generates an Oakley-768 keypair,
/// derives a shared secret from the client's public key, hashes it (SHA-256) into an AES-256
/// key, and encrypts/decrypts operation payloads (AES-CBC, zero IV, PKCS7) — matching the
/// client's <c>DiffieHellmanCryptoProvider</c> exactly. Verified against the real provider.
/// </summary>
public sealed class DiffieHellmanCryptoProvider
{
    private static readonly BigInteger Prime = new(OakleyGroups.OakleyPrime768, isUnsigned: true, isBigEndian: true);
    private static readonly BigInteger Generator = OakleyGroups.Generator;

    private readonly BigInteger _secret;
    private readonly BigInteger _publicKey;
    private Aes? _aes;

    public DiffieHellmanCryptoProvider()
    {
        _secret = GenerateSecret(160);
        _publicKey = BigInteger.ModPow(Generator, _secret, Prime);
    }

    public bool IsInitialized => _aes != null;

    /// <summary>Our public key as Photon transmits it: minimal big-endian magnitude bytes.</summary>
    public byte[] PublicKey => _publicKey.ToByteArray(isUnsigned: true, isBigEndian: true);

    public void DeriveSharedKey(byte[] otherPartyPublicKey)
    {
        // The public key arrives from the network. Reject null/empty/oversized blobs (a 768-bit key is
        // at most 96 magnitude bytes) and degenerate values (0, 1, or >= the modulus) before ModPow,
        // so a malformed or small-subgroup key can't produce a weak shared secret or throw deep in BigInteger.
        if (otherPartyPublicKey is null || otherPartyPublicKey.Length is 0 or > 96)
            throw new ArgumentException($"DH public key must be 1..96 bytes (got {otherPartyPublicKey?.Length ?? 0})", nameof(otherPartyPublicKey));
        var other = new BigInteger(otherPartyPublicKey, isUnsigned: true, isBigEndian: true);
        if (other <= BigInteger.One || other >= Prime)
            throw new ArgumentException("DH public key out of range (degenerate or >= modulus)", nameof(otherPartyPublicKey));
        var shared = BigInteger.ModPow(other, _secret, Prime);
        var sharedBytes = shared.ToByteArray(isUnsigned: true, isBigEndian: true);

        byte[] key = SHA256.HashData(sharedBytes);
        _aes = Aes.Create();
        _aes.Key = key;
        _aes.IV = new byte[16];
        _aes.Mode = CipherMode.CBC;
        _aes.Padding = PaddingMode.PKCS7;
    }

    public byte[] Encrypt(byte[] data) => Encrypt(data, 0, data.Length);

    public byte[] Encrypt(byte[] data, int offset, int count)
    {
        EnsureRange(data, offset, count);
        using var enc = Cipher().CreateEncryptor();
        return enc.TransformFinalBlock(data, offset, count);
    }

    public byte[] Decrypt(byte[] data) => Decrypt(data, 0, data.Length);

    public byte[] Decrypt(byte[] data, int offset, int count)
    {
        EnsureRange(data, offset, count);
        using var dec = Cipher().CreateDecryptor();
        return dec.TransformFinalBlock(data, offset, count);
    }

    /// <summary>The AES instance, or a clear error if a payload is (de)crypted before the key handshake completed.</summary>
    private Aes Cipher() => _aes ?? throw new InvalidOperationException("Crypto not initialized; call DeriveSharedKey first");

    private static void EnsureRange(byte[] data, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (offset < 0 || count < 0 || offset > data.Length - count)
            throw new ArgumentOutOfRangeException(nameof(count), $"offset {offset} + count {count} exceeds buffer {data.Length}");
    }

    private static BigInteger GenerateSecret(int bits)
    {
        // Rejection sampling for a secret in [1, Prime-2]. The acceptance probability is ~1, so the
        // bound only guards against a broken RNG looping forever rather than reflecting real retries.
        for (int attempt = 0; attempt < 256; attempt++)
        {
            var bytes = new byte[bits / 8];
            RandomNumberGenerator.Fill(bytes);
            var v = new BigInteger(bytes, isUnsigned: true, isBigEndian: true);
            if (v != 0 && v < Prime - 1) return v;
        }
        throw new CryptographicException("Failed to generate a Diffie-Hellman secret in range");
    }
}
