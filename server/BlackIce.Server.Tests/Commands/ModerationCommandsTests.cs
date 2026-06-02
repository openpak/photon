using BlackIce.Photon;
using BlackIce.Server.Data;
using BlackIce.Server.LoadBalancing;
using BlackIce.Server.LoadBalancing.Plugins;
using Xunit;

namespace BlackIce.Server.Tests.Commands;

/// <summary>
/// Covers the relay-level moderation slice end to end: the console commands (driven through a
/// <see cref="CommandRegistry"/> exactly as the live console drives them) flip the shared
/// <see cref="ModerationState"/>, and the <see cref="ModerationInterceptor"/> enforces it — a frozen
/// actor's position stream (201) and a muted actor's RPCs (200) are dropped, everything else forwarded.
/// </summary>
public class ModerationCommandsTests
{
    private static (CommandRegistry reg, ModerationState state) Setup()
    {
        var rooms = new RoomRegistry();
        rooms.GetOrCreate("co-op");                 // so ResolveName finds the realm
        var state = new ModerationState();
        var reg = new CommandRegistry().Register(new ModerationCommands(rooms, state));
        return (reg, state);
    }

    [Fact]
    public void Freeze_command_marks_the_actor_frozen_in_shared_state()
    {
        var (reg, state) = Setup();

        Assert.True(reg.TryExecute("freeze co-op 2", PlayerLevel.Console, out var o));
        Assert.Contains("froze", o);
        Assert.True(state.IsFrozen("co-op", 2));
    }

    [Fact]
    public void Mute_and_unmute_round_trip_through_the_console()
    {
        var (reg, state) = Setup();

        reg.TryExecute("mute co-op 3", PlayerLevel.Console, out _);
        Assert.True(state.IsMuted("co-op", 3));

        reg.TryExecute("unmute co-op 3", PlayerLevel.Console, out _);
        Assert.False(state.IsMuted("co-op", 3));
    }

    [Fact]
    public void Modlist_reports_muted_and_frozen_actors()
    {
        var (reg, state) = Setup();
        state.Mute("co-op", 5);
        state.Freeze("co-op", 7);

        Assert.True(reg.TryExecute("modlist co-op", PlayerLevel.Console, out var o));
        Assert.Contains("muted=[5]", o);
        Assert.Contains("frozen=[7]", o);
    }

    [Fact]
    public void Frozen_actor_position_stream_is_dropped()
    {
        var state = new ModerationState();
        state.Freeze("co-op", 2);
        var i = new ModerationInterceptor(state);

        var v = i.Intercept(new EventContext("co-op", 2, new EventData(PhotonCodes.PunEvent.SendSerialize, new())));
        Assert.Equal(RelayAction.Drop, v.Action);
    }

    [Fact]
    public void Non_frozen_actor_position_stream_is_forwarded()
    {
        var state = new ModerationState();
        state.Freeze("co-op", 2);                   // a different actor is frozen
        var i = new ModerationInterceptor(state);

        var v = i.Intercept(new EventContext("co-op", 9, new EventData(PhotonCodes.PunEvent.SendSerialize, new())));
        Assert.Equal(RelayAction.Forward, v.Action);
    }

    [Fact]
    public void Muted_actor_rpc_is_dropped()
    {
        var state = new ModerationState();
        state.Mute("co-op", 4);
        var i = new ModerationInterceptor(state);

        var v = i.Intercept(new EventContext("co-op", 4, new EventData(PhotonCodes.PunEvent.Rpc, new())));
        Assert.Equal(RelayAction.Drop, v.Action);
    }

    [Fact]
    public void Muted_actor_non_rpc_event_is_forwarded()
    {
        // Mute only silences RPCs (200): a muted actor's position stream (201) still relays.
        var state = new ModerationState();
        state.Mute("co-op", 4);
        var i = new ModerationInterceptor(state);

        var v = i.Intercept(new EventContext("co-op", 4, new EventData(PhotonCodes.PunEvent.SendSerialize, new())));
        Assert.Equal(RelayAction.Forward, v.Action);
    }
}
