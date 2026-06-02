using BlackIce.Server.LoadBalancing;
using Xunit;

namespace BlackIce.Server.Tests.Auth;

public class AuthTokenVerifiedClaimTests
{
    private const string Secret = "test-secret";

    [Fact]
    public void Verified_flag_round_trips()
    {
        Assert.True(AuthToken.Validate(AuthToken.Mint("76561198000000000", true, Secret), Secret).TryGet(out var v));
        Assert.True(v.Verified);

        Assert.True(AuthToken.Validate(AuthToken.Mint("76561198000000000", false, Secret), Secret).TryGet(out var u));
        Assert.False(u.Verified);
    }

    [Fact]
    public void Recovers_the_user_id()
    {
        Assert.True(AuthToken.Validate(AuthToken.Mint("76561198000000000", true, Secret), Secret).TryGet(out var v));
        Assert.Equal("76561198000000000", v.UserId);
    }

    [Fact]
    public void Tampering_with_the_flag_fails_validation()
    {
        var token = AuthToken.Mint("76561198000000000", false, Secret);   // body "76561198000000000|0"
        var forged = token.Replace("|0.", "|1.");                          // flip the flag, keep the old signature
        Assert.NotEqual(token, forged);
        Assert.False(AuthToken.Validate(forged, Secret).IsOk);        // HMAC is over the body → mismatch
    }

    [Fact]
    public void Legacy_unflagged_token_validates_as_unverified()
    {
        // Mint(userId, secret) produces the old "userId.sig" shape (no flag); it must still validate, unverified.
        Assert.True(AuthToken.Validate(AuthToken.Mint("76561198000000000", Secret), Secret).TryGet(out var v));
        Assert.Equal("76561198000000000", v.UserId);
        Assert.False(v.Verified);
    }

    [Fact]
    public void Wrong_secret_is_rejected()
    {
        var token = AuthToken.Mint("76561198000000000", true, Secret);
        Assert.False(AuthToken.Validate(token, "different-secret").IsOk);
    }
}
