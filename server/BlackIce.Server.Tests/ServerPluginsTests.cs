using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using BlackIce.Photon;
using BlackIce.Server.Core;
using BlackIce.Server.Data;
using BlackIce.Server.LoadBalancing;
using BlackIce.Server.LoadBalancing.Plugins;
using Xunit;

namespace BlackIce.Server.Tests;

/// <summary>Covers the server-only gameplay plugins (mutators / spawn protection / thorns / killfeed), the
/// cumulative interceptor composition in <see cref="PluginManager.Evaluate"/>, and the console-command
/// register/unregister wiring that backs runtime plugin load/unload.</summary>
public class ServerPluginsTests
{
    private static EventData Dmg(int targetActor, float damage) =>
        DamageData.BuildTakeDamageRpc(targetActor * 1000 + 1, damage);

    private static EventContext Ctx(int sender, EventData ev) => new("r", sender, ev);

    private static PeerConnection Peer()
    {
        var p = new PeerConnection("test", new IPEndPoint(IPAddress.Loopback, 0), new NullHandler(), (_, _) => { });
        p.OnRaised = _ => { };
        return p;
    }

    private static PeerConnection Peer(out List<EventData> raised)
    {
        var captured = new List<EventData>();
        raised = captured;
        var p = new PeerConnection("test", new IPEndPoint(IPAddress.Loopback, 0), new NullHandler(), (_, _) => { });
        p.OnRaised = captured.Add;
        return p;
    }

    private sealed class ServicesWith : IServiceProvider
    {
        private readonly Dictionary<Type, object> _map = new();
        public ServicesWith(params object[] services) { foreach (var s in services) _map[s.GetType()] = s; }
        public object? GetService(Type t) => _map.TryGetValue(t, out var s) ? s : null;
    }

    private sealed class NullHandler : IOperationHandler
    {
        public void OnConnect(PeerConnection peer) { }
        public void OnOperationRequest(PeerConnection peer, OperationRequest request) { }
        public void OnDisconnect(PeerConnection peer) { }
    }

    // --- DamageData round-trip ------------------------------------------------------------------

    [Fact]
    public void TryRewriteDamage_scales_value_and_forces_crit_bit()
    {
        var ev = Dmg(2, 50f);
        Assert.True(DamageData.TryRewriteDamage(ev, d => d * 2f, forceCrit: true));

        var info = PunRpcInfo.From(ev)!.Value;
        Assert.Equal(100f, info.DamageValue);
        Assert.True((info.DamagePacket![DamageData.CombinedFlagsOffset] & DamageData.CritBit) != 0);
    }

    // --- mutators (Rewrite) ---------------------------------------------------------------------

    [Fact]
    public void Mutators_are_inert_until_tuned_then_rewrite_damage()
    {
        var state = new MutatorState();
        var i = new MutatorInterceptor(state);
        Assert.Equal(RelayAction.Forward, i.Intercept(Ctx(1, Dmg(2, 50f))).Action);

        state.DamageMultiplier = 3f;
        var v = i.Intercept(Ctx(1, Dmg(2, 50f)));
        Assert.Equal(RelayAction.Rewrite, v.Action);
        Assert.Equal(150f, PunRpcInfo.From(v.Event!)!.Value.DamageValue);
    }

    // --- spawn protection (Drop) ----------------------------------------------------------------

    [Fact]
    public void SpawnGuard_drops_damage_to_a_freshly_joined_player_only()
    {
        var state = new SpawnGuardState();
        var i = new SpawnGuardInterceptor(state);

        // disarmed -> nothing dropped
        state.Joined("r", 2);
        Assert.Equal(RelayAction.Forward, i.Intercept(Ctx(1, Dmg(2, 20f))).Action);

        state.Seconds = 30;
        Assert.Equal(RelayAction.Drop, i.Intercept(Ctx(1, Dmg(2, 20f))).Action);       // actor 2 just joined
        Assert.Equal(RelayAction.Forward, i.Intercept(Ctx(1, Dmg(3, 20f))).Action);     // actor 3 never joined
        Assert.Equal(RelayAction.Forward, i.Intercept(Ctx(2, Dmg(2, 20f))).Action);     // self-damage ignored
    }

    // --- thorns (Originate) ---------------------------------------------------------------------

