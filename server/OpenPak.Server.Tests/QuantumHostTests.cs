using OpenPak.Photon;
using OpenPak.Server.LoadBalancing.Quantum;
using Xunit;

namespace OpenPak.Server.Tests;

/// <summary>The Quantum room host against a scripted client speaking the protocol as the client writes it.</summary>
public class QuantumHostTests
{
    private readonly List<(int Actor, byte Code, byte[] Payload, bool Reliable)> _sent = new();
    private QuantumHost Host() => new((room, actor, code, payload, reliable) => _sent.Add((actor, code, payload, reliable))) { HardTolerance = 2 };

    private static byte[] Join(int slots = 1, int players = 4)
    {
        var w = new QuantumBitWriter();
        w.WriteByte(QuantumProtocol.Join);
        w.WriteString("client-id");
        w.WriteString("2.2.0.0");
        w.WriteInt(slots);       // PlayerSlots
        w.WriteInt(0);           // InitialTick
        w.WriteInt(players);     // PlayerCount
        w.WriteBool(false);      // Rejoin
        return w.ToArray();
    }

    private static byte[] Configs(int players = 4, int fps = 60)
    {
        var w = new QuantumBitWriter();
        w.WriteByte(QuantumProtocol.SessionConfig);
        w.WriteBool(false);      // Requested
        w.WriteBool(false);      // not null
        w.WriteInt(0);           // BackgroundThreadPriority
        w.WriteInt(players);
        for (var i = 0; i < 5; i++) w.WriteBool(false);
        w.WriteInt(0); w.WriteInt(0); w.WriteInt(0);
        w.WriteInt(fps);
        for (var i = 0; i < 13; i++) w.WriteInt(i);
        w.WriteBool(false); w.WriteBool(false); w.WriteInt(0);
        w.WriteByte(QuantumProtocol.RuntimeConfig);
        w.WriteBool(false);
        w.WriteByteArray(new byte[] { 1, 2, 3 });
        w.WriteByte(QuantumProtocol.StartRequest);
        return w.ToArray();
    }

    private static byte[] Input(int index, int tick, byte[] data)
    {
        var w = new QuantumBitWriter();
        w.WriteByte((byte)index);
        w.WriteInt(tick);
        w.WriteByteArray(data);
        w.WriteBool(false);      // rpc (always absent from the client)
        w.WriteBits(0, 4);       // flags
        return w.ToArray();
    }

    [Fact]
    public void Join_is_confirmed_with_a_slot_and_the_configs_are_requested()
    {
        var host = Host();
        host.Feed("r", 1, QuantumProtocol.ProtocolEvent, Join());
        var (actor, code, payload, reliable) = Assert.Single(_sent);
        Assert.Equal(1, actor); Assert.Equal(QuantumProtocol.ProtocolEvent, code); Assert.True(reliable);
        var r = new QuantumBitReader(payload);
        Assert.Equal(QuantumProtocol.Joined, r.ReadByte());
        Assert.True(r.ReadBool());
        Assert.Equal(QuantumProtocol.JoinConfirmed, r.ReadInt());
        Assert.Equal(new[] { 0 }, r.ReadIntArray());
        Assert.Equal(QuantumProtocol.SessionConfig, r.ReadByte());
        Assert.True(r.ReadBool()); Assert.True(r.ReadBool());
        Assert.Equal(QuantumProtocol.RuntimeConfig, r.ReadByte());
        Assert.True(r.ReadBool()); Assert.Null(r.ReadByteArray());
    }

    [Fact]
    public void A_start_request_starts_the_simulation_and_inputs_come_back_confirmed()
    {
        var host = Host();
        host.Feed("r", 1, QuantumProtocol.ProtocolEvent, Join(players: 2));
        host.Feed("r", 2, QuantumProtocol.ProtocolEvent, Join(players: 2));
        _sent.Clear();
        host.Feed("r", 1, QuantumProtocol.ProtocolEvent, Configs(players: 2));
        var start = Assert.Single(_sent);
        var s = new QuantumBitReader(start.Payload);
        Assert.Equal(QuantumProtocol.SimulationStart, s.ReadByte());
        Assert.False(s.ReadBool());
        s.ReadDouble();
        Assert.Equal(new byte[] { 1, 2, 3 }, s.ReadByteArray());
        Assert.Equal(s.BitPosition + QuantumProtocol.SessionConfigBits, s.BitPosition + QuantumProtocol.SessionConfigBits);
        host.Feed("r", 2, QuantumProtocol.ProtocolEvent, new byte[] { QuantumProtocol.StartRequest });
        _sent.Clear();

        host.Feed("r", 1, QuantumProtocol.InputEvent, Input(0, 1, new byte[] { 0xAA }));
        host.Feed("r", 2, QuantumProtocol.InputEvent, Input(0, 1, new byte[] { 0xBB }));
        Thread.Sleep(60);
        host.Tick();
        var packets = _sent.Where(x => x.Code == QuantumProtocol.InputEvent).ToList();
        Assert.Equal(2, packets.Count);
        var p = new QuantumBitReader(packets[0].Payload);
        p.ReadBits(32); p.ReadDouble(); p.ReadDouble();
        Assert.Equal(2, p.ReadByte());          // PlayerCount
        Assert.False(p.ReadBool());             // no fixed input size
        p.AlignToByte();
        Assert.Equal(1, p.ReadInt());           // first confirmed tick
        Assert.True(p.ReadBool());              // complete
        Assert.Equal(3UL, p.ReadBits(2));       // both players
        Assert.False(p.ReadBool());             // player 0 not empty
        Assert.Equal(new byte[] { 0xAA }, p.ReadByteArray());
        Assert.Null(p.ReadByteArray());
        Assert.Equal(0UL, p.ReadBits(4));
        Assert.False(p.ReadBool());
        Assert.Equal(new byte[] { 0xBB }, p.ReadByteArray());
    }

    [Fact]
    public void A_command_rides_in_the_players_next_confirmed_input()
    {
        var host = Host();
        host.Feed("r", 1, QuantumProtocol.ProtocolEvent, Join(players: 1));
        host.Feed("r", 1, QuantumProtocol.ProtocolEvent, Configs(players: 1));
        var w = new QuantumBitWriter();
        w.WriteByte(QuantumProtocol.Command); w.WriteInt(0); w.WriteByteArray(new byte[] { 7 });
        host.Feed("r", 1, QuantumProtocol.ProtocolEvent, w.ToArray());
        _sent.Clear();
        host.Feed("r", 1, QuantumProtocol.InputEvent, Input(0, 1, new byte[] { 1 }));
        Thread.Sleep(40);
        host.Tick();
        var p = new QuantumBitReader(_sent.First(x => x.Code == QuantumProtocol.InputEvent).Payload);
        p.ReadBits(32); p.ReadDouble(); p.ReadDouble(); p.ReadByte(); p.ReadBool(); p.AlignToByte();
        Assert.Equal(1, p.ReadInt()); p.ReadBool(); p.ReadBits(1);
        Assert.False(p.ReadBool());
        Assert.Equal(new byte[] { 1 }, p.ReadByteArray());
        Assert.Equal(new byte[] { 7 }, p.ReadByteArray());
        Assert.Equal((ulong)QuantumProtocol.FlagCommand, p.ReadBits(4));
    }
}
