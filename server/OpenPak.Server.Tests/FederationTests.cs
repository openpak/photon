using System.Net;
using System.Text;
using System.Text.Json;
using OpenPak.Photon;
using OpenPak.Server.Core;
using OpenPak.Server.Host;
using OpenPak.Server.LoadBalancing;
using Xunit;

namespace OpenPak.Server.Tests;

/// <summary>
/// Region routing. The resolver runs the same fixture as the account core's Go resolver
/// (RoutingCases.json = account/pkg/routing/testdata/cases.json; RoutingRegions.json = the registry the
/// core serves at /internal/v1/routing). A new room goes where its creator chose, else their country's
/// region, else down the fallback chain, else home; a joiner goes wherever the room already is; and with no
/// routing table or no nodes nothing differs from a home-only server.
/// </summary>
public class FederationTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, name));

    private static RoutingTable Table(string? nodesJson = null)
    {
        var t = RoutingTable.Parse(Fixture("RoutingRegions.json"))!;
        using var cases = JsonDocument.Parse(nodesJson ?? Fixture("RoutingCases.json"));
        t.Nodes = JsonSerializer.Deserialize<List<RoutingNode>>(cases.RootElement.GetProperty("nodes").GetRawText())!;
        return t;
    }

    private static NodeDirectory Directory(RoutingTable? table)
    {
        var nodes = new NodeDirectory(new HttpClient(), "http://core", "rk", null, null) { Table = table };
        return nodes;
    }

    [Fact]
    public void SharedFixtureCases()
    {
        var t = Table();
        using var doc = JsonDocument.Parse(Fixture("RoutingCases.json"));
        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            string? S(string k) => c.TryGetProperty(k, out var v) ? v.GetString() : null;
            var stack = S("stack")!;
            var service = stack switch { "photon" => "photon", "amongus" => "impostor", "playfab" => "playfab_party", "npln" => "turn", "gcp" => "stun", _ => "" };
            var r = t.Resolve(stack, S("token"), S("country"), c.TryGetProperty("tester", out var te) && te.GetBoolean(), service);
            var want = S("want_node");
            Assert.True((want == "" ? null : want) == r.Node && S("want_region") == r.Region && S("want_via") == r.Via,
                $"{S("name")}: got node={r.Node} region={r.Region} via={r.Via}");
        }
    }

    [Fact]
    public void ExplicitRegionPlacesTheRoom_JoinerFollowsIt()
    {
        var nodes = Directory(Table());
        nodes.SetCountry("host", "DE");   // a German who picked South America in game
        nodes.SetCountry("guest", "FR");
        var registry = new RoomRegistry();
        var master = new MasterServerHandler("home:5056", "secret", registry, nodes: nodes);

        Assert.Equal("198.51.100.10:5056", master.GameAddressFor("Q35V9E", existed: false, userId: "host", region: "sa"));
        Assert.Equal("br-gru-1", nodes.NodeOfRoom("Q35V9E"));
        // The node reports the host in; now the room "exists" with people in it.
        new RoomReport { Node = "br-gru-1", Room = "Q35V9E", Actors = { 1 } }.ApplyTo(registry, nodes);
        Assert.Equal("198.51.100.10:5056", master.GameAddressFor("Q35V9E", existed: true, userId: "guest", region: "eu"));

        // No choice: the country decides; Brazil goes to Brazil, an unknown user and Europe stay home.
        nodes.SetCountry("carioca", "BR");
        Assert.Equal("198.51.100.10:5056", master.GameAddressFor("R1", existed: false, userId: "carioca"));
        Assert.Equal("home:5056", master.GameAddressFor("R2", existed: false, userId: "guest"));
        Assert.Equal("home:5056", master.GameAddressFor("R3", existed: false, userId: "nobody"));
        // Explicit Europe from Brazil is home: the choice wins over the country.
        Assert.Equal("home:5056", master.GameAddressFor("R4", existed: false, userId: "carioca", region: "eu"));
        // A token we do not know is home, never an error.
        Assert.Equal("home:5056", master.GameAddressFor("R5", existed: false, userId: "carioca", region: "nx"));
        Assert.Null(nodes.NodeOfRoom("R5"));
    }

    [Fact]
    public void TestNodeOnlyForTesters()
    {
        var nodes = Directory(Table());
        var master = new MasterServerHandler("home:5056", "secret", new RoomRegistry(), nodes: nodes);
        nodes.SetCountry("player", "JP");
        nodes.SetCountry("owner", "JP");
        nodes.SetTester("owner", true, IPAddress.Parse("192.0.2.7"));

        Assert.Equal("home:5056", master.GameAddressFor("A", existed: false, userId: "player", region: "jp"));
        Assert.Equal("198.51.100.30:5056", master.GameAddressFor("B", existed: false, userId: "owner", region: "jp"));

        // GetRegions: the public list never shows the test node; the tester's address sees it.
        var ns = new NameServerHandler("home:5055", "secret", null!, nodes: nodes);
        Assert.Equal(new[] { "eu", "us", "sa" }, (string[])ns.GetRegions(new IPEndPoint(IPAddress.Parse("198.51.100.99"), 1)).Parameters[PhotonCodes.Param.Region]);
        var t = ns.GetRegions(new IPEndPoint(IPAddress.Parse("192.0.2.7"), 1));
        Assert.Equal(new[] { "eu", "us", "sa", "jp" }, (string[])t.Parameters[PhotonCodes.Param.Region]);
        // Each region's address is one the client can ping: the node's ping responder when it has one.
        Assert.Equal(new[] { "home:5055", "198.51.100.20:5056", "198.51.100.10:5055", "198.51.100.30:5056" },
                     (string[])t.Parameters[PhotonCodes.Param.Address]);
    }

    /// <summary>Home-only operation: no routing configured, or a table with no nodes, answers exactly as a
    /// server without federation: only "eu" at the home Master, every room at home's Game address.</summary>
    [Fact]
    public void ZeroNodesIsExactlyHomeOnly()
    {
        var empty = Table("""{"nodes":[]}""");
        foreach (var nodes in new NodeDirectory?[] { null, new(new HttpClient(), null, null, null, null), Directory(null), Directory(empty) })
        {
            nodes?.SetCountry("u", "BR");
            nodes?.SetTester("u", true, IPAddress.Parse("192.0.2.7"));
            var master = new MasterServerHandler("home:5056", "secret", new RoomRegistry(), nodes: nodes);
            foreach (var region in new string?[] { null, "sa", "eu", "jp", "nx" })
                Assert.Equal("home:5056", master.GameAddressFor("R", existed: false, userId: "u", region: region));
            Assert.Equal("home:5056", master.GameAddressFor("R", existed: true, userId: "u"));

            var ns = new NameServerHandler("home:5055", "secret", null!, nodes: nodes);
            var r = ns.GetRegions(new IPEndPoint(IPAddress.Parse("192.0.2.7"), 1));
            Assert.Equal(new[] { "eu" }, (string[])r.Parameters[PhotonCodes.Param.Region]);
            Assert.Equal(new[] { "home:5055" }, (string[])r.Parameters[PhotonCodes.Param.Address]);
            Assert.Equal(2, r.Parameters.Count);
        }
    }

    [Fact]
    public void RoomOnANodeThatLeftRotationIsServedHome()
    {
        var nodes = Directory(Table());
        nodes.SetCountry("host", "BR");
        var master = new MasterServerHandler("home:5056", "secret", new RoomRegistry(), nodes: nodes);
        Assert.Equal("198.51.100.10:5056", master.GameAddressFor("Q", existed: false, userId: "host"));
        nodes.Table = Table("""{"nodes":[]}""");
        Assert.Equal("home:5056", master.GameAddressFor("Q", existed: true, userId: "guest"));
    }

    [Fact]
    public void Param210IsAcceptedWhateverItSays()
    {
        OperationRequest Req(object? v) => new(PhotonCodes.Op.Authenticate, v is null ? new() : new() { { PhotonCodes.Param.Region, v } });
        Assert.Equal("sa", NameServerHandler.RegionOf(Req("sa/*")));
        Assert.Equal("us", NameServerHandler.RegionOf(Req("US/ussc")));
        Assert.Equal("eu", NameServerHandler.RegionOf(Req("eu")));
        Assert.Null(NameServerHandler.RegionOf(Req(null)));
        Assert.Null(NameServerHandler.RegionOf(Req(42)));
        Assert.Null(NameServerHandler.RegionOf(Req("<script>")));
        Assert.Null(NameServerHandler.RegionOf(Req(new string('a', 40))));
    }

    [Fact]
    public async Task CountryAndTesterAreAskedAtAuth()
    {
        var http = new HttpClient(new Fake(req => req.RequestUri!.AbsolutePath switch
        {
            "/internal/photon-identity" => """{"pid":7,"country":"br","baas_user_id":"00aa11bb22cc33dd"}""",
            "/internal/v1/routing/account" when req.RequestUri.Query == "?namespace=switch&subject=00aa11bb22cc33dd"
                                               && req.Headers.GetValues("X-Routing-Key").Single() == "rk" => """{"country":"BR","tester":true}""",
            _ => null,
        }));
        var nodes = new NodeDirectory(http, "http://core", "rk", "http://adapter", "ik");
        var ip = IPAddress.Parse("192.0.2.9");
        await nodes.PrefetchCountryAsync("u1", "bsdid:x", ip);
        Assert.Equal("BR", nodes.CountryOf("u1"));
        Assert.True(nodes.IsTester("u1"));
        Assert.True(nodes.IsTesterAddress(ip));
        Assert.False(nodes.IsTesterAddress(IPAddress.Parse("192.0.2.10")));

        // Routing off: the country is still asked (as before), the core never is.
        var calls = new List<string>();
        var off = new NodeDirectory(new HttpClient(new Fake(req => { calls.Add(req.RequestUri!.AbsolutePath); return """{"pid":7,"country":"BR","baas_user_id":"x"}"""; })),
                                    null, null, "http://adapter", "ik");
        await off.PrefetchCountryAsync("u2", "bsdid:y", ip);
        Assert.Equal(new[] { "/internal/photon-identity" }, calls);
        Assert.False(off.IsTester("u2"));
    }

    [Fact]
    public async Task RefreshKeepsTheLastGoodTable()
    {
        var fail = false;
        var body = $$"""{"home":"eu-west","regions":[],"nodes":null}""";
        var nodes = new NodeDirectory(new HttpClient(new Fake(_ => fail ? null : body)), "http://core/", "rk", null, null);
        await nodes.RefreshAsync();
        Assert.NotNull(nodes.Table);
        Assert.Empty(nodes.Table!.Nodes);   // Go's null list is an empty one here
        fail = true;
        await nodes.RefreshAsync();
        Assert.NotNull(nodes.Table);
    }

    [Fact]
    public void RoomReportRoundTripsLobbyProperties()
    {
        var nodes = Directory(Table());
        var registry = new RoomRegistry();
        var wire = JsonSerializer.Serialize(new RoomReport
        {
            Node = "br-gru-1", Room = "Q35V9E", Actors = { 1, 2 },
            GameProperties = { ["version"] = "1.2.3", ["vehicleType"] = 4, ["open"] = true },
        });
        JsonSerializer.Deserialize<RoomReport>(wire)!.ApplyTo(registry, nodes);
        var room = registry.Find("Q35V9E")!;
        Assert.Equal(2, room.ActorNumbers.Count);
        Assert.Equal("1.2.3", room.GameProperties["version"]);
        Assert.Equal(4, room.GameProperties["vehicleType"]);
        Assert.Equal(true, room.GameProperties["open"]);
        Assert.Equal("br-gru-1", nodes.NodeOfRoom("Q35V9E"));

        // Everyone left: the room is unpinned and a new creator may place it elsewhere.
        JsonSerializer.Deserialize<RoomReport>(JsonSerializer.Serialize(new RoomReport { Node = "br-gru-1", Room = "Q35V9E" }))!.ApplyTo(registry, nodes);
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

    [Fact]
    public void ReportsNeedTheirOwnNodesKey()
    {
        var keys = new Dictionary<string, string> { ["br-gru-1"] = "k-br", ["us-ash-1"] = "k-us" };
        Assert.True(HealthHostedService.NodeKeyMatches(keys, "br-gru-1", "k-br"));
        Assert.False(HealthHostedService.NodeKeyMatches(keys, "br-gru-1", "k-us"));   // one node speaking for another
        Assert.False(HealthHostedService.NodeKeyMatches(keys, "jp-x", "k-br"));
        Assert.False(HealthHostedService.NodeKeyMatches(new Dictionary<string, string>(), "br-gru-1", ""));
    }

    [Fact]
    public async Task ListenersAnswerTheRegionPing()
    {
        Assert.True(RegionPing.IsPing(new byte[] { 0x7d, 0x7d, 0x7d, 0x7d, 0x7d, 0x7d, 0x7d, 0x7d, 0x7d, 0x7d, 0x7d, 0x7d, 42 }));
        Assert.False(RegionPing.IsPing(new byte[12]));
        Assert.False(RegionPing.IsPing(new byte[13]));

        var listener = new UdpListener("ping-test", 0, new NullHandler());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = listener.RunAsync(cts.Token);
        using var client = new System.Net.Sockets.UdpClient();
        client.Connect(IPAddress.Loopback, listener.LocalPort);
        var ping = new byte[] { 0x7d, 0x7d, 0x7d, 0x7d, 0x7d, 0x7d, 0x7d, 0x7d, 0x7d, 0x7d, 0x7d, 0x7d, 9 };
        await client.SendAsync(ping, ping.Length);
        var echo = await client.ReceiveAsync(cts.Token);
        Assert.Equal(ping, echo.Buffer);
        cts.Cancel();
        try { await run; } catch (OperationCanceledException) { }
    }

    private sealed class NullHandler : IOperationHandler
    {
        public void OnConnect(PeerConnection peer) { }
        public void OnOperationRequest(PeerConnection peer, OperationRequest request) { }
        public void OnDisconnect(PeerConnection peer) { }
    }

    private sealed class Fake(Func<HttpRequestMessage, string?> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = answer(request);
            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
