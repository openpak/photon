# 07 — Fusion relay transport (Photon event 101)

A clean-room description of Fusion 2's low-level transport (`Fusion.Sockets`: `NetPeer`,
`NetPeerGroup`, `NetConnection`, `NetSocketRelay`), written **from the server's side**: the peer at
actor 0 that accepts connections from Fusion clients over the Photon relay. It ends where a payload
is handed up: "here are N bits that arrived on the notify channel of connection C" (or "here is a
reliable byte slice"). What those bits mean — ticks, snapshots, object words, RPC messages — belongs
to the simulation/state-replication spec, not this one.

Everything here is our own description of observed behaviour. Field and enum names are the
wire-visible names the SDK uses, so logs and captures can be matched against it.

## 0. Provenance and conventions

| | |
|---|---|
| SDK build read | `Fusion.Sockets` / `Fusion.Runtime` / `Fusion.Realtime` **2.0.8.1402** (`+4c396d6f`), configuration **"Unity Debug"** — the DLLs in `server/fusion-libs/`, and the version Outbound reports in its own Join (`FusionProtocolTests`: serialization version 2.0.8) |
| Cross-check | Fusion **1.1.12** (`scratch/fusion/src-1.1.12`): `NetPacketType`, `NetNotifyHeader`, `NetCommandConnect`, `NetCommandAccepted` are byte-for-byte the same, so this spec should also hold for Moving Out 2's 1.1.8 (not re-verified beyond those structs) |
| Working files | `~/REPOS/Openpak/scratch/fusion/src/` (`Fusion.Realtime.decompiled.cs` was added for this doc), `~/REPOS/Openpak/scratch/fusion/transport-spec/bb.py` (the bit-packing model that produced the worked examples in §4) |
| Captures | the Connect / Accepted pair in `server/OpenPak.Photon.Tests/FusionRelayProtocolTests.cs` (real Photon Cloud, Steam client, room JWN7NE) |

Conventions used throughout:

- **Byte order: little-endian** for every multi-byte field. The SDK writes packet headers by
  overlaying fixed-layout structs on the buffer, so "native order" — and every platform Fusion
  ships on (x86-64, ARM64, the Switch) is little-endian. A server must write LE explicitly.
- **Bit order: LSB-first.** In the bit-packed parts, stream bit *k* is bit `k % 8` of byte `k / 8`.
- Offsets are byte offsets from the start of the Fusion datagram (= start of the event-101 payload).
- "Inference" marks a statement not read directly from the code (derived from it, or from a capture).
- `dist16(a, b)` = the signed 16-bit difference `(short)(a - b)`; see §6.1.

## 1. Where this layer sits

```
 Photon UDP / eNet  (doc 05)        reliable/unreliable commands, channels, Photon fragmentation
   └ OpRaiseEvent / EventData        code 101, parameter 245 = byte[]
       └ Fusion datagram (this doc)  first byte = NetPacketType
           └ notify payload bits     simulation layer (separate spec)
```

Event codes in use by Fusion (constants `EventCodes`): **100** = protocol messages (Join, Start,
PlayerRefMapping … — see `docs/fusion-protocol-map.md`), **101** = this transport, **102** = dummy
traffic (logged and discarded by the client). `PhotonConstants`: server actor = **0**, Fusion data
parameter = **245**.

### 1.1 Client → server

The client's relay socket hands each datagram to `OpRaiseEvent` with:

| | value |
|---|---|
| event code | 101 |
| CustomData (parameter 245) | the datagram, as a byte array (sent from a `ByteArraySlice`; GpBinary type "byte[]") |
| TargetActors (parameter 252) | `[0]` — one element, the server's actor number |
| Photon delivery | `reliable == false` → **channel 0, UnreliableUnsequenced**; `reliable == true` → **channel 1, Reliable** |

The `reliable` flag is set by the transport for exactly one thing: the pieces of a fragmented notify
packet (§7). Every other datagram — commands, ordinary notify packets, ack-only packets, reliable-
channel slices — goes **unreliable** on channel 0. The client also lowers Photon's own send window
(`SendWindowSize /= 3`), raises `QuickResendAttempts` to 8 and sets the Photon disconnect timeout to
15 s; those are Photon-layer settings (doc 05), listed here because they shape what arrives.

Datagrams larger than Photon's MTU are split by Photon itself (eNet fragment commands, including the
unsequenced fragment type 15 noted in `docs/fusion-shared-server.md`); the server must reassemble
those *before* this layer sees anything. A Fusion datagram from the client is at most
**1280 bytes** (§7).

### 1.2 Server → client

The server raises event 101 to one actor with parameter 245 = the datagram as a byte array and
parameter 254 (sender actor) = 0. On receipt the client:

1. takes the event's sender; **any value ≤ 0, including "absent" (reads as −1), is treated as 0**;
2. requires CustomData to be a byte array (it arrives as a pooled `ByteArraySlice`) — anything else
   is dropped silently;
3. queues `(sender, bytes)` for its transport, which reads them as if from `NetAddress.FromActorId(sender)`.

Size limit on the client side: the Fusion simulation configures its transport with a **receive
buffer of 8192 bytes**, and an inbound datagram longer than that trips an always-on assertion in
the client. In practice stay far below: the Photon client itself reads UDP into a 1200-byte buffer
(`docs/fusion-protocol-map.md`), so anything that does not fit one Photon datagram must be split
by the Photon layer, which in our server means sending it reliably (`EnetPeer.WrapReliableFragmented`).

Delivery mode the server should use (inference — mirrors what the SDK does on its own side):
ordinary datagrams **unreliable**, the pieces of a fragmented notify packet **reliable** (or at
least sequenced: the receiver needs them consecutive and in order, §7).

### 1.3 No Fusion-level encryption on the relay path

The relay socket's encryption hooks are empty; Fusion's own `DataEncryptor` / encrypted-key prefix
exists only on the direct-UDP socket (`NetSocketNative`) used for ClientServer NAT punch. Over
event 101 the datagram is exactly the Fusion bytes, nothing prepended or stripped. (Photon-level
payload encryption could in principle apply, but Photon Cloud sends 100/101 in clear —
`PeerConnection.RaiseEvent` already does the same.)

