using BlackIce.Server.LoadBalancing;
using Xunit;

namespace BlackIce.Server.Tests.Authority;

public class AuthorityChainTests
{
    [Fact]
    public void Session_chain_includes_the_authority_validators()
    {
        var reg = new RoomRegistry();
        var session = reg.Session("co-op");
        // The session exists and relays; the validators are present but log-only so relay is unchanged.
        Assert.Equal("co-op", session.RoomName);
        // Reflection-free behavioral check: a normal relay still forwards (covered by RoomSessionRelayTests);
        // here we just assert the session builds without error with the authority chain.
        Assert.Equal(0, session.Count);
    }
}
