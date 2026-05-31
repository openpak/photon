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
        var other = new BigInteger(otherPartyPublicKey, isUnsigned: true, isBigEndian: true);
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
        using var enc = _aes!.CreateEncryptor();
        return enc.TransformFinalBlock(data, offset, count);
    }

    public byte[] Decrypt(byte[] data) => Decrypt(data, 0, data.Length);

    public byte[] Decrypt(byte[] data, int offset, int count)
    {
        using var dec = _aes!.CreateDecryptor();
        return dec.TransformFinalBlock(data, offset, count);
    }

    private static BigInteger GenerateSecret(int bits)
    {
        while (true)
        {
            var bytes = new byte[bits / 8];
            RandomNumberGenerator.Fill(bytes);
            var v = new BigInteger(bytes, isUnsigned: true, isBigEndian: true);
            if (v != 0 && v < Prime - 1) return v;
        }
    }
}
