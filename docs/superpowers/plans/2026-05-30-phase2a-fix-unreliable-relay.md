# Phase 2a fix — Relay the unreliable (movement/weapon) stream

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development / executing-plans. Steps use `- [ ]`.

**Goal:** Relay Photon CT_SENDUNRELIABLE (type 7) commands — the ~10 Hz position + weapon-model serialize stream (event 201) — between clients, so a remote player's avatar moves and shows its weapon. Today these are dropped at the transport layer.

**Root cause (confirmed live + via decompiled Photon3Unity3D):** the client sends thousands of `SendUnreliable` commands; `EnetPeer.HandleCommand` has no case for type 7, so their payloads never reach the relay. Additionally `NCommand.Parse` assumes a 12-byte header for all types, but type 7 has a **16-byte header** (an extra int32 `unreliableSequenceNumber` at offset 12, big-endian, included in CommandLength; payload starts at offset 16). Outbound, the client **discards** any unreliable command whose per-channel `unreliableSequenceNumber` is not strictly greater than the last seen (stale/duplicate), and only delivers it once its stamped `reliableSequenceNumber <= incomingReliableSequenceNumber`. So the server must (a) parse type 7 correctly, (b) surface its payload, (c) re-send unreliably with a per-peer/per-channel monotonically increasing unreliable seq, stamping the channel's current reliable seq. Unreliable commands are NOT acked.

**Architecture:** Carry the inbound delivery class (reliable vs unreliable) through the decode→relay→send path so the relay forwards a movement event with the same delivery semantics it arrived with. Transport gains correct type-7 parse/serialize + a per-channel outgoing unreliable counter.

**Tech Stack:** C#/.NET 8, xUnit, oracle = real Photon3Unity3D.dll.

---

## Task 1: NCommand parses & serializes type 7 (and 11) correctly

**Files:**
- Modify: `server/BlackIce.Photon/Transport/NCommand.cs`
- Test: `server/BlackIce.Photon.Tests/TransportTests.cs` (add)

- [ ] **Step 1 — failing tests** (append inside the existing `TransportTests` class in `server/BlackIce.Photon.Tests/TransportTests.cs`):

```csharp
    [Fact]
    public void Unreliable_command_roundtrips_with_its_own_sequence_field()
    {
        // type 7 has a 16-byte header: the extra int32 unreliableSeq at offset 12 (BE), payload after.
        var c = new NCommand(NCommand.SendUnreliable, ChannelId: 0, Flags: 0, ReservedByte: 4,
                             ReliableSequenceNumber: 51, Payload: new byte[] { 0xF3, 0x04, 1, 2, 3 })
                { UnreliableSequenceNumber = 1234 };
        var bytes = c.ToBytes();
        var parsed = NCommand.Parse(bytes, out int consumed);
        Assert.Equal(bytes.Length, consumed);
        Assert.Equal(NCommand.SendUnreliable, parsed.CommandType);
        Assert.Equal(51, parsed.ReliableSequenceNumber);
        Assert.Equal(1234, parsed.UnreliableSequenceNumber);
        Assert.Equal(new byte[] { 0xF3, 0x04, 1, 2, 3 }, parsed.Payload);
    }

    [Fact]
    public void Unreliable_header_is_16_bytes_so_payload_is_not_corrupted()
    {
        // A reliable command with the SAME payload must yield identical payload bytes — proving the
        // extra 4-byte unreliable-seq is consumed as header, not as the first 4 payload bytes.
        var payload = new byte[] { 0xF3, 0x04, 9, 9, 9, 9 };
        var unrel = new NCommand(NCommand.SendUnreliable, 0, 0, 4, 50, payload) { UnreliableSequenceNumber = 7 };
        var parsed = NCommand.Parse(unrel.ToBytes(), out _);
        Assert.Equal(payload, parsed.Payload);
    }

    [Fact]
    public void Reliable_command_still_uses_a_12_byte_header()
    {
        var c = new NCommand(NCommand.SendReliable, 0, NCommand.FlagReliable, 4, 5, new byte[] { 1, 2, 3 });
        var parsed = NCommand.Parse(c.ToBytes(), out int consumed);
        Assert.Equal(c.ToBytes().Length, consumed);
        Assert.Equal(new byte[] { 1, 2, 3 }, parsed.Payload);
        Assert.Equal(0, parsed.UnreliableSequenceNumber);   // unused for reliable
    }
```

