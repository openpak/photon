using OpenPak.Server.Host;
using OpenPak.Server.LoadBalancing;
using Xunit;

namespace OpenPak.Server.Tests;

/// <summary>
/// The health endpoint exists so something outside this process can tell a
/// running server from a silent one — the UDP listeners cannot answer that.
/// Its player count has to come from live rooms, or a status page would report
/// a number nobody is behind.
/// </summary>
public class HealthEndpointTests
{
    [Fact]
    public void HealthAnswersOk()
    {
        var json = HealthHostedService.Payload("/health", new RoomRegistry(), out var status);
        Assert.Equal(200, status);
        Assert.Contains("\"ok\"", json);
    }

    [Fact]
    public void PlayersCountsLiveRoomMembership()
    {
        var rooms = new RoomRegistry();
        var a = rooms.GetOrCreate("ROOM-A");
        a.AddActor();
        a.AddActor();
        rooms.GetOrCreate("ROOM-B").AddActor();

        var json = HealthHostedService.Payload("/players", rooms, out var status);
        Assert.Equal(200, status);
        Assert.Contains("\"players\":3", json);
        Assert.Contains("\"rooms\":2", json);
    }

    [Fact]
    public void EmptyServerReportsZeroRatherThanFailing()
    {
        // An idle server is healthy; "nobody is playing" and "not running" are
        // different answers and the endpoint must distinguish them.
        var json = HealthHostedService.Payload("/players", new RoomRegistry(), out var status);
        Assert.Equal(200, status);
        Assert.Contains("\"players\":0", json);
    }

    [Fact]
    public void UnknownPathIsNotFound()
    {
        HealthHostedService.Payload("/admin", new RoomRegistry(), out var status);
        Assert.Equal(404, status);
    }
}
