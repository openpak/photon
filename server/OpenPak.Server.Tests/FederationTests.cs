using System.Text.Json;
using OpenPak.Photon;
using OpenPak.Server.Core;
using OpenPak.Server.LoadBalancing;
using Xunit;

namespace OpenPak.Server.Tests;

/// <summary>
/// A new room goes to the node for its creator's country; a joiner goes wherever the room already
/// is, whatever their own country; and a node's report puts its room in home's lobby.
/// </summary>
public class FederationTests
{
    private static NodeDirectory Directory()
    {
        var nodes = new NodeDirectory(new HttpClient(), null, null, null);
        var json = JsonDocument.Parse("""
            {"regions":[{"id":"au-sydney","country":"AU","services":{"photon":"203.0.113.9:22002"}},
                        {"id":"nz-akl","country":"NZ","services":{}}]}
            """);
        typeof(NodeDirectory).GetField("_regions", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(nodes, NodeDirectory.Parse(json.RootElement));
        return nodes;
    }

    [Fact]
    public void CreatorCountryPlacesTheRoom_JoinerFollowsIt()
    {
        var nodes = Directory();
        nodes.SetCountry("host", "au");
        nodes.SetCountry("guest", "FR");
        var registry = new RoomRegistry();
        var master = new MasterServerHandler("home:5056", "secret", registry, nodes: nodes);

        Assert.Equal("203.0.113.9:22002", master.GameAddressFor("Q35V9E", existed: false, userId: "host"));
        // The node reports the host in; now the room "exists" with people in it.
        new RoomReport { Node = "au-sydney", Room = "Q35V9E", Actors = { 1 } }.ApplyTo(registry, nodes);
        Assert.Equal("203.0.113.9:22002", master.GameAddressFor("Q35V9E", existed: true, userId: "guest"));

        // A country with no Photon address, and an unknown user, both stay home.
        Assert.Equal("home:5056", master.GameAddressFor("R2", existed: false, userId: "nzuser"));
        Assert.Equal("home:5056", master.GameAddressFor("R3", existed: false, userId: "nobody"));
    }

    [Fact]
    public void RoomReportRoundTripsLobbyProperties()
    {
        var nodes = Directory();
        var registry = new RoomRegistry();
        var wire = JsonSerializer.Serialize(new RoomReport
        {
            Node = "au-sydney", Room = "Q35V9E", Actors = { 1, 2 },
            GameProperties = { ["version"] = "1.2.3", ["vehicleType"] = 4, ["open"] = true },
        });
        JsonSerializer.Deserialize<RoomReport>(wire)!.ApplyTo(registry, nodes);
        var room = registry.Find("Q35V9E")!;
        Assert.Equal(2, room.ActorNumbers.Count);
        Assert.Equal("1.2.3", room.GameProperties["version"]);
        Assert.Equal(4, room.GameProperties["vehicleType"]);
        Assert.Equal(true, room.GameProperties["open"]);
        Assert.Equal("au-sydney", nodes.NodeOfRoom("Q35V9E"));

        // Everyone left: the room is unpinned and a new creator may place it elsewhere.
        JsonSerializer.Deserialize<RoomReport>(JsonSerializer.Serialize(new RoomReport { Node = "au-sydney", Room = "Q35V9E" }))!.ApplyTo(registry, nodes);
        Assert.Null(nodes.NodeOfRoom("Q35V9E"));
    }

    [Fact]
    public void ReportFromRegistryKeepsOnlyLobbySafeValues()
    {
        var room = new Room { Name = "X", Key = "X" };
        room.AddActor();
        room.SetProperties(null, new System.Collections.Hashtable { ["version"] = "1", ["blob"] = new byte[] { 1 }, [(byte)253] = true });
        var r = RoomReport.From("au-sydney", room, "bsdid:abc");
        Assert.Equal(new[] { 1 }, r.Actors);
        Assert.Equal("1", r.GameProperties["version"]);
        Assert.False(r.GameProperties.ContainsKey("blob"));
        Assert.Equal("bsdid:abc", r.Identity);
    }
}
