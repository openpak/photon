using BlackIce.Server.LoadBalancing;
using Xunit;

namespace BlackIce.Server.Tests;

public class RoomSessionRegistryTests
{
    [Fact]
    public void Session_is_created_once_per_room_name()
    {
        var reg = new RoomRegistry();
        var s1 = reg.Session("co-op");
        var s2 = reg.Session("co-op");
        Assert.Same(s1, s2);
        Assert.Equal("co-op", s1.RoomName);
    }

    [Fact]
    public void Different_rooms_get_different_sessions()
    {
        var reg = new RoomRegistry();
        Assert.NotSame(reg.Session("co-op"), reg.Session("pvp"));
    }
}