- [ ] **Step 2 — run, expect FAIL** (UnreliableSequenceNumber missing; type-7 parse wrong):
`dotnet test server/BlackIce.Photon.Tests/BlackIce.Photon.Tests.csproj --filter TransportTests`

- [ ] **Step 3 — implement.** Replace the body of `server/BlackIce.Photon/Transport/NCommand.cs` with:

```csharp
using System.Buffers.Binary;

namespace BlackIce.Photon.Transport;

/// <summary>
/// One eNet command. The common header is 12 bytes (type, channel, flags, reserved, length int32,
/// reliableSeq int32). Unreliable (7) and unsequenced (11) commands carry one extra int32 after that
/// (unreliableSeq / unsequencedGroup), making their header 16 bytes; CommandLength includes it.
/// All multi-byte fields are big-endian. Fragments (8/15) are not handled yet.
/// </summary>
public sealed record NCommand(byte CommandType, byte ChannelId, byte Flags, byte ReservedByte,
                              int ReliableSequenceNumber, byte[] Payload)
{
    public const int ReliableHeaderSize = 12;
    public const int UnreliableHeaderSize = 16;
    public const int HeaderSize = ReliableHeaderSize;   // back-compat alias for reliable callers

    public const byte Acknowledge = 1, Connect = 2, VerifyConnect = 3, Disconnect = 4,
                      Ping = 5, SendReliable = 6, SendUnreliable = 7, SendFragment = 8,
                      SendUnsequenced = 11;

    public const byte FlagReliable = 1, FlagUnsequenced = 2;

    /// <summary>For type 7 the per-channel unreliable sequence; for type 11 the unsequenced group.
    /// Zero/unused for reliable commands.</summary>
    public int UnreliableSequenceNumber { get; init; }

    private static bool HasExtraSeq(byte type) => type is SendUnreliable or SendUnsequenced;

    public byte[] ToBytes()
    {
        int headerSize = HasExtraSeq(CommandType) ? UnreliableHeaderSize : ReliableHeaderSize;
        int total = headerSize + Payload.Length;
        var b = new byte[total];
        b[0] = CommandType; b[1] = ChannelId; b[2] = Flags; b[3] = ReservedByte;
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(4), total);
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(8), ReliableSequenceNumber);
        if (HasExtraSeq(CommandType))
            BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(12), UnreliableSequenceNumber);
        Payload.CopyTo(b.AsSpan(headerSize));
        return b;
    }

    public static NCommand Parse(ReadOnlySpan<byte> b, out int consumed)
    {
        byte type = b[0], channel = b[1], flags = b[2], reserved = b[3];
        int length = BinaryPrimitives.ReadInt32BigEndian(b[4..]);
        int reliableSeq = BinaryPrimitives.ReadInt32BigEndian(b[8..]);
        int headerSize = HasExtraSeq(type) ? UnreliableHeaderSize : ReliableHeaderSize;
        int extraSeq = HasExtraSeq(type) ? BinaryPrimitives.ReadInt32BigEndian(b[12..]) : 0;
        var payload = b[headerSize..length].ToArray();
        consumed = length;
        return new NCommand(type, channel, flags, reserved, reliableSeq, payload) { UnreliableSequenceNumber = extraSeq };
    }
}
```

- [ ] **Step 4 — run, expect PASS** (the 3 new + the existing transport tests):
`dotnet test server/BlackIce.Photon.Tests/BlackIce.Photon.Tests.csproj --filter TransportTests`

- [ ] **Step 5 — verify the UdpListener parse loop still advances correctly.** It uses `offset + NCommand.HeaderSize <= datagram.Length` as a guard (12 is fine as a minimum-size guard since 12 < 16; the per-command `consumed` from Parse drives the real advance). No change needed. Build the solution to confirm: `dotnet build server/BlackIce.Server.sln`.

- [ ] **Step 6 — commit:**
```bash
git add server/BlackIce.Photon/Transport/NCommand.cs server/BlackIce.Photon.Tests/TransportTests.cs
git commit -m "fix(transport): parse/serialize CT_SENDUNRELIABLE (type 7) 16-byte header"
```

---

## Task 2: EnetPeer surfaces inbound unreliable + builds outbound unreliable with a per-channel seq

