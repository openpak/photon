using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace OpenPak.Server.LoadBalancing;

/// <summary>
/// The keys auth tokens are signed and checked with: Ed25519, so a region node holds only the public
/// key and can check every token home minted but never mint one (federation-worldwide-plan.md §3.6).
///
/// <para>Home signs with <c>Server.TokenSigningKey</c> (a base64 32-byte seed) or, when that is unset, with a
/// key derived from <c>Server.Secret</c>, so a home-only server needs no new setting. A node is configured
/// with <c>Server.TokenPublicKey</c> only. <see cref="PublicKeyBase64"/> is what home logs at start for the
/// node operator.</para>
/// </summary>
public sealed class TokenKeys
{
    private readonly Ed25519PrivateKeyParameters? _private;
    private readonly Ed25519PublicKeyParameters _public;

    private TokenKeys(Ed25519PrivateKeyParameters? priv, Ed25519PublicKeyParameters pub)
    {
        _private = priv;
        _public = pub;
    }

    /// <summary>False on a node: it verifies, it cannot mint.</summary>
    public bool CanMint => _private is not null;

    /// <summary>True when the signing key came from the shipped default secret, so anyone with the source
    /// could mint: the Name Server then refuses to vouch for a verified identity.</summary>
    public bool IsShippedDefault { get; private init; }

    public string PublicKeyBase64 => Convert.ToBase64String(_public.GetEncoded());

    /// <summary>A signing key from a 32-byte seed.</summary>
    public static TokenKeys FromSeed(byte[] seed)
    {
        if (seed.Length != 32) throw new ArgumentException("an Ed25519 seed is 32 bytes", nameof(seed));
        var priv = new Ed25519PrivateKeyParameters(seed, 0);
        return new TokenKeys(priv, priv.GeneratePublicKey());
    }

    /// <summary>A verify-only key (a node).</summary>
    public static TokenKeys VerifyOnly(byte[] publicKey)
    {
        if (publicKey.Length != 32) throw new ArgumentException("an Ed25519 public key is 32 bytes", nameof(publicKey));
        return new TokenKeys(null, new Ed25519PublicKeyParameters(publicKey, 0));
    }

    private static readonly ConcurrentDictionary<string, TokenKeys> FromSecretCache = new(StringComparer.Ordinal);

    /// <summary>The signing key derived from a shared secret: SHA-256 over a fixed label and the secret.
    /// Deterministic, so every home process with the same secret mints the same tokens.</summary>
    public static TokenKeys FromSecret(string secret) => FromSecretCache.GetOrAdd(secret, s =>
    {
        var seed = SHA256.HashData(Encoding.UTF8.GetBytes("openpak-photon-token-ed25519-v1|" + s));
        var k = FromSeed(seed);
        return new TokenKeys(k._private, k._public) { IsShippedDefault = s == OpenPak.Server.Core.ServerOptions.DefaultSecret };
    });

    /// <summary>The keys a process runs with: a node's public key, else home's seed, else the secret.</summary>
    public static TokenKeys FromConfig(string secret, string? signingKey, string? publicKey)
    {
        if (!string.IsNullOrWhiteSpace(signingKey)) return FromSeed(Convert.FromBase64String(signingKey.Trim()));
        if (!string.IsNullOrWhiteSpace(publicKey)) return VerifyOnly(Convert.FromBase64String(publicKey.Trim()));
        return FromSecret(secret);
    }

    public byte[] Sign(byte[] message)
    {
        if (_private is null) throw new InvalidOperationException("verify-only token keys cannot mint (region node)");
        var s = new Ed25519Signer();
        s.Init(true, _private);
        s.BlockUpdate(message, 0, message.Length);
        return s.GenerateSignature();
    }

    public bool Verify(byte[] message, byte[] signature)
    {
        if (signature.Length != 64) return false;
        var v = new Ed25519Signer();
        v.Init(false, _public);
        v.BlockUpdate(message, 0, message.Length);
        return v.VerifySignature(signature);
    }
}