## 2. Addressing

`NetAddress` is a 24-byte value: three 64-bit blocks. For an IP endpoint the first 18 bytes hold the
native socket address. For a **relay address** everything is zero except a 32-bit integer at byte
offset 20, which holds **actorNumber + 1**:

```
FromActorId(a):  a must be >= 0;  bytes[20..23] = a + 1 (LE);  all other bytes 0
ActorId        = int at [20] - 1
IsRelayAddr    = ActorId >= 0           (so the all-zero address is NOT a relay address)
```

So actor 0 (the server) is `…00 01 00 00 00`, never the all-zero "no address". Two addresses are
equal iff all 24 bytes are equal. **Addresses never appear on the wire**; they only key the
transport's connection table. Over the relay, a client is identified purely by the Photon actor
number its events arrive from, and the server's own transport address is `FromActorId(0)`.

Server-side consequence: one Fusion connection per (room, actor). If an actor's Photon peer is
replaced (leave + rejoin with the same actor number), the transport sees the same address.

## 3. Datagram framing

Byte 0 of every datagram is the packet type (`NetPacketType`, one byte):

| value | name | sent by SDK? | handled on receive | layout |
|---|---|---|---|---|
| 1 | `Command` | yes | yes | §3.1 — byte 1 is a `NetCommands` value |
| 2 | `UnreliableData` | never by the Fusion simulation | client simulation **asserts / fails** if it gets one | 1-byte header, payload bits from bit 8 |
| 3 | `NotifyData` | yes — every simulation packet | yes | 14-byte notify header (§3.2) + payload bits |
| 4 | `NotifyAcks` | yes — ack-only / keepalive | yes | 14-byte notify header, nothing after |
| 5 | `Unconnected` | only for direct-UDP NAT punch pings | ignored | 1 byte type, then raw bytes from byte 1 |
| 6 | `MtuDiscoveryReq` | never (defined, unused) | logged as invalid | — |
| 7 | `MtuDiscoveryRep` | never (defined, unused) | logged as invalid | — |
| 8 | `NotifyReliableData` | yes — reliable channel | yes | 14-byte notify header + 48-byte `ReliableId` + bytes (§8) |

A datagram from an address the transport has no connection for is **dropped unless it is a
Connect command** (type 1, command 1). Unknown types from a connected peer are dropped.

### 3.1 Commands (type 1)

All commands are fixed-size little-endian structs, and the SDK sends the **whole struct including
alignment padding** (the size column), with unused bytes zero. Receivers do not length-check
commands — the receive buffer is zeroed before each read, so a short command reads zeros for the
missing tail — but a server should send the exact sizes below.

`NetCommands`: **Connect = 1, Accepted = 2, Refused = 3, Disconnect = 4, Ping = 5**.
`Ping` has no struct, is never sent, and an incoming one is logged as "Invalid Command Type" and
ignored. Keepalive is done with `NotifyAcks` instead (§6.4).

#### Connect (client → server), 152 bytes

| offset | size | field | notes |
|---|---|---|---|
| 0 | 1 | PacketType | 1 |
| 1 | 1 | Command | 1 |
| 2 | 2 | — | padding, 0 |
| 4 | 4 | TokenLength | int32; 0..128 (sender clamps; receiver clamps again) |
| 8 | 8 | ConnectionId | the client's own `NetConnectionId` (below) |
| 16 | 128 | TokenData | first `TokenLength` bytes meaningful, rest 0 |
| 144 | 8 | UniqueId | the 8 bytes the server gave in the event-100 Join Confirmation |

`NetConnectionId` (8 bytes): `Group` int16 @0, `GroupIndex` int16 @2, `Generation` uint32 @4.
A connection slot's Generation starts at 1 and is bumped every time the slot is recycled (skipping 0).
A Fusion client has one group and one slot, so its first Connect carries `Group=0, GroupIndex=0,
Generation=1` — exactly what the capture shows.

The token is the game's `StartGameArgs.ConnectionToken` (opaque, ≤ 128 bytes; Outbound sends none).
The UniqueId is opaque to the transport; the server's application layer compares it as a
**little-endian uint64**.

Captured Connect (Outbound → Photon Cloud), abbreviated:

```
01 01 00 00   00 00 00 00   00 00 00 00 01 00 00 00   00 … (128 × 00) …   98 0C E9 BA A7 28 63 7B
type cmd pad  TokenLength=0  Group=0 Idx=0 Gen=1       TokenData           UniqueId
```

#### Accepted (server → client), 32 bytes

| offset | size | field | notes |
|---|---|---|---|
| 0 | 1 | PacketType | 1 |
| 1 | 1 | Command | 2 |
| 2 | 6 | — | padding, 0 |
| 8 | 8 | AcceptedLocalId | the **server's** `NetConnectionId` for this client |
| 16 | 8 | AcceptedRemoteId | echo of the client's `ConnectionId` from its Connect |
| 24 | 4 | Counter | uint32, see §5.3 — **not cosmetic** |
| 28 | 4 | — | padding, 0 (struct is 8-aligned, so 28 → 32) |