**Files:**
- Modify: `server/BlackIce.Photon/Transport/EnetPeer.cs`
- Test: `server/BlackIce.Photon.Tests/TransportTests.cs` (add)

**Context:** `HandleCommand(NCommand cmd, int incomingSentTime, out byte[]? appPayload)` returns control commands and surfaces an app payload. Today only `SendReliable` sets `appPayload`. Unreliable must also set it (no ack — only reliable commands are acked, and the existing reliable-ack guard `(cmd.Flags & FlagReliable) != 0` already excludes type 7 since its flags=0, so the ack logic needs no change). `WrapReliable(payload, channel)` exists. Add `WrapUnreliable(payload, channel)` that stamps `++_outgoingUnreliableSeq[channel]` and the channel's current reliable seq (the last value handed out by `NextSeq`, i.e. the current `_outgoingSeq[channel]`, NOT incremented).

- [ ] **Step 1 — failing tests** (append to `TransportTests`):

```csharp
    [Fact]
    public void Inbound_unreliable_command_surfaces_its_payload()
    {
        var peer = new EnetPeer();
        var cmd = new NCommand(NCommand.SendUnreliable, 0, 0, 4, 50, new byte[] { 0xF3, 0x04, 1 }) { UnreliableSequenceNumber = 9 };
        var outgoing = peer.HandleCommand(cmd, incomingSentTime: 0, out var payload);
        Assert.Equal(new byte[] { 0xF3, 0x04, 1 }, payload);
        Assert.DoesNotContain(outgoing, c => c.CommandType == NCommand.Acknowledge);   // unreliable is NOT acked
    }

    [Fact]
    public void WrapUnreliable_stamps_increasing_per_channel_unreliable_seq()
    {
        var peer = new EnetPeer();
        var c1 = peer.WrapUnreliable(new byte[] { 1 }, channel: 0);
        var c2 = peer.WrapUnreliable(new byte[] { 2 }, channel: 0);
        Assert.Equal(NCommand.SendUnreliable, c1.CommandType);
        Assert.Equal((byte)0, c1.Flags);                       // unreliable: flags 0
        Assert.True(c2.UnreliableSequenceNumber > c1.UnreliableSequenceNumber, "per-channel unreliable seq must advance");
    }
```

- [ ] **Step 2 — run, expect FAIL:** `dotnet test server/BlackIce.Photon.Tests/BlackIce.Photon.Tests.csproj --filter TransportTests`

- [ ] **Step 3 — implement.** In `server/BlackIce.Photon/Transport/EnetPeer.cs`:

(a) Add a field next to `_outgoingSeq`:
```csharp
    private readonly Dictionary<byte, int> _outgoingUnreliableSeq = new();
```

(b) In `HandleCommand`'s switch, add a case mirroring SendReliable (no ack — the reliable-ack guard above already skips it):
```csharp
            case NCommand.SendUnreliable:
                appPayload = cmd.Payload;
                break;
```

(c) Add the builder after `WrapReliable`:
```csharp
    /// <summary>
    /// Wraps an application payload as an UNRELIABLE command (type 7) on the given channel. Stamps the
    /// per-channel monotonically increasing unreliable sequence the client requires (else it discards
    /// the packet as stale/duplicate) and the channel's current reliable sequence (the client only
    /// delivers an unreliable command once its reliableSeq has been reached). Not acked.
    /// </summary>
    public NCommand WrapUnreliable(byte[] payload, byte channel = 0)
    {
        _outgoingUnreliableSeq.TryGetValue(channel, out int u);
        u++;
        _outgoingUnreliableSeq[channel] = u;
        _outgoingSeq.TryGetValue(channel, out int reliableSoFar);   // current reliable seq, not incremented
        return new NCommand(NCommand.SendUnreliable, channel, 0, 4, reliableSoFar, payload) { UnreliableSequenceNumber = u };
    }
```

- [ ] **Step 4 — run, expect PASS:** `dotnet test server/BlackIce.Photon.Tests/BlackIce.Photon.Tests.csproj --filter TransportTests`

- [ ] **Step 5 — commit:**
```bash
git add server/BlackIce.Photon/Transport/EnetPeer.cs server/BlackIce.Photon.Tests/TransportTests.cs
git commit -m "feat(transport): surface inbound unreliable payloads + WrapUnreliable with per-channel seq"
```

