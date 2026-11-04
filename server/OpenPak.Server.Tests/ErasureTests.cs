using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OpenPak.Photon;
using OpenPak.Server.Core;
using OpenPak.Server.Data;
using OpenPak.Server.LoadBalancing;
using Xunit;

namespace OpenPak.Server.Tests;

public class ErasureTests
{
    const string AcctA = "11111111-1111-1111-1111-111111111111", AcctB = "22222222-2222-2222-2222-222222222222";
    static readonly DateTime T0 = new(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);

    sealed class Links : ILinkSource
    {
        public Task<IReadOnlyList<string>> SwitchSubjectsAsync(string id, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(id == AcctA ? new[] { "baas-a" } : id == AcctB ? new[] { "baas-b" } : Array.Empty<string>());
    }

    sealed class Factory(SqliteConnection c) : IDbContextFactory<OpenPakDbContext>
    {
        public OpenPakDbContext CreateDbContext() => new(new DbContextOptionsBuilder<OpenPakDbContext>().UseSqlite(c).Options);
    }

    sealed class NullHandler : IOperationHandler
    {
        public void OnConnect(PeerConnection peer) { }
        public void OnOperationRequest(PeerConnection peer, OperationRequest request) { }
        public void OnDisconnect(PeerConnection peer) { }
    }

    sealed class Refuse : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    }

    static PeerConnection Peer(string userId, string appId) =>
        new("t", new IPEndPoint(IPAddress.Loopback, 0), new NullHandler(), (_, _) => { }) { SteamId = userId, AppId = appId };

    [Fact]
    public async Task Erase_removes_one_account_everywhere_and_is_idempotent()
    {
        var conn = new SqliteConnection("DataSource=:memory:"); conn.Open();
        var store = new SqlRewardsStore(new Factory(conn));
        var reg = new RoomRegistry();
        var ids = new AccountIdentityRegistry();
        var ua = ids.Register("bsdid:a"); var ub = ids.Register("bsdid:b");
        var key = RoomRegistry.KeyFor("app-1", "1", "room");
        reg.GetOrCreate(key);
        var sess = reg.Session(key);
        sess.Join(1, Peer(ua, "app-1")); sess.Join(2, Peer(ub, "app-1"));
        var now = T0;
        var w = new RewardsWatcher(reg, ids, PhotonRewards.Parse("app-1=outbound"), TimeSpan.FromMinutes(3),
            id => Task.FromResult<string?>("baas-" + id[^1]), _ => { }, () => now, store);
        await w.TickAsync(); now = T0.AddMinutes(4); await w.TickAsync();
        var emitter = new RewardsEmitter(new HttpClient(new Refuse()), "http://rewards", "k", store, TimeSpan.FromHours(1));
        emitter.Enqueue(new SessionEvent("e-a", "m", "r", "baas-a", T0));
        emitter.Enqueue(new SessionEvent("e-b", "m", "r", "baas-b", T0));
        var nodes = new NodeDirectory(new HttpClient(), null, null, null, null);
        nodes.SetCountry(ua, "SE"); nodes.SetCountry(ub, "DE");

        var hub = new ErasureHub { Store = store, Watcher = w, Emitter = emitter, Identities = ids, Nodes = nodes };
        var eraser = new AccountEraser(new Links(), hub, "k");
        var rows = await eraser.EraseAsync(AcctA, CancellationToken.None);
        Assert.True(rows >= 5, rows.ToString());   // subject, 2 clock keys, event, identity, country
        Assert.Null(ids.Find(ua));
        Assert.NotNull(ids.Find(ub));
        Assert.Null(nodes.CountryOf(ua));
        Assert.Equal("DE", nodes.CountryOf(ub));
        Assert.Equal(1, emitter.Pending);
        var subjects = store.Get("subjects")!;
        Assert.DoesNotContain("baas-a", subjects);
        Assert.Contains("baas-b", subjects);
        var clock = store.Get("clock")!;
        Assert.DoesNotContain("baas-a", clock);
        Assert.Contains("baas-b", clock);
        Assert.DoesNotContain("baas-a", store.Get("outbox")!);
        Assert.Equal(0, await eraser.EraseAsync(AcctA, CancellationToken.None));
        await emitter.DisposeAsync();
    }

    [Fact]
    public async Task Erase_works_on_the_stored_state_when_rewards_is_off()
    {
        var conn = new SqliteConnection("DataSource=:memory:"); conn.Open();
        var store = new SqlRewardsStore(new Factory(conn));
        store.Put("subjects", "{\"openpak:1\":\"baas-a\",\"openpak:2\":\"baas-b\"}");
        store.Put("outbox", "[{\"EventId\":\"x\",\"Metric\":\"m\",\"RoomId\":\"r\",\"Subject\":\"baas-a\",\"At\":\"2026-11-04T12:00:00Z\"}]");
        var eraser = new AccountEraser(new Links(), new ErasureHub { Store = store }, "k");
        Assert.Equal(2, await eraser.EraseAsync(AcctA, CancellationToken.None));
        Assert.Equal("{\"openpak:2\":\"baas-b\"}", store.Get("subjects"));
        Assert.Equal("[]", store.Get("outbox"));
        Assert.Equal(0, await eraser.EraseAsync(AcctA, CancellationToken.None));
    }

    [Fact]
    public void Endpoint_checks_key_uuid_and_configuration()
    {
        var hub = new ErasureHub();
        Assert.Equal(503, new AccountEraser(new Links(), hub, null).Handle(AcctA, "Bearer k").Status);
        var e = new AccountEraser(new Links(), hub, "k");
        Assert.Equal(401, e.Handle(AcctA, null).Status);
        Assert.Equal(401, e.Handle(AcctA, "Bearer wrong").Status);
        Assert.Equal(401, e.Handle(AcctA, "k").Status);
        Assert.Equal(400, e.Handle("not-a-uuid", "Bearer k").Status);
        var ok = e.Handle(AcctA, "Bearer k");
        Assert.Equal(200, ok.Status);
        Assert.Equal("{\"service\":\"photon\",\"rows\":0}", ok.Json);
    }

    [Fact]
    public void Parses_the_links_response()
    {
        static byte[] Str(int no, string s) => new[] { (byte)((no << 3) | 2), (byte)s.Length }.Concat(Encoding.UTF8.GetBytes(s)).ToArray();
        byte[] Link(string ns, string sub) => Str(2, ns).Concat(Str(3, sub)).ToArray();
        var l1 = Link("switch", "aaaa"); var l2 = Link("nex", "77");
        var msg = new[] { (byte)0x0a, (byte)l1.Length }.Concat(l1).Concat(new[] { (byte)0x0a, (byte)l2.Length }).Concat(l2).ToArray();
        var framed = new byte[] { 0, 0, 0, 0, (byte)msg.Length }.Concat(msg).ToArray();
        Assert.Equal(new[] { "aaaa" }, CoreLinks.ParseResponse(framed));
    }
}
