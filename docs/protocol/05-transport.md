# Transport (Reliable UDP)

Photon's transport is an eNet-derived reliable-UDP protocol implemented in
`Photon3Unity3D.dll` (`EnetPeer : PeerBase`, `EnetChannel`, `NCommand`). The framing below
is from static analysis of that code.

> **Status:** documented from the binary. Live byte-level validation with a packet capture
> is deferred — run `tools/capture/capture.ps1` after installing Wireshark/npcap to confirm
> against the wire (npcap requires an interactive driver install, so it was not possible in
> the automated recon run).

## UDP packet header (12 bytes)

Each UDP datagram carries one Photon packet header followed by 1..N commands.

| Offset | Field | Type | Notes |
|---|---|---|---|
| 0 | PeerID | int16 | server-assigned; `-1`/`-2` before connect (`-2` enables server tracing) |
| 2 | CrcEnabled flag | byte | 0 = no CRC |
| 3 | CommandCount | byte | number of commands in this packet |
| 4 | ServerSentTime | int32 | sender timestamp (ms) |
| 8 | Challenge | int32 | per-connection random; mismatched packets are dropped |

(If CRC is enabled, a 4-byte CRC follows and is validated before parsing.)

## Command header (per command)

| Field | Type | Notes |
|---|---|---|
| CommandType | byte | see table below |
| ChannelId | byte | logical channel (RPCs/events use separate channels) |
| CommandFlags | byte | bit0 = Reliable, bit1 = Unsequenced |
| ReservedByte | byte | `4` default; on `Disconnect`, a reason byte |
| CommandLength | int32 | total command size including header |
| ReliableSequenceNumber | int32 | per-channel sequence for ordering/acks |

`SendReliable` and `SendUnreliable` commands then carry the GpBinary-serialized
operation/event payload (see [04-serialization.md](04-serialization.md)).

## Command types (`CT_*`)

| Code | Name | Meaning |
|---|---|---|
| 0 | CT_NONE | |
| 1 | CT_ACK | acknowledgement of a reliable command |
| 2 | CT_CONNECT | connection request |
| 3 | CT_VERIFYCONNECT | connection accepted (assigns PeerID) |
| 4 | CT_DISCONNECT | disconnect (reason in reservedByte) |
| 5 | CT_PING | keepalive / RTT probe |
| 6 | CT_SENDRELIABLE | reliable, sequenced payload |
| 7 | CT_SENDUNRELIABLE | unreliable, sequenced payload |
| 8 | CT_SENDFRAGMENT | fragment of a payload larger than MTU |
| 11 | CT_SENDUNSEQUENCED | unreliable, unsequenced |
| 12 | CT_EG_SERVERTIME | Exit Games server-time sync |
| 13 | CT_EG_SEND_UNRELIABLE_PROCESSED | |
| 14 | CT_EG_SEND_RELIABLE_UNSEQUENCED | |
| 15 | CT_EG_SEND_FRAGMENT_UNSEQUENCED | |
| 16 | CT_EG_ACK_UNSEQUENCED | |

### Unreliable / unsequenced command header (types 7 and 11)

`CT_SENDUNRELIABLE` (7) and `CT_SENDUNSEQUENCED` (11) extend the 12-byte common header with one
extra `int32` (big-endian) immediately after `ReliableSequenceNumber`, making their header **16 bytes**:

| Offset | Field | Notes |
|--------|-------|-------|
| 12 | UnreliableSequenceNumber (7) / UnsequencedGroup (11) | int32, big-endian |

`CommandLength` (at offset 4) **includes** this extra field, so the payload begins at offset 16
(not 12). Reliable commands (type 6) keep the 12-byte header.

Delivery notes for relaying an unreliable command to a client:
- The client tracks a per-channel incoming unreliable sequence and **discards** any unreliable
  command whose `UnreliableSequenceNumber` is not strictly greater than the last accepted one
  (stale/duplicate). A relay must therefore stamp a monotonically increasing per-channel value.
- An unreliable command also carries a `ReliableSequenceNumber`; the client only **delivers** it
  once it has consumed the reliable stream up to that value (`reliableSeq <= incomingReliableSequenceNumber`).
  Stamp it with the channel's most recently sent reliable sequence — never a higher one — or the
  packet is held until the reliable stream catches up.
- Unreliable commands are **not** acknowledged (only reliable commands are).

## Reliability model

- Each `EnetChannel` keeps independent incoming/outgoing **reliable** and **unreliable**
  sequence numbers (`incomingReliableSequenceNumber`, `outgoingUnreliableSequenceNumber`, …).
- Reliable commands are buffered until acked (`CT_ACK`); unacked commands are resent until a
  timeout, after which the peer disconnects.
- Fragments (`CT_SENDFRAGMENT`) are reassembled by reliable sequence number before the
  payload is surfaced.

## Connection establishment

`CT_CONNECT` → `CT_VERIFYCONNECT` (server assigns the real `PeerID`, replacing `-1`/`-2`) →
application-level `Authenticate` (op 230) rides on `CT_SENDRELIABLE`. See
[01-connection-flow.md](01-connection-flow.md) for the application sequence.

## Implications for reimplementation (Phase 1)

The transport layer is **game-agnostic** — implementing this eNet framing (header parsing,
per-channel sequencing, ack/resend, fragmentation) is the foundation. Photon's reference
serialization (`Protocol16`/GpBinary) can be ported directly from the decompiled
`Photon3Unity3D` logic since the project targets C#/.NET.