---

## Task 3: Carry delivery class through decode → relay → send

**Files:**
- Modify: `server/BlackIce.Server.Core/PeerConnection.cs`
- Modify: `server/BlackIce.Server.LoadBalancing/EventContext.cs`
- Modify: `server/BlackIce.Server.LoadBalancing/RoomSession.cs`
- Modify: `server/BlackIce.Server.LoadBalancing/GameServerHandler.cs`
- Test: `server/BlackIce.Server.Tests/UnreliableRelayTests.cs` (create)

**Context:** The relay must forward a movement event UNRELIABLY (the client discards reliably-delivered position spam out of order and it floods the reliable buffer). We thread a `bool unreliable` from the inbound command class to the outbound send.

(1) `PeerConnection.HandlePacket` currently calls `HandleAppPayload(payload)` without the command. We need to know, when dispatching the resulting operation, whether it arrived unreliable. Simplest correct approach: track the delivery class of the payload being handled and expose it to the handler via a property the handler can read during `OnOperationRequest`, OR pass it through. To keep the seam small and explicit, add an overload of `RaiseEvent` that takes a delivery flag, and have the handler tell the session the inbound class.

Concretely:

- [ ] **Step 1 — failing test** `server/BlackIce.Server.Tests/UnreliableRelayTests.cs`:

```csharp
using System.Collections.Generic;
using System.Net;
using BlackIce.Photon;
using BlackIce.Photon.Transport;
using BlackIce.Server.Core;
using BlackIce.Server.LoadBalancing;
using Xunit;

namespace BlackIce.Server.Tests;

public class UnreliableRelayTests
{
    private static PeerConnection Peer(out List<(EventData ev, bool unreliable)> raised)
    {
        var captured = new List<(EventData, bool)>();
        raised = captured;
        var p = new PeerConnection("test", new IPEndPoint(IPAddress.Loopback, 0), new Null(), (_, _) => { });
        p.OnRaisedClassified = (ev, unrel) => captured.Add((ev, unrel));
        return p;
    }
    private sealed class Null : IOperationHandler
    {
        public void OnConnect(PeerConnection peer) { }
        public void OnOperationRequest(PeerConnection peer, OperationRequest request) { }
        public void OnDisconnect(PeerConnection peer) { }
    }

    [Fact]
    public void Unreliable_event_is_relayed_unreliably_to_others()
    {
        var session = new RoomSession("co-op", new InterceptorChain(new IEventInterceptor[] { new PassthroughInterceptor() }));
        var a = Peer(out _); session.Join(1, a);
        var b = Peer(out var bRaised); session.Join(2, b);

        session.RelayFrom(senderActor: 1, new EventData(201, new() { { 245, "pos" } }), unreliable: true);

        Assert.Single(bRaised);
        Assert.True(bRaised[0].unreliable, "a position event that arrived unreliable must be relayed unreliable");
        Assert.Equal(201, bRaised[0].ev.Code);
    }

    [Fact]
    public void Reliable_event_is_relayed_reliably()
    {
        var session = new RoomSession("co-op", new InterceptorChain(new IEventInterceptor[] { new PassthroughInterceptor() }));
        var a = Peer(out _); session.Join(1, a);
        var b = Peer(out var bRaised); session.Join(2, b);

        session.RelayFrom(1, new EventData(200, new()), unreliable: false);

        Assert.Single(bRaised);
        Assert.False(bRaised[0].unreliable);
    }
}
```

- [ ] **Step 2 — run, expect FAIL** (`OnRaisedClassified`, `RaiseEvent(ev, unreliable)`, `RelayFrom(..., unreliable)` missing).

- [ ] **Step 3 — PeerConnection: add classified raise + send-unreliable path.** In `server/BlackIce.Server.Core/PeerConnection.cs`:

Add near `OnRaised`:
```csharp
    /// <summary>Test/diagnostic hook reporting each raised event AND whether it was sent unreliably.</summary>
    public System.Action<EventData, bool>? OnRaisedClassified { get; set; }
```

