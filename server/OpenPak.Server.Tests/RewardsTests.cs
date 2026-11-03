using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using OpenPak.Photon;
using OpenPak.Server.Core;
using OpenPak.Server.LoadBalancing;
using Xunit;

namespace OpenPak.Server.Tests;

public class RewardsTests
{
    static readonly DateTime T0 = new(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);
    static ClockRoom Room(string id, params string[] subjects) => new(id, "game_sessions.outbound", subjects);

    [Fact]
    public void Parse_maps_appids_case_insensitively_to_metrics()
    {
        var map = PhotonRewards.Parse("AAA-1=Outbound, bbb-2=moving-out-2 ,junk,=x,y=");
        Assert.Equal(2, map.Count);
        Assert.Equal("game_sessions.outbound", map["aaa-1"]);
        Assert.Equal("game_sessions.moving-out-2", map["BBB-2"]);
        Assert.Empty(PhotonRewards.Parse(null));
    }

    [Fact]
    public void SessionMin_default_and_override()
    {
        Assert.Equal(TimeSpan.FromMinutes(3), PhotonRewards.SessionMin(null));
        Assert.Equal(TimeSpan.FromSeconds(90), PhotonRewards.SessionMin("90s"));
        Assert.Equal(TimeSpan.FromMinutes(5), PhotonRewards.SessionMin("5m"));
        Assert.Equal(TimeSpan.FromMinutes(3), PhotonRewards.SessionMin("nonsense"));
    }

    [Fact]
    public void Alone_never_counts_and_two_players_count_after_three_minutes_once()
    {
        var c = new SessionClock(TimeSpan.FromMinutes(3));
        Assert.Empty(c.Tick(T0, new[] { Room("r@1", "a") }));
        Assert.Empty(c.Tick(T0.AddMinutes(10), new[] { Room("r@1", "a") }));
        Assert.Empty(c.Tick(T0.AddMinutes(11), new[] { Room("r@1", "a", "b") }));
        Assert.Empty(c.Tick(T0.AddMinutes(13), new[] { Room("r@1", "a", "b") }));
        var ev = c.Tick(T0.AddMinutes(14), new[] { Room("r@1", "a", "b") });
        Assert.Equal(2, ev.Count);
        Assert.Contains(ev, e => e.EventId == "game_sessions.outbound:r@1:switch:a");
        Assert.Empty(c.Tick(T0.AddMinutes(20), new[] { Room("r@1", "a", "b") }));
    }

    [Fact]
    public void Leaving_resets_the_clock_and_a_return_does_not_double_count()
    {
        var c = new SessionClock(TimeSpan.FromMinutes(3));
        c.Tick(T0, new[] { Room("r", "a", "b") });
        c.Tick(T0.AddMinutes(2), new[] { Room("r", "a", "b") });
        c.Tick(T0.AddMinutes(2.5), new[] { Room("r", "a") });                  // b left: resets both
        Assert.Empty(c.Tick(T0.AddMinutes(4), new[] { Room("r", "a", "b") })); // clock restarts
        Assert.Empty(c.Tick(T0.AddMinutes(6), new[] { Room("r", "a", "b") }));
        Assert.Equal(2, c.Tick(T0.AddMinutes(7), new[] { Room("r", "a", "b") }).Count);
        c.Tick(T0.AddMinutes(8), new[] { Room("r", "a") });
        Assert.Empty(c.Tick(T0.AddMinutes(20), new[] { Room("r", "a", "b") }));
        Assert.Empty(c.Tick(T0.AddMinutes(30), new[] { Room("r", "a", "b") }));
    }

    [Fact]
    public void Unmapped_players_do_not_count_as_the_other_player()
    {
        var c = new SessionClock(TimeSpan.Zero);
        Assert.Empty(c.Tick(T0, new[] { Room("r", "a", "") }));
        Assert.Empty(c.Tick(T0.AddMinutes(9), new[] { Room("r", "a", "") }));
    }

    static PeerConnection Peer(string userId, string appId) =>
        new("t", new IPEndPoint(IPAddress.Loopback, 0), new NullHandler(), (_, _) => { }) { SteamId = userId, AppId = appId };

    [Fact]
    public async Task Watcher_resolves_accounts_skips_unmapped_apps_and_unknown_identities()
    {
        var reg = new RoomRegistry();
        var ids = new AccountIdentityRegistry();
        var ua = ids.Register("bsdid:a"); var ub = ids.Register("bsdid:b"); var uz = ids.Register("bsdid:unknown");
        var key = RoomRegistry.KeyFor("app-1", "1", "room");
        var room = reg.GetOrCreate(key);
        var sess = reg.Session(key);
        sess.Join(1, Peer(ua, "app-1")); sess.Join(2, Peer(ub, "app-1")); sess.Join(3, Peer(uz, "app-1"));
        var other = RoomRegistry.KeyFor("app-9", "1", "other");   // an AppId with no metric
        reg.GetOrCreate(other);
        reg.Session(other).Join(1, Peer(ua, "app-9")); reg.Session(other).Join(2, Peer(ub, "app-9"));

        var now = T0;
        var sent = new List<SessionEvent>();
        var w = new RewardsWatcher(reg, ids, PhotonRewards.Parse("app-1=outbound"), TimeSpan.FromMinutes(3),
            id => Task.FromResult<string?>(id switch { "bsdid:a" => "baas-a", "bsdid:b" => "baas-b", _ => null }),
            sent.Add, () => now);
        await w.TickAsync();
        now = T0.AddMinutes(3.5);
        await w.TickAsync();
        Assert.Equal(2, sent.Count);
        Assert.All(sent, e => Assert.StartsWith("game_sessions.outbound:room@", e.EventId));
        Assert.Equal(new[] { "baas-a", "baas-b" }, sent.Select(e => e.Subject).OrderBy(s => s).ToArray());
        now = T0.AddMinutes(9);
        await w.TickAsync();
        Assert.Equal(2, sent.Count);
    }

    sealed class NullHandler : IOperationHandler
    {
        public void OnConnect(PeerConnection peer) { }
        public void OnOperationRequest(PeerConnection peer, OperationRequest request) { }
        public void OnDisconnect(PeerConnection peer) { }
    }
}
