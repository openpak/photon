using BlackIce.Photon;
using BlackIce.Server.LoadBalancing;
using Xunit;

namespace BlackIce.Server.Tests;

public class ServerMessageTests
{
    [Fact]
    public void ServerMessage_event_carries_text_under_customdata_key()
    {
        var ev = GameServerHandler.ServerMessageEvent("hello world");
        Assert.Equal(199, ev.Code);
        Assert.Equal("hello world", ev.Parameters[245]);
    }
}