Replace `RaiseEvent` with a reliable default that delegates to a classified overload:
```csharp
    public void RaiseEvent(EventData ev) => RaiseEvent(ev, unreliable: false);

    public void RaiseEvent(EventData ev, bool unreliable)
    {
        Log.Info(_role, $"{Remote} -> raise {PhotonNames.Event(ev.Code)} ({(unreliable ? "unreliable" : "reliable")}) [{PhotonNames.Params(ev.Parameters)}]");
        OnRaised?.Invoke(ev);
        OnRaisedClassified?.Invoke(ev, unreliable);
        var msg = WireMessage.EventMessage(ev);
        if (unreliable) _send(new[] { _enet.WrapUnreliable(msg) }, _enet.Challenge);
        else SendRaw(msg);
    }
```
(Leave `SendRaw` as the reliable wrapper used by responses/handshake.)

- [ ] **Step 4 — EventContext: remember the delivery class.** In `server/BlackIce.Server.LoadBalancing/EventContext.cs`, add an `Unreliable` flag:
```csharp
    public bool Unreliable { get; }

    public EventContext(string roomName, int senderActor, EventData ev, bool unreliable = false)
    {
        RoomName = roomName; SenderActor = senderActor; Event = ev; Unreliable = unreliable;
    }
```
(Keep the existing 3-arg usage working via the default.)

- [ ] **Step 5 — RoomSession: relay preserving class.** In `server/BlackIce.Server.LoadBalancing/RoomSession.cs`, change `RelayFrom` to take the flag and pass it to the classified raise:
```csharp
    public void RelayFrom(int senderActor, EventData ev, bool unreliable = false)
    {
        var verdict = _chain.Run(new EventContext(RoomName, senderActor, ev, unreliable));
        if (verdict.Action == RelayAction.Drop) return;

        List<PeerConnection> recipients;
        lock (_gate)
        {
            recipients = new List<PeerConnection>(_members.Count);
            foreach (var (actor, peer) in _members)
                if (actor != senderActor) recipients.Add(peer);
        }

        foreach (var peer in recipients)
        {
            if (verdict.Event is not null) peer.RaiseEvent(verdict.Event, unreliable);
            foreach (var extra in verdict.Originated) peer.RaiseEvent(extra, unreliable);
        }
    }
```

- [ ] **Step 6 — run the new test, expect PASS:** `dotnet test server/BlackIce.Server.Tests/BlackIce.Server.Tests.csproj --filter UnreliableRelayTests`

- [ ] **Step 7 — commit:**
```bash
git add server/BlackIce.Server.Core/PeerConnection.cs server/BlackIce.Server.LoadBalancing/EventContext.cs server/BlackIce.Server.LoadBalancing/RoomSession.cs server/BlackIce.Server.Tests/UnreliableRelayTests.cs
git commit -m "feat(lb): relay events preserving their reliable/unreliable delivery class"
```

---

## Task 4: GameServerHandler tells the relay the inbound delivery class

**Files:**
- Modify: `server/BlackIce.Server.Core/PeerConnection.cs` (expose inbound class to handler)
- Modify: `server/BlackIce.Server.LoadBalancing/GameServerHandler.cs`
- Test: `server/BlackIce.Server.Tests/GameServerRelayTests.cs` (add)

**Context:** The handler's `OnOperationRequest` relays gameplay events but doesn't know if the op arrived unreliable. `PeerConnection` knows (it parsed the command). Expose the current inbound delivery class on the peer so the handler reads it when relaying.

- [ ] **Step 1 — failing test** (append to `GameServerRelayTests`; uses a helper to drive an unreliable op):

```csharp
    [Fact]
    public void Unreliable_gameplay_event_is_relayed_unreliably()
    {
        var (h, _, db) = NewHandler();
        using (db)
        {
            var a = Peer(out _); var b = PeerClassified(out var bRaised);
            h.OnOperationRequest(a, Join());
            h.OnOperationRequest(b, Join());
            // Mark the next inbound op on 'a' as unreliable, as the listener would for a type-7 command.
            a.CurrentInboundUnreliable = true;
            bRaised.Clear();

            h.OnOperationRequest(a, new OperationRequest(253, new()
            {
                { 244, (byte)201 },                                  // position stream event code
                { 245, new Dictionary<object, object> { { (byte)0, 2001 } } },
            }));

            Assert.Single(bRaised);
            Assert.True(bRaised[0].unreliable);
        }
    }

    private static PeerConnection PeerClassified(out List<(EventData ev, bool unreliable)> raised)
    {
        var captured = new List<(EventData, bool)>();
        raised = captured;
        var p = new PeerConnection("GameServer", new IPEndPoint(IPAddress.Loopback, 0), new Null(), (_, _) => { });
        p.OnRaisedClassified = (ev, unrel) => captured.Add((ev, unrel));
        return p;
    }
```

