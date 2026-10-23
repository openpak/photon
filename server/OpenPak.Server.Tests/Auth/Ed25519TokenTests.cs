using OpenPak.Server.LoadBalancing;
using Xunit;

namespace OpenPak.Server.Tests.Auth;

/// <summary>Auth tokens are Ed25519-signed: a region node holds home's public key, checks every token home
/// minted, and cannot mint one of its own.</summary>
public class Ed25519TokenTests
{
    private static readonly byte[] Seed = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();

    [Fact]
    public void NodeVerifiesHomeTokensButCannotMint()
    {
        var home = TokenKeys.FromSeed(Seed);
        var node = TokenKeys.VerifyOnly(Convert.FromBase64String(home.PublicKeyBase64));
        Assert.True(home.CanMint);
        Assert.False(node.CanMint);

        var token = AuthToken.Mint("openpak:AB", true, home, "app", "1.0", "sa");
        Assert.True(AuthToken.Validate(token, node).TryGet(out var id));
        Assert.Equal(("openpak:AB", true, "app", "1.0", "sa"), (id.UserId, id.Verified, id.AppId, id.AppVersion, id.Region));
        Assert.Throws<InvalidOperationException>(() => AuthToken.Mint("x", false, node));
    }

    [Fact]
    public void AnotherKeyOrATamperedRegionFails()
    {
        var home = TokenKeys.FromSeed(Seed);
        var other = TokenKeys.FromSeed(new byte[32]);
        var token = AuthToken.Mint("u", false, home, "a", "v", "sa");
        Assert.False(AuthToken.Validate(token, other).IsOk);
        Assert.False(AuthToken.Validate(token.Replace("|sa.", "|jp."), home).IsOk);
        Assert.False(AuthToken.Validate("u|0.not-base64!", home).IsOk);
        Assert.False(AuthToken.Validate("nodot", home).IsOk);
    }

    [Fact]
    public void NoRegionKeepsTheOldBodyShapes()
    {
        var home = TokenKeys.FromSeed(Seed);
        Assert.StartsWith("u|1.", AuthToken.Mint("u", true, home));
        Assert.StartsWith("u|0|a|v.", AuthToken.Mint("u", false, home, "a", "v"));
        Assert.True(AuthToken.Validate(AuthToken.Mint("u", false, home, "a", "v"), home).TryGet(out var id));
        Assert.Null(id.Region);
    }

    [Fact]
    public void SecretDerivedKeysAreStableAndFlagTheDefault()
    {
        Assert.Equal(TokenKeys.FromSecret("s1").PublicKeyBase64, TokenKeys.FromSecret("s1").PublicKeyBase64);
        Assert.NotEqual(TokenKeys.FromSecret("s1").PublicKeyBase64, TokenKeys.FromSecret("s2").PublicKeyBase64);
        Assert.True(TokenKeys.FromSecret(OpenPak.Server.Core.ServerOptions.DefaultSecret).IsShippedDefault);
        Assert.False(TokenKeys.FromSecret("s1").IsShippedDefault);
        var cfg = TokenKeys.FromConfig("s1", null, TokenKeys.FromSecret("s1").PublicKeyBase64);
        Assert.False(cfg.CanMint);
        Assert.True(AuthToken.Validate(AuthToken.Mint("u", "s1"), cfg).IsOk);
        Assert.True(TokenKeys.FromConfig("s1", Convert.ToBase64String(Seed), null).CanMint);
    }
}