Captured Accepted (Photon Cloud → the room's first client):

```
01 02 00 00 00 00 00 00   00 00 00 00 01 00 00 00   00 00 00 00 01 00 00 00   01 00 00 00   00 00 00 00
                          Local: G0 I0 Gen1          Remote: G0 I0 Gen1         Counter=1     pad
```

#### Refused (server → client), 3 bytes

| offset | size | field |
|---|---|---|
| 0 | 1 | PacketType = 1 |
| 1 | 1 | Command = 3 |
| 2 | 1 | Reason (`NetConnectFailedReason`): **Timeout = 1, ServerFull = 2, ServerRefused = 3** |

Example "server full": `01 03 02`. The client maps ServerFull → `ShutdownReason.GameIsFull`,
ServerRefused → `ConnectionRefused`; its own retry exhaustion (§5.1) → `ConnectionTimeout`.

#### Disconnect (either direction), 136 bytes

| offset | size | field | notes |
|---|---|---|---|
| 0 | 1 | PacketType | 1 |
| 1 | 1 | Command | 4 |
| 2 | 1 | Reason | `NetDisconnectReason` (below) |
| 3 | 1 | — | padding |
| 4 | 4 | TokenLength | int32, 0..128 |
| 8 | 128 | TokenData | optional disconnect token |

`NetDisconnectReason`: **Unknown = 1, Timeout = 2, Requested = 3, SequenceOutOfBounds = 4,
SendWindowFull = 5, ByRemote = 6**. (In 2.0.8 nothing actually produces `Unknown` or `ByRemote`:
a received Disconnect is recorded with the *sender's* reason, see §5.4.)

Example, "Requested", no token: `01 04 03 00 00 00 00 00` followed by 128 zero bytes.

### 3.2 The notify header (types 3, 4, 8), 14 bytes

| offset | size | field | notes |
|---|---|---|---|
| 0 | 1 | PacketType | 3, 4 or 8 |
| 1 | 1 | Fragment | 0 = not fragmented; else fragment index 1..40, with **0x80 set on the last** (§7) |
| 2 | 2 | Sequence | uint16, this packet's notify sequence (0 in a type-4 ack-only packet, ignored there) |
| 4 | 2 | AckSequence | uint16, newest notify sequence the sender has received from us |
| 6 | 8 | AckMask | uint64, bit *k* set ⇔ sequence `AckSequence − k` was received (bit 0 = AckSequence itself) |

The header is not bit-packed: it is these 14 bytes, then the payload begins **at byte 14 = bit 112**.

Example — server NotifyData, its own sequence 1, acknowledging client sequences 1..5, one payload
byte `00`:

```
03 00 01 00 05 00 1F 00 00 00 00 00 00 00 | 00
```

Same acknowledgement as an ack-only packet (exactly 14 bytes):

```
04 00 00 00 05 00 1F 00 00 00 00 00 00 00
```

Rules a receiver applies (details in §6):

- type 4 is processed only if the datagram is **≥ 14** bytes;
- types 3 and 8 are processed only if the datagram is **> 14** bytes — a NotifyData with an empty
  payload is dropped *before* its acks are read, so never send one;
- types 2/3/4/8 are processed only once the connection is `Connected`; before that they are dropped.

## 4. Bit-buffer primitives (`NetBitBuffer`)

The payload after the 14-byte notify header is written with `NetBitBuffer`. The transport itself
only uses the byte-aligned operations (header overlay, `WriteBytesAligned` for the `ReliableId`);
the bit-level primitives matter to the simulation layer, and are specified here so both layers
share one definition.

### 4.1 Storage and bit order

The buffer is an array of 64-bit words stored little-endian, filled from bit 0 of word 0 upward.
Because the words are little-endian, this is identical to a byte stream filled **LSB-first**:
stream bit *k* = bit `k mod 8` of byte `⌊k/8⌋`. A field of *n* bits stores its **low bit first**.
Writing never needs a read-modify-write of earlier fields; unused high bits of the final byte are
zero. The datagram length is `ceil(bitsWritten / 8)` bytes — **the bit length is not transmitted**,
so a reader sees up to 7 trailing zero bits it must tolerate (the simulation layer's job).

A value written with *n* bits is masked to its low *n* bits. **Reading never sign-extends**: a
signed value written in fewer than its full width reads back as the unsigned low bits.

### 4.2 Primitive catalogue

| primitive | encoding |
|---|---|
| `Boolean` | 1 bit, 1 = true |
| `Byte(bits = 8)` | `bits` ≤ 8 low bits |
| `Int16/UInt16(bits = 16)` | `bits` ≤ 16 low bits, two's complement, no sign extension on read |
| `Int32/UInt32(bits = 32)` | `bits` ≤ 32 low bits, same |
| `Int64/UInt64(bits = 64)` | `bits` ≤ 64 low bits, same |
| `Single` | the 32 IEEE-754 bits, as a 32-bit field |
| `Double` | the 64 IEEE-754 bits, as a 64-bit field |
| `UInt32VarLength(v)` (no block size) | LEB128-style: 7 value bits per byte, low group first, bit 7 = "more follows"; the 1..5 bytes are written as **one (8·count)-bit field at the current bit position — not byte-aligned** |
| `UInt32VarLength(v, blockSize)` / `UInt64VarLength` | block varint, below |
| `Int32VarLength`, `Int64VarLength` | the signed value reinterpreted as unsigned, then as above. **No zig-zag** — a negative int32 always costs 5 LEB bytes |
| `PadToByteBoundary` | write 0-bits until bit position is a multiple of 8 |
| `WriteBytesAligned(bytes)` | pad to a byte boundary, then copy the bytes verbatim |
| `String` | Boolean **isNull**; if not null: UInt16 byte length, **pad to byte boundary**, then the UTF-8 bytes. An empty string still pads |

Block varint, `blockSize` clamped to 2..16:

```
h      = index of highest set bit of v (0 when v == 0)
blocks = (h + blockSize) / blockSize            // integer division, >= 1
write (blocks - 1) zero bits, then one 1 bit    // unary block count, terminator last
write v in blocks * blockSize bits
```

Reading counts 0-bits up to the first 1-bit (that many + 1 blocks). If the buffer runs out while
counting, the read yields 0.

A read past the end of the buffer throws on the reader; the transport catches nothing for the
simulation layer — it is the simulation's own try/catch that drops a malformed packet.

### 4.3 Worked examples

All produced by `scratch/fusion/transport-spec/bb.py`, a model of the rules above. Bytes are in
wire order.

| write sequence | bits | bytes |
|---|---|---|
| Boolean true, Byte 0x5A, UInt16 0x1234 | 25 | `B5 68 24 00` |
| Int32 −2 (32 bits) | 32 | `FE FF FF FF` |
| Int32 −2 in 8 bits (reads back as 254) | 8 | `FE` |
| UInt32 3 in 3 bits, then UInt32 0x1FF in 9 bits | 12 | `FB 0F` |
| UInt32VarLength 300 | 16 | `AC 02` |
| Boolean false, then UInt32VarLength 300 (unaligned) | 17 | `58 05 00` |
| Int32VarLength −1 | 40 | `FF FF FF FF 0F` |
| block varint 0, blockSize 4 → `1` + `0000` | 5 | `01` |
| block varint 5, blockSize 4 → `1` + `0101` | 5 | `0B` |
| block varint 300, blockSize 4 → `0 0 1` + 12 bits | 15 | `64 09` |
| block varint 300, blockSize 8 → `0 1` + 16 bits | 18 | `B2 04 00` |
| Boolean true, String "Hi" | 40 | `09 00 00 48 69` |
| String null | 1 | `01` |
| String "" (false, length 0, pad) | 24 | `00 00 00` |
| 3 bits `101`, then WriteBytesAligned `AA BB` | 24 | `05 AA BB` |
| Single 1.0 | 32 | `00 00 80 3F` |

Reading the first row by hand: byte 0 = `1` (the bool) | `0x5A << 1` = `0xB5`; byte 1 = the top
bit of 0x5A (`0`) | the low 7 bits of 0x34 shifted up 1 = `0x68`; and so on.

### 4.4 Not the same as the event-100 serializer

Event-100 protocol messages use a different class, `Fusion.Protocol.BitStream` (what
`FusionBitReader/Writer` in `FusionProtocol.cs` model). It shares the LSB-first bit order and the
length-prefixed-string shape, but **does not pad before byte arrays or string bytes** — they are
written at whatever bit offset the stream is at. Do not reuse one codec for the other: a
`NetBitBuffer` string after an odd number of bits differs from a `BitStream` string by the padding.

## 5. Connection lifecycle

`NetConnectionStatus`: **Created = 1, Connecting = 2, Connected = 3, Disconnected = 4, Shutdown = 5**
(0 = a free slot). Connecting is client-only; Created is a server-side instant between allocating a
slot and processing the Connect that caused it.

### 5.1 What the client does (the behaviour a server must satisfy)

A Shared-mode client starts this only after event 100 told it to: Join Confirmation (which gave it
its UniqueId) and then Start with `ConnectToShared`. It then opens one connection to
`FromActorId(0)`:

```
attempts = 0
loop while status == Connecting:
    if attempts == ConnectAttempts (10):        fail(Timeout)  -> ShutdownReason.ConnectionTimeout
    send Connect(ConnectionId, token, UniqueId) ; attempts += 1
    wait ConnectInterval (0.5 s) or until Accepted / Refused arrives
on Accepted : RemoteId = AcceptedLocalId ; Counter = Accepted.Counter ; status = Connected
on Refused  : fail(reason)
```

So the server has **≈ 5 s from the first Connect** (attempts at t = 0, 0.5, … 4.5 s; failure is
declared at ≈ 5.0 s) to answer Accepted. Both numbers are compile-time constants in this SDK
(`NetworkConfiguration.ConnectAttempts` / `ConnectInterval` are getter-only), not project settings.

The client only checks its status when Accepted arrives; the `AcceptedRemoteId == my ConnectionId`
check is a debug-only assertion (live in these "Unity Debug" DLLs, compiled out of release game
builds). Echo it correctly anyway.

While Connecting, the client drops every non-command datagram. Once Connected it never sends
Connect again, and a late duplicate Accepted is logged and ignored.

### 5.2 What the server does with a Connect

Order of checks for a Connect from address A (= actor *a*):

1. **Capacity (peer level).** If A is not yet known and the transport is at its connection limit,
   reply `Refused(ServerFull)` immediately and forget A. The SDK server's limit is the room's
   `PlayerCount` (`PlayerCount − 1` when the server is itself a player, i.e. a host).
2. **No connection for A yet → ask the application** with `(A, token, UniqueId)`:
   - **Ok** → allocate a slot (§5.3), status Created; then process the Connect as below.
   - **Refuse** → reply `Refused(ServerRefused)`, keep no state.
   - **Waiting** → **reply nothing** and keep no state. The client's next retry (0.5 s later) asks again.
3. **Connection for A exists:**
   - Created → set `RemoteId = Connect.ConnectionId`, `Counter = ++groupCounter`, status Connected,
     queue `Accepted(LocalId, RemoteId, Counter)`, tell the application "connected".
   - Connected → queue the **same** Accepted again (idempotent; the client may have lost the first).
   - any other status → ignore.

The SDK server's application decision (in `Simulation`): answer **Waiting unless the 8-byte UniqueId
(as LE uint64) already has a registered PlayerRef mapping**; if it does, defer to the game's
`OnConnectRequest` callback, which defaults to Ok. In our deployment the mapping is registered from
the event-100 Join flow (`FusionProtocol.UniqueIdForActor` → `RegisterUniqueIdPlayerMapping`), so a
Connect that beats that registration gets Waiting, and the retry 0.5 s later succeeds. The 5-second
budget in §5.1 is the hard limit on that race.

UniqueId already in use by another connection (inference about the effect): the connection table
indexes UniqueIds; if a Connect from a *new* address carries a UniqueId that an existing slot holds,
that slot is re-pointed to the new address and **no reply is sent** for this Connect; the client's
next retry then hits case 3 (Connected → Accepted re-sent, with the old ids, Counter and — notably —
the old notify sequence state). Over the relay the address is the actor number, so this only happens
if a player's actor number changes while their UniqueId does not.

### 5.3 Server-side identifiers

- **LocalId** (`AcceptedLocalId`): `Group` = 0 (the SDK simulation runs one group), `GroupIndex` =
  slot index, `Generation` = slot generation (1 on first use, +1 on each reuse, never 0). The client
  stores it and never sends it back; it has no further wire effect.
- **Counter**: a per-transport counter, incremented once per accepted connection and **never reset**;
  first connection gets 1. **It matters:** a Shared-mode client builds every NetworkId it allocates
  as `(Counter << 19) | (localCounter & 0x7FFFF)` (keeping the low 32 bits). So Counter is the
  client's object-id namespace: it must be **≥ 1** (0 would overlap ids the server allocates from 1
  upward) and **unique per client in the session** — and, as an inference, not reused for a later
  client while objects created by an earlier holder can still exist. Only its low 13 bits survive
  the shift.

### 5.4 Disconnect

Who starts it, and what is sent:

| trigger | reason recorded | packets |
|---|---|---|
| application disconnects a peer (`Disconnect`) | Requested | one Disconnect(Requested, token) immediately, **then** one more Disconnect(Requested, no token) on the next update |
| no datagram of any type from the peer for `ConnectionTimeout` (default 10 s) | Timeout | one Disconnect(Timeout) |
| notify sequence jump beyond ±2048 (§6.2) | SequenceOutOfBounds | one Disconnect(SequenceOutOfBounds) |
| a notify send attempted with the send window full (§6.3) | SendWindowFull | one Disconnect(SendWindowFull) |
| peer's Disconnect received while **Connected** | the **peer's** reason, copied | one Disconnect back, with that same reason |

A Disconnect received while not Connected (e.g. still Connecting) is logged and ignored.

After Disconnected the connection moves to **Shutdown** on the next update: every un-acked notify
envelope is discarded (neither delivered nor lost — a third callback, "dispose"), the address is
unmapped from the transport immediately, and the slot itself (with its UniqueId entry) is released
only after `ConnectionShutdownTime` (default 1 s). During that second a fresh Connect from the same
actor passes the peer-level filter but finds the dying slot and is ignored — the client's next retry
after the release succeeds. A refused peer is unmapped the same way.

Client side, the received reason is handed to the game (`OnDisconnectedFromServer(reason)`); the game
decides what to show.

## 6. The notify channel (sequenced, unreliable, delivery-notified)

Every simulation packet in both directions is a `NotifyData` (type 3). It is never retransmitted by
the transport; instead the *sender* learns, per packet, whether it was **delivered** or **lost**, and
the simulation layer decides what to resend. Per connection, each side keeps:

| state | width | initial | meaning |
|---|---|---|---|
| `sendSeq` | 16 bits | 0 | last sequence assigned; the first packet sent is **1** |
| `sendWindow` | ring of 128 envelopes | empty | `{sequence, sendTime, packetType, userToken}` of every NotifyData / NotifyReliableData not yet resolved by an ack |
| `recvSeq` | 16 bits | 0 | newest sequence accepted from the peer |
| `recvMask` | 64 bits | 0 | bit *k* ⇔ `recvSeq − k` accepted |
| `recvUnacked` | int | 0 | packets accepted since we last sent a NotifyData |
| `lastNotifySend` | time | — | when we last sent NotifyData/NotifyReliableData **or** NotifyAcks |
| `lastRecv` | time | connect time | when we last received **any** datagram from the peer |
| `rtt` | seconds | 0.1 | see §6.5 |

### 6.1 Sequence arithmetic

Sequences are 16-bit and wrap from 65535 to 0. All comparisons use the signed difference
`dist16(a, b) = (short)(a − b)`: positive = `a` is newer. Example: `dist16(2, 65534) = +4`,
`dist16(65534, 2) = −4`.

### 6.2 Receiving NotifyData / NotifyReliableData (unfragmented)

```
on any datagram from a known connection:  lastRecv = now      // before any checks, any type/status
on datagram type 3 or 8, connection Connected:
    if length <= 14: drop
    d = dist16(h.Sequence, recvSeq)
    if d > 2048 or d < -2048: disconnect(SequenceOutOfBounds); stop
    if d < 0: drop                                   // older than the newest: never accepted
    if h.Fragment != 0: fragment path (§7); stop
    if d == 0: drop                                  // duplicate
    accept:
        recvSeq   = h.Sequence
        recvMask  = (d >= 64) ? 1 : (recvMask << d) | 1
        recvUnacked += 1
        process h.AckSequence / h.AckMask            (§6.3)
        deliver payload from bit 112 to the end      (type 3 → notify payload; type 8 → §8)
```

Important properties:

- **Reordered packets are discarded, not buffered.** A packet that arrives after a newer one never
  gets its mask bit, so its sender will see it as lost. Delivery is "newest wins".
- The ±2048 bound is `WindowSize × 16`.
- A gap of 64 or more clears the mask history ("huge loss"); the sender then sees everything older
  than the new packet as lost.

### 6.3 Processing acks (from types 3, 4 and 8)

```
resolved = 0
while sendWindow not empty:
    e = oldest envelope
    d = dist16(e.sequence, h.AckSequence)
    if d > 0: break                          // peer has not reached this one yet
    if d == 0: rtt = max(0, now - e.sendTime)
    remove e ; resolved += 1
    if d <= -64 or (h.AckMask bit (-d)) == 0:
         LOST      → type 8: put the reliable slice back at the FRONT of the reliable queue (§8)
                     type 3: report lost(e.userToken) to the simulation
    else DELIVERED → type 8: free the slice
                     type 3: report delivered(e.userToken) to the simulation
```

Envelopes are resolved strictly oldest-first, and only by an ack whose `AckSequence` has reached
them. A NotifyAcks (type 4) carries no payload and consumes no sequence; its `Sequence` field is 0
and ignored.

Send-window rules (the stall/overflow behaviour):

- Asking for a notify send buffer **fails** while the connection is not Connected or the window holds
  128 envelopes — the SDK simulation simply skips sending that tick.
- If a send nevertheless finds the window full, the connection is disconnected with
  **SendWindowFull**.
- Envelopes only leave the window through acks. A peer that keeps sending but never acks therefore
  stalls our sending after 128 packets (≈ 2 s at 60 Hz); it is not disconnected for that, only by
  the inactivity timeout if it also goes silent.

### 6.4 Sending, ack piggy-backing and keepalive

Every NotifyData / NotifyReliableData we send carries our current `recvSeq` / `recvMask` as
`AckSequence` / `AckMask` and the next `sendSeq`; sending a **type 3** resets `recvUnacked` to 0
(a type 8 does not). An envelope is pushed for every type 3 and type 8.

Once per transport update, for each Connected connection, in this order:

```
if now - lastRecv > ConnectionTimeout:  disconnect(Timeout); next connection
reliable slot (§8): at most one NotifyReliableData per 50 ms
if (recvUnacked > 0 and recvUnacked >= 8) or (now - lastNotifySend > 0.1 s):
    send NotifyAcks(AckSequence = recvSeq, AckMask = recvMask)     // 14 bytes
    recvUnacked = 0 ; lastNotifySend = now
```

So with no simulation traffic the transport emits an **ack-only packet every ~100 ms** (once per
update after 0.1 s of silence), and that is the only keepalive — `NetCommands.Ping` and
`ConnectionPingInterval` (1 s) are unused in this build. Inactivity is measured on *any* received
datagram, so these ack-only packets alone keep both sides from timing out.

The SDK simulation (above this layer) additionally throttles itself: to a connection that has been
silent for ≥ 1 s it sends at most one NotifyData per 0.5 s. A server that answers every client
packet promptly never triggers that.

### 6.5 Round-trip time

RTT is measured only when an ack's `AckSequence` exactly equals a still-pending envelope's sequence:
`rtt = now − that envelope's sendTime`, no smoothing, initial value 0.1 s. The client exposes this
as its RTT to the server (used by its time synchronisation and stats). Consequence for a server:
**ack promptly** — an ack-only packet sent 100 ms after the data it acknowledges inflates the
client's RTT by up to 100 ms. Piggy-backing acks on the per-tick NotifyData is what the SDK server
does.

## 7. Fragmentation and size limits

Only **NotifyData (type 3)** is ever fragmented, and only by the sender's transport after the
simulation has written the whole packet:

```
total = 14 + payloadBytes
if total <= 1280: send as one datagram, Fragment = 0, unreliable
else:
    split the payload (not the header) into chunks of 1122 bytes (last one shorter)
    for i = 1..n:   (n <= 40)
        datagram = copy of the 14-byte header (same Sequence/AckSequence/AckMask),
                   Fragment = i, | 0x80 on the last chunk,
                   followed by chunk i
        send it with reliable = true   (→ Photon reliable, channel 1)
```

One sequence number and one send-window envelope cover all the pieces; the packet is delivered or
lost as a whole. Since a fragmented packet is > 1280 bytes, it always has at least two pieces
(`Fragment = 0x81` never occurs from the SDK).

Receiver, per connection (state: `fragIndex`, `fragDistance`, a 51 200-byte reassembly buffer):

```
d = dist16(h.Sequence, recvSeq)          // checked against the bounds and d < 0 as in §6.2
k = h.Fragment & 0x7F
if d == 0:                                 drop
elif k == 1:                               start over: buffer = whole datagram (header + chunk);
                                           fragIndex = 1; fragDistance = d
elif k == fragIndex + 1 and d == fragDistance:
    append the chunk (datagram minus its 14-byte header)
    if buffer would exceed 51 200 bytes:   reset, drop
    fragIndex = k
    if h.Fragment & 0x80:                  accept the header exactly as §6.2 "accept" does
                                           (recvSeq, recvMask, acks), then deliver the
                                           reassembled buffer from bit 112; reset
else:                                      reset, drop
```

Any unfragmented packet that is accepted in between also resets the reassembly. So pieces must
arrive **in order and without a newer notify packet overtaking them**; the SDK gets that by sending
them reliably on their own Photon channel in one burst. Missing pieces are not re-requested: the
packet simply never gets its mask bit and the sender sees it as lost.

Constants (`NetPeer`):

| constant | value | meaning |
|---|---|---|
| `MAX_MTU_BYTES_TOTAL` | 1280 | largest unfragmented Fusion datagram; fragmentation threshold |
| `DEFAULT_HEADERS` | 144 | 1280 − 1136, headroom reserved for lower layers |
| `MAX_MTU_BYTES_PAYLOAD` | 1136 | largest fragment datagram = 14 + 1122 |
| `MAX_MTU_BITS_PAYLOAD` | 9088 | the same in bits |
| fragment chunk | 1122 | payload bytes per fragment |
| `FRAG_MAX_COUNT` | 40 | pieces per packet (sender-side assertion only; the receiver does not count) |
| `FRAG_END_BIT` | 0x80 | last-piece flag in `Fragment` |
| `MAX_PACKET_BYTES_PAYLOAD` | 44 880 | 40 × 1122, largest notify payload |
| `MAX_PACKET_BYTES_TOTAL` | 51 200 | reassembly buffer size |
| simulation `PacketSize` | 8192 | the Fusion simulation overrides the 1280 default: buffers start at 8 KiB and grow on demand; this is also the client's per-datagram receive limit (§1.2) |

Server-side note (inference): the receiver accepts fragments of any size, so a server is free to
fragment at a lower threshold than 1280 if that keeps each piece inside one Photon datagram.
Remember the Photon client's 1200-byte receive buffer (§1.2): a 1280-byte Fusion datagram plus the
event and eNet headers does **not** fit one unreliable Photon datagram.

## 8. The reliable channel (`NotifyReliableData`, type 8)

An ordered, reliable byte-message service, used by the SDK for `SendReliableDataToPlayer/Server`
(game-level "reliable data" transfers). It rides the notify channel: each piece is one notify
packet, and loss detection is the notify ack (§6.3). Layout:

| offset | size | field |
|---|---|---|
| 0 | 14 | notify header, PacketType = 8, Fragment = 0 |
| 14 | 48 | `ReliableId` |
| 62 | SliceLength | the slice bytes |

`ReliableId` (48 bytes, little-endian):

| offset | size | field | meaning |
|---|---|---|---|
| 0 | 8 | Sequence | reliable sequence; only the **low 32 bits** are significant (wraps at 2³²); first slice = 1 |
| 8 | 4 | SliceLength | bytes in this slice, 1..1088 |
| 12 | 4 | TotalLength | bytes in the whole message (same in every slice) |
| 16 | 4 | Source | sender's player index (PlayerRef index, −1 = the server) |
| 20 | 4 | SourceSend | sender's per-message counter (1, 2, …) |
| 24 | 4 | Target | final recipient's player index, **−1 = the server** |
| 28 | 16 | Key | `ReliableKey`, 16 opaque application bytes |
| 44 | 4 | — | padding |

Sending a message of L bytes: split into slices of ≤ **1088** bytes (so 14 + 48 + 1088 = 1150 ≤ 1280:
never fragmented), each getting the next reliable Sequence and the same Source/SourceSend/Target/Key/
TotalLength, appended to a per-connection queue. The queue drains at **one slice per 50 ms**
(`RELIABLE_SEND_INTERVAL`) per connection, only when the notify window has room. A slice whose
notify packet is reported lost goes back to the **front** of the queue and is resent (with a new
notify sequence) in a later 50 ms slot; a delivered one is freed. There is no other reliable ack.

Receiving: `expected = lastDelivered + 1` (32-bit wrap; `lastDelivered` starts at 0):

```
if slice.Sequence == expected: deliver it; then repeatedly deliver any stored slice
                                whose Sequence is the new expected
else:                           store it (its SliceLength is recomputed from the datagram length)
```

There is no duplicate check: a slice resent after its ack was lost (so already delivered) is stored
and never matches again — harmless for ordering, but it leaks memory (inference about the effect).

Reassembly of slices into a message is **above** the transport (the runner groups by
`(Source << 32) | SourceSend` and completes when the slice lengths add up to TotalLength).

Server proxy rule (SDK server, `ReliableDataTransferModes` default = ClientToServer |
ClientToClientWithServerProxy): a slice whose `Target` is neither −1 nor the server's own player is
re-sent, `ReliableId` unchanged except a fresh Sequence, to the connection of player `Target`; a
slice for −1 is delivered to the server application. If the mode needed for a slice is disabled the
sender is disconnected (Requested).

## 9. Unreliable and unconnected data

- **UnreliableData (type 2)**: `[02]` then payload bits from bit 8; no sequencing, no acks. The
  transport supports it, but the Fusion simulation never sends it and its receive handler is an
  always-fail assertion. A server must **not** send type 2 to a Fusion client.
- **Unconnected (type 5)**: `[05]` then raw bytes from byte 1. Used only as NAT-punch "pings" to IP
  addresses (the ClientServer direct-UDP path, `NetworkSendPing` with an empty body → the 1-byte
  datagram `05`). Ignored on receipt. Not used over the relay.

## 10. Configuration that affects the wire or its timing

Nothing in this layer is negotiated: the Accepted command carries ids and Counter only. Each side
runs on its own configuration; the client's comes from compiled-in constants plus its
`NetworkProjectConfig`, which it also sends us as JSON in the event-100 `NetworkConfigSync`
(`FusionProtocol.TryDecodeNetworkConfigSync`), so a server can mirror the client's values.

| setting | `NetConfig` default | as run by the Fusion simulation | source | effect |
|---|---|---|---|---|
| `ConnectAttempts` | 10 | 10 | built-in constant | Connect retries (§5.1) |
| `ConnectInterval` | 0.5 s | 0.5 s | built-in constant | Connect retry spacing → ~5 s to answer |
| `ConnectionTimeout` | 10 s | project config `Network.ConnectionTimeout` (default 10) | per side | inactivity disconnect (Timeout) |
| `ConnectionShutdownTime` | 1 s | project config `Network.ConnectionShutdownTime` (default 1) | per side | slot reuse delay after disconnect |
| `ConnectionDefaultRtt` | 0.1 s | 0.1 s | built-in | initial RTT |
| `ConnectionPingInterval` | 1 s | 1 s | built-in | **unused** in 2.0.8 |
| `PacketSize` | 1280 | **8192** | simulation override | buffer size / max inbound datagram |
| `MaxConnections` | 1000 (clamped 1..2048) | client 1; server `PlayerCount` (`−1` for a host) | per side | ServerFull threshold |
| `ConnectionGroups` | 12 | **1** | simulation override | `NetConnectionId.Group` is always 0 |
| `ConnectionSendBuffers` | 4 | 4 | — | no effect on the wire |
| `SocketSendBuffer` / `SocketRecvBuffer` | 256 KiB | 256 KiB | — | direct UDP only |
| `OperationExpireTime` | 3.0 | 3.0 | — | local receive-loop budget; compared against **milliseconds**, so 3 ms |
| `Notify.SequenceBytes` | 2 | 2 | built-in | 16-bit notify sequence (also fixed by the header layout) |
| `Notify.AckMaskBytes` | 8 | 8 | built-in | 64-bit ack mask (also fixed by the header layout) |
| `Notify.WindowSize` | 128 | 128 | built-in | send window; `SequenceBounds` = 128 × 16 = 2048 |
| `Notify.AckForceCount` | 8 | 8 | built-in | ack-only after 8 unacknowledged receipts |
| `Notify.AckForceTimeout` | 0.1 s | 0.1 s | built-in | ack-only / keepalive cadence |
| `Simulation.*` (loss, delay, duplicate) | off | `NetworkConditions` (off unless enabled) | per side | test-only impairment, never in a shipped game unless the developer left it on |

Fixed constants: notify header 14 bytes; Connect 152, Accepted 32, Refused 3, Disconnect 136 bytes;
token ≤ 128 bytes; UniqueId 8 bytes; `ReliableId` 48 bytes; reliable slice ≤ 1088 bytes; reliable
sequence 32 bits; reliable send interval 50 ms; fragment constants in §7.

## 11. A minimal conforming server

Per (room, actor) connection, the server needs: the state in §6 table, a reliable send queue and
reorder store (§8), a fragment reassembly buffer (§7), and the slot ids/Counter (§5.3).

1. **Receive** each event-101 payload from actor *a* as a datagram from `FromActorId(a)`. Dispatch on
   byte 0 (§3). Unknown actor and not a Connect → drop.
2. **Connect** → §5.2. Hold the reply (Waiting) until the event-100 layer has bound this UniqueId to a
   PlayerRef, then Accepted with a fresh Counter. Re-send the identical Accepted for duplicate Connects.
3. **Every server tick, for each Connected client**: build one NotifyData whose payload is the
   simulation's packet (never empty), stamp `Sequence = ++sendSeq`, `AckSequence = recvSeq`,
   `AckMask = recvMask`, push an envelope with the simulation's token, fragment if > 1280 bytes.
4. **Every transport update**: timeout check, one reliable slice if due, ack-only packet if the
   §6.4 condition holds.
5. **Hand up**: `connected(conn, uniqueId, token)`, `notifyData(conn, bits from byte 14)`,
   `delivered(conn, token)` / `lost(conn, token)` for each of our NotifyData, `reliable(conn,
   ReliableId, bytes)`, `disconnected(conn, reason)`.
6. **Send** server datagrams as event 101 to the actor, sender 0, unreliable — reliable only for
   fragments (§1.2, §7).

The boundary with the simulation spec is item 5: the notify payload is "the bits after byte 14, up
to the end of the datagram (or reassembled buffer), with up to 7 zero padding bits at the end".

## 12. Reconciliation with what the repo already says

Checked against `docs/fusion-protocol-map.md`, `docs/fusion-shared-server.md`, doc 05, doc 06,
`FusionProtocol.cs` (`FusionRelayProtocol`), `FusionRelayProtocolTests.cs`, `RelaySocket.cs` and the
event-101 path in `GameServerHandler` / `ListenersHostedService`. Nothing below was changed by this
document; these are findings.

1. **`FusionRelayProtocol` names packet type 4 as "Data". It is `NotifyAcks`; NotifyData is 3.**
   `NetPacketTypeData = 4` and `IsDataPacket` (`payload[0] == 4`, length ≥ 14) therefore match only
   ack-only/keepalive packets. The legacy solo fallback that echoes "data packets" back to a lone
   client echoes its keepalives and nothing else — which is consistent with
   `fusion-shared-server.md`'s "a lone client accepts its own keepalives echoed back", but the code's
   name and comment are wrong.
2. **`FusionRelayProtocol.EncodeAccepted` always sends Counter = 1.** Fine for one client; with two
   clients both would allocate NetworkIds from the same namespace (§5.3). Only the no-Fusion-libs
   fallback uses it today, but a replacement server must issue a fresh Counter per connection.
3. **`RelaySocket.Send` drops the `reliable` hint, and the host wiring sends every server 101
   datagram Photon-reliable** (`RoomSession.SendToActor` defaults to reliable). The SDK sends only
   fragment pieces reliably. Sending all of it reliably adds head-of-line blocking, and — given
   `fusion-protocol-map.md`'s note that the server does not yet retransmit its own reliable eNet
   commands — one lost server datagram would stall every later 101 datagram on that channel until
   the client's 10 s inactivity timeout. Worth checking against the "peer goes quiet a few seconds
   after joining" symptom. (The inference is about our Photon layer, not Fusion.)
4. **SDK version.** `fusion-protocol-map.md` says it was transcribed from Fusion **2.1.2**; the
   decompiled sources and the DLLs in `server/fusion-libs/` are **2.0.8.1402**, matching the 2.0.8
   serialization version in Outbound's captured Join. The 2.0.8 `ProtocolMessageVersion` ends at
   V1_6_0 = 10, so the `ProtocolVersion >= 12` (PlayerCounterRef) branch in `FusionProtocol` belongs
   to a newer SDK and never fires for Outbound — harmless.
5. **The libs are the "Unity Debug" build.** Debug-only assertions (`Assert.Check`) are live in the
   hosted SDK server but compiled out of release game clients — so "the SDK server tolerated it" and
   "a retail client tolerates it" are not the same test in either direction.
6. **"Omitting Sender is not equivalent"** (comment on `GameServerHandler.ServerFusionEvent`): in
   this SDK the client's Fusion layer maps an absent sender (−1) to actor 0, the same as an explicit
   0. Keeping parameter 254 = 0 is right and costs nothing; the stated reason does not match the
   code, so whatever was observed then had another cause.
7. `FusionRelayProtocolTests` says the client connects "to the relay representing itself"; it
   connects to `FromActorId(0)`, the server.

Confirmed correct: the Connect layout and its 152-byte size, the Accepted layout and 32-byte size
with Generation at offsets 12 and 20 and Counter at 24, little-endian struct overlay, event 101 with
parameter 245, and `RelaySocket`'s actor-id addressing.

## 13. Open questions / unverified

1. **Release build parity.** Everything was read from the 2.0.8 "Unity Debug" DLLs. The retail
   client is a release (IL2CPP) build; the wire format should be identical, but timing constants and
   assertion behaviour have not been compared against a release `Fusion.Sockets`.
2. **What the Photon Cloud Fusion plugin uses** for `Counter`, `AcceptedLocalId`, and Photon delivery
   mode on its own 101 sends — we have exactly one Accepted capture (first client: Counter 1, slot 0,
   generation 1). A second client's Accepted from the real cloud would confirm the counter scheme.
3. **Waiting vs. the 5 s budget.** Whether a retail client ever hits the 10-attempt limit against our
   server (event-100 PlayerRefMapping arriving late) is not measured.
4. **Counter reuse.** That a Counter must not be reused while an earlier holder's objects survive is
   inferred from how NetworkIds are built, not observed.
5. **Reconnect with stale notify state.** A client that re-runs Connect on a connection the server
   still holds as Connected gets the old Accepted and the old `recvSeq`; its fresh sequence (1) then
   reads as "old" until it passes the server's, or trips SequenceOutOfBounds. Not exercised.
6. **Duplicate reliable slices** (§8) are assumed to only leak memory; not exercised.
7. **Fragment pieces over unreliable-sequenced** Photon delivery instead of reliable — expected to
   work (order is preserved, loss just loses the packet) but untested with a real client.
8. **Fusion 1.x.** Only the four structs named in §0 were compared with 1.1.12; the state machine,
   timings and reliable channel were not re-read for 1.x.