- [ ] **Step 2 — run, expect FAIL** (`CurrentInboundUnreliable` missing; relay currently always reliable).

- [ ] **Step 3 — PeerConnection: expose + set inbound class.** In `PeerConnection.cs`:

Add the property:
```csharp
    /// <summary>Delivery class of the operation currently being dispatched to the handler — set by the
    /// transport as it unwraps each command so the handler can relay an event with matching semantics.</summary>
    public bool CurrentInboundUnreliable { get; set; }
```

In `HandlePacket`, the loop calls `HandleCommand(cmd, ...)` then `HandleAppPayload(payload)`. Set the flag from the command's type before handling its payload. Change the loop body so that, right before `if (payload is not null) HandleAppPayload(payload);`, you set:
```csharp
            if (payload is not null)
            {
                CurrentInboundUnreliable = cmd.CommandType == NCommand.SendUnreliable;
                HandleAppPayload(payload);
                CurrentInboundUnreliable = false;
            }
```
(Remove the old bare `if (payload is not null) HandleAppPayload(payload);` line.)

- [ ] **Step 4 — GameServerHandler: relay with the inbound class.** In the `OpRaiseEvent` case, the relay line currently is:
```csharp
                    _registry.Session(state.RoomName).RelayFrom(state.Actor, new EventData(ec, new() { { PData, data } }));
```
Replace with:
```csharp
                    _registry.Session(state.RoomName).RelayFrom(state.Actor, new EventData(ec, new() { { PData, data } }), peer.CurrentInboundUnreliable);
```

- [ ] **Step 5 — run the test, expect PASS:** `dotnet test server/BlackIce.Server.Tests/BlackIce.Server.Tests.csproj --filter GameServerRelayTests`

- [ ] **Step 6 — full solution, expect green:** `dotnet test server/BlackIce.Server.sln`

- [ ] **Step 7 — commit:**
```bash
git add server/BlackIce.Server.Core/PeerConnection.cs server/BlackIce.Server.LoadBalancing/GameServerHandler.cs server/BlackIce.Server.Tests/GameServerRelayTests.cs
git commit -m "feat(lb): relay gameplay events with the delivery class they arrived on (movement = unreliable)"
```

---

## Task 5: Interop review + live re-test

- [ ] **Step 1 — dispatch photon-interop-reviewer** on the NCommand type-7 framing and WrapUnreliable, to confirm against the real DLL that (a) our type-7 serialization is byte-accurate (16-byte header, BE seq at offset 12, length includes it) and (b) the unreliable-seq/reliable-seq stamping matches what the client's receive path accepts (not discarded as stale). Fix any findings, re-review.

- [ ] **Step 2 — full suite + clean build:** `dotnet test server/BlackIce.Server.sln` ; `dotnet build server/BlackIce.Server.sln`.

- [ ] **Step 3 — LIVE re-test (manual):** restart server, connect two clients. Expected now: the remote player's avatar **moves** as they move, and shows its **weapon model**. The `--trace` log should show `raise EvPunRpc`/position events going out as `(unreliable)` and inbound type-7 commands surfacing payloads. Update `.remember/remember.md` with the result.

---

## Self-review

- Inbound type-7 drop → Tasks 1+2 (parse 16-byte header, surface payload). ✓
- Payload corruption (extra seq) → Task 1 (payload from offset 16). ✓
- Outbound stale-discard → Task 2 (per-channel increasing unreliable seq + current reliable seq stamp). ✓
- Relay must preserve unreliable class → Tasks 3+4. ✓
- Wire-accuracy vs real client → Task 5 interop review + live. ✓
- Placeholders: none; every step has full code. ✓
- Type consistency: `NCommand.UnreliableSequenceNumber`/`SendUnsequenced`/`WrapUnreliable`; `EventContext(...,bool unreliable=false)`; `RoomSession.RelayFrom(int,EventData,bool=false)`; `PeerConnection.RaiseEvent(EventData,bool)` + `OnRaisedClassified` + `CurrentInboundUnreliable` — consistent across tasks. ✓