    [Fact]
    public void Thorns_reflects_a_percentage_back_at_the_attacker()
    {
        var reg = new RoomRegistry();
        var session = reg.Session("r");
        session.Join(1, Peer());
        session.Join(2, Peer());

        var state = new ThornsState();
        var i = new ThornsInterceptor(state, reg);
        Assert.Equal(RelayAction.Forward, i.Intercept(Ctx(1, Dmg(2, 40f))).Action);     // off

        state.Percent = 50;
        var v = i.Intercept(Ctx(1, Dmg(2, 40f)));                                        // actor 1 hits actor 2
        Assert.Equal(RelayAction.Originate, v.Action);
        var reflect = PunRpcInfo.From(Assert.Single(v.Originated))!.Value;
        Assert.Equal(1 * 1000 + 1, reflect.ViewId);                                      // aimed at attacker's avatar view
        Assert.Equal(20f, reflect.DamageValue);                                          // 50% of 40

        // damage to a non-member (world/enemy view) is never reflected
        Assert.Equal(RelayAction.Forward, i.Intercept(Ctx(1, Dmg(9, 40f))).Action);
    }

    // Killfeed unit coverage now lives in KillfeedDeathTests (the HP-summing model was retired for a
    // real-death detector — see the 2026-06-02 arena-down-and-respawn plan, Task 6).

    // --- full stack: command -> plugin manager -> relay -> reflected hit delivered to attacker ---

    [Fact]
    public void Thorns_armed_via_console_reflects_through_the_real_relay_to_the_attacker()
    {
        // Wire the relay to the plugin manager exactly as the host does, with the same RoomRegistry the
        // thorns plugin resolves (so its room-membership check sees the live session).
        var mgr = new PluginManager();
        mgr.Add(new ThornsPlugin(), enabled: true);
        var reg = new RoomRegistry(mgr.Evaluate);
        mgr.ConfigureAll(new ServicesWith(reg));

        // Arm it through its actual console command.
        var console = new CommandRegistry();
        foreach (var provider in mgr.CommandProviders) console.Register(provider);
        Assert.True(console.TryExecute("thorns percent 50", PlayerLevel.Console, out _));

        var session = reg.Session("r");
        var attacker = Peer(out var attackerRaised); session.Join(1, attacker);
        var victim = Peer(out var victimRaised); session.Join(2, victim);

        session.RelayFrom(senderActor: 1, Dmg(2, 40f));   // actor 1 hits actor 2 for 40

        // The victim receives the original 40 damage on its own avatar view...
        Assert.Contains(victimRaised, e =>
            PunRpcInfo.From(e) is { ViewId: 2 * 1000 + 1, DamageValue: 40f });
        // ...and the attacker receives the server-originated reflected 20 (50%) on ITS avatar view.
        Assert.Contains(attackerRaised, e =>
            PunRpcInfo.From(e) is { ViewId: 1 * 1000 + 1, DamageValue: 20f });
    }

    // --- arena: team scoring -> score cap -> win -> reset ---------------------------------------

    private static GameModeRegistry TwoTeams(out int team1, out int team2)
    {
        var modes = new GameModeRegistry();
        modes.SetMode("r", GameMode.TeamVsTeam);
        team1 = modes.AssignTeam("r", 1);   // team 0
        team2 = modes.AssignTeam("r", 2);   // team 1
        return modes;
    }

    [Fact]
    public void Arena_scores_cross_team_kills_and_resets_after_a_win()
    {
        var modes = TwoTeams(out int t1, out _);
        var state = new ArenaState { Enabled = true, ScoreCap = 2 };
        var match = new ArenaMatch(state, modes, rooms: null, bus: new KillBus());

        match.OnKill(new KillNotice("r", Killer: 1, Victim: 2, KillerStreak: 1));
        Assert.Equal(1, state.Score("r", t1));

        match.OnKill(new KillNotice("r", 1, 2, 2));   // reaches the cap -> win -> reset (ResetOnWin default true)
        Assert.Equal(0, state.Score("r", t1));        // scores cleared for the next round
        Assert.False(state.Ended("r"));               // reset, not left in an ended state
    }

    [Fact]
    public void Arena_ignores_same_team_kills_and_can_stay_ended_without_reset()
    {
        var modes = TwoTeams(out int t1, out _);
        int t3 = modes.AssignTeam("r", 3);            // balances onto team 0 (same as actor 1)
        Assert.Equal(t1, t3);

        var state = new ArenaState { Enabled = true, ScoreCap = 1, ResetOnWin = false };
        var match = new ArenaMatch(state, modes, rooms: null, bus: null);

        match.OnKill(new KillNotice("r", Killer: 1, Victim: 3, KillerStreak: 1));   // same team -> no score
        Assert.Equal(0, state.Score("r", t1));

        match.OnKill(new KillNotice("r", 1, 2, 1));   // cross-team, cap 1 -> win, no reset
        Assert.True(state.Ended("r"));
        match.OnKill(new KillNotice("r", 1, 2, 2));   // ignored while ended
        Assert.Equal(1, state.Score("r", t1));
    }

    // The killfeed -> arena -> win -> reset integration ran on the retired HP-summing model and
    // arming it via "killfeed hp"; arena now scores on real deaths (KilledPlayerRemote), so that
    // end-to-end coverage is rebuilt in ArenaMatchTests (2026-06-02 arena-down-and-respawn plan, Task 8).

    // --- cumulative composition in PluginManager.Evaluate ---------------------------------------

    [Fact]
    public void A_rewrite_is_seen_by_a_later_validator_which_can_still_drop()
    {
        var mgr = new PluginManager();
        // Register the validator FIRST and the rewriter SECOND: the plugin Order (rewriter -100 < validator 0),
        // not registration order, must put the rewrite ahead so the validator sees the scaled value and drops.
        mgr.Add(new DropOverPlugin(1000f), enabled: true);       // Order 0, drops > 1000
        mgr.Add(new ScaleDamagePlugin(100f), enabled: true);     // Order -100, 20 -> 2000
        mgr.ConfigureAll(new EmptyServices());

        Assert.Equal(RelayAction.Drop, mgr.Evaluate(Ctx(1, Dmg(2, 20f))).Action);
    }

    [Fact]
    public void A_throwing_interceptor_is_skipped_and_later_interceptors_still_run()
    {
        var mgr = new PluginManager();
        mgr.Add(new ThrowingPlugin(), enabled: true);            // throws on every event
        mgr.Add(new DropOverPlugin(1000f), enabled: true);       // must still get to run
        mgr.ConfigureAll(new EmptyServices());

        Assert.Equal(RelayAction.Drop, mgr.Evaluate(Ctx(1, Dmg(2, 2000f))).Action);
    }

    // --- console-command register / unregister (runtime load/unload backing) -------------------

    [Fact]
    public void Unregister_removes_a_providers_commands()
    {
        var reg = new CommandRegistry();
        var provider = new PingCommands();
        reg.Register(provider);
        Assert.True(reg.TryExecute("ping", PlayerLevel.Console, out var ok) && ok == "pong");

        reg.Unregister(provider);
        Assert.False(reg.TryExecute("ping", PlayerLevel.Console, out _));   // unknown command now
    }

    // --- test doubles ---------------------------------------------------------------------------

    private sealed class EmptyServices : IServiceProvider { public object? GetService(Type t) => null; }

    private sealed class ScaleDamagePlugin : IServerPlugin
    {
        private readonly float _scale;
        public ScaleDamagePlugin(float scale) => _scale = scale;
        public string Name => "scale";
        public string Description => "test: scales damage";
        public int Order => -100;   // run as a rewriter, before validators
        public void Configure(PluginBuilder b) => b.AddInterceptor(() => new Inner(_scale));
        private sealed class Inner : IEventInterceptor
        {
            private readonly float _scale;
            public Inner(float scale) => _scale = scale;
            public RelayVerdict Intercept(EventContext ctx) =>
                DamageData.TryRewriteDamage(ctx.Event, d => d * _scale)
                    ? RelayVerdict.Rewrite(ctx.Event) : RelayVerdict.Forward(ctx.Event);
        }
    }

    private sealed class DropOverPlugin : IServerPlugin
    {
        private readonly float _max;
        public DropOverPlugin(float max) => _max = max;
        public string Name => "dropover";
        public string Description => "test: drops big hits";
        public void Configure(PluginBuilder b) => b.AddInterceptor(() => new Inner(_max));
        private sealed class Inner : IEventInterceptor
        {
            private readonly float _max;
            public Inner(float max) => _max = max;
            public RelayVerdict Intercept(EventContext ctx) =>
                PunRpcInfo.From(ctx.Event)?.DamageValue > _max ? RelayVerdict.Drop() : RelayVerdict.Forward(ctx.Event);
        }
    }

    private sealed class ThrowingPlugin : IServerPlugin
    {
        public string Name => "throws";
        public string Description => "test: throws";
        public int Order => -100;   // run first so the later validator still has to be reached
        public void Configure(PluginBuilder b) => b.AddInterceptor(() => new Inner());
        private sealed class Inner : IEventInterceptor
        {
            public RelayVerdict Intercept(EventContext ctx) => throw new InvalidOperationException("boom");
        }
    }

    private sealed class PingCommands
    {
        [ConsoleCommand("ping", MinLevel = PlayerLevel.Console)]
        private string Ping(CommandLine line) => "pong";
    }
}
