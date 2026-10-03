# 08 — Fusion Shared-mode simulation layer (state relay)

What a self-hosted server must read and write **above** Fusion's transport so that stock Photon
Fusion 2 clients in **Shared** topology join one session, see each other's networked objects and run
RPCs — specified as a *minimal state-holding relay*, not a general Fusion runtime. This replaces the
retail `Simulation.Server` that `server/OpenPak.Server.Fusion` currently links.

Scope boundary: everything here starts from "a NotifyData payload of N bits arrived from connection C"
or "is to be sent to connection C". The transport underneath — NetPeerGroup, the Connect/Accepted
handshake, the notify channel with its delivered/lost callbacks, reliable-data slicing, fragmentation,
and the bit-buffer primitives — is specified in `07-fusion-relay-transport.md`. The session protocol on
Photon event 100 (Join / Start / PlayerRefMapping / ChangeMasterClient) is in `fusion-protocol-map.md`
and `06-fusion-session-join.md`.

Conventions. MUST / SHOULD / MAY are normative. **[inferred]** marks a statement deduced from client
behaviour or Photon's public documentation rather than read from server code or measured live.
**[measured]** marks a fact established by a live Outbound session (`fusion-shared-server.md`).
Everything unmarked was read from the decompiled SDK (clean-room description, own words).

---

## 1. Which Fusion build this describes

Outbound ships a **Fusion 2.0.x** runtime. Its IL2CPP metadata (`outbound-re/dump-output`) has the same
`StateReplicator` member list, the same block-size constants (data 6, offset 4, header 8, global 8), the
same `NetworkObjectHeader`, `SimulationRuntimeConfig`, `SimulationPacketHeader`, `TimeFeedback` and
`SimulationMessageInternalTypes` layouts as the **2.0.8.1402** assembly in `server/fusion-libs/` and its
decompile `scratch/fusion/src/Fusion.Runtime.decompiled.cs`. Only methods the AOT compiler strips
(an uncalled private helper, `[Conditional]` debug hooks) differ. This document is the 2.0.x wire.

**Correction to `docs/fusion-shared-server.md` ("The one required binary patch").** The 2.0.x wire has
**no `PluginVersion` field at all** — not in the object record, not in the header. The PluginVersion
varint and the `WriteObject_WriteData` / `ReadObjectUpdate_GetWords` methods that `tools/fusion-patcher`
edits exist only in **Fusion 2.1.x** (the 2.1.2 build under `scratch/home/photon-oracle/`, where header
word 10 became `PluginVersion`). `server/fusion-libs/Fusion.Runtime.dll` is byte-identical (MD5) to the
unpatched 2.0.8 copy, and the patcher could not even run against it (the methods it looks up are absent).
So the production Outbound host runs the **stock, unpatched** 2.0.8 server, and the asymmetry described
there is a 2.1.x fact that does not apply to Outbound. A future 2.1.x title needs its own wire study;
§18 lists what is known to differ.

---

## 2. Model

- One **room** = one simulation. Every Fusion peer in it is a *client* connected to the server
  (Photon actor 0) over the relay transport. The server has no local player (`LocalPlayer = None`) and
  never runs game code.
- State lives in **objects**: a header of 20 32-bit words followed by data words. Clients that hold
  *state authority* (SA) over an object send its changed words; the server stores the latest value of
  every word and forwards changes to every other connection.
- A few **structs** (objects flagged `Struct`) are owned by the server: RuntimeConfig, SceneInfo,
  PhysicsInfo and one PlayerData per player.
- **Simulation messages** (RPCs and internal control messages) ride in the same packets, with their
  own per-connection reliable sequencing.
- The server holds, per connection and per object, how far that connection has been told about the
  object (§8). Delivery feedback comes from the transport's notify-delivered / notify-lost callbacks.

The server MAY treat as opaque: every data word of every client object (except the transform words of
§4.7, which have their own encoding), header words 10–19, NestingRoot/NestingKey/Type, RPC payloads,
the contents of SceneInfo and PhysicsInfo. It MUST interpret: the packet header, the message envelope,
the object record structure (ids, header-present bit, word deltas, terminator), header words 1, 6, 7,
8, 9, the four internal message types, and the struct layouts it writes itself.

---

## 3. Primitive encodings used in this document

Defined bit-exactly in 07; named here so the tables below are unambiguous.

| name here | meaning |
|---|---|
| `bool` | one bit |
| `uN` | N-bit unsigned field |
| `vb(B)` | block varint with block size B: a unary length prefix (k−1 zero bits then a one bit) followed by k·B value bits, k = smallest count of B-bit blocks that holds the value (k ≥ 1). Signed ints are cast to unsigned (no zig-zag) unless `zz` is said. |
| `zz32` / `zz64` | zig-zag mapping of a signed value to unsigned before encoding |
| `v7` | byte-style varint: 7 value bits + continuation bit per byte, least significant group first, *not* byte-aligned |
| `pad` | write zero bits to the next byte boundary (read side: skip to it) |
| `bytes(n)` | `pad`, then n raw bytes |

All multi-bit fields are written least-significant bit first in the buffer's bit order (07).

---

## 4. Packet layout (NotifyData payload)

### 4.1 Frame

Both directions use the same frame. Offsets are in bits from the start of the NotifyData packet.

| order | field | C→S | S→C |
|---|---|---|---|
| 1 | Notify header (112 bits, owned by 07) | ✓ | ✓ |
| 2 | `SimulationPacketHeader` (64 bits, at bit 112) | ✓ | ✓ |
| 3 | Message block (§4.3) | ✓ | ✓ |
| 4 | `TimeFeedback` (§4.4) | — | ✓ |
| 5 | Destroy list (§4.5) | ✓ | ✓ |
| 6 | Object records: structs first, then objects (§4.6) | ✓ | ✓ |

The only asymmetry in Shared mode is item 4: the server writes TimeFeedback, clients never do, and the
server never reads one. (The 2.1.x PluginVersion asymmetry does not exist here, §1.) A sender reserves
the 64 header bits, writes the body, then fills the header in with the final counts.

Alignment, exactly (read from the SDK). The header is always at bit 112 — the reader asserts that its
cursor is at 112 when it reads the header, both for an ordinary packet and for a reassembled
fragmented one. The **only** byte alignment in the whole body is around the message block: one `pad`
before the first envelope and one after the last, both written **unconditionally**, even when the
message count is 0, plus the per-message pad before each payload (§4.3). Since the header ends at bit
176 (byte 22), the leading pad never inserts bits; with zero messages the trailing pad does not either,
so TimeFeedback then starts at bit 176. TimeFeedback, the destroy list and the object records follow
each other **with no padding at all** (bit-contiguous). The four header counts are single bytes in the
byte order of §4.2 (the struct is copied as one little-endian u64).

### 4.2 `SimulationPacketHeader` (64 bits)

Read and written as one 64-bit little-endian quantity; byte order of the struct:

| bits | field | meaning |
|---|---|---|
| 0–7 | `SimulationMessages` (u8) | number of messages in §4.3 |
| 8–15 | `Inputs` / `Cells` (u8) | input count in ClientServer; **always 0 in Shared** |
| 16–23 | `ObjectUpdates` (u8) | number of object records in §4.6 (structs included) |
| 24–31 | `ObjectDestroys` (u8) | number of ids in §4.5 |
| 32–63 | `Tick` (i32) | the sender's simulation tick when the packet was built |

Server→client `Tick` is the server tick (the client's `LatestServerTick`, §10). Client→server `Tick` is
the client's own tick; the server uses it only for time feedback (§10.3).

### 4.3 Message block

`pad`, then `SimulationMessages` message envelopes back to back, then `pad`. Each envelope:

| # | field | encoding | notes |
|---|---|---|---|
| 1 | Offset | `vb(10)` | payload length **in bits** |
| 2 | Tick | `vb(16)` | sender's tick; the server rewrites it on forward (§12.3) |
| 3 | Source | PlayerRef wire form (below) | None from clients; the original sender on server→client |
| 4 | Target | PlayerRef wire form | C→S: the destination player when TARGET_PLAYER is set, else None. S→C: always None (§12.3). Source and Target are never both real players. |
| 5 | flags present | `bool` | false ⇒ flags = 0 and field 6 is absent |
| 6 | flags | `u16` | the low 16 flag bits with `REMOTE` (2) cleared |
| 6a | user-flags present | `bool`, only if flag bit 1 (`USER_MESSAGE`) set | the SDK logs an error if this is ever true; send false |
| 7 | Sequence | `vb(16)` of a u64 | **absent** when flag `UNRELIABLE` (8) is set |
| 8 | payload | `bytes(ceil(Offset/8))` | opaque except for the RPC header and internal types (§12) |

PlayerRef wire form: `bool isRealPlayer`; if true, `v7` of the player *index* (raw − 1). None, Invalid
and MasterClient all encode as a single 0 bit.

Envelope details confirmed against the SDK reader and writer:
- The field order above is exact. Field 6a sits *inside* the "flags present" branch: it exists only
  when field 5 is true **and** bit 1 of the u16 is set.
- Field 7 is an unsigned 64-bit value in `vb(16)` blocks, present exactly when bit 8 (UNRELIABLE) of
  the flags is clear (flags absent ⇒ 0 ⇒ the sequence is present).
- Field 8's `pad` is **per message**: every payload starts on a byte boundary. **When Offset is 0 there
  is neither pad nor bytes** — the writer skips the aligned write and the reader skips the aligned read,
  so the next envelope starts immediately, unaligned. Do not emit a pad for an empty payload.
- The payload is ceil(Offset / 8) whole bytes; the receiver keeps Offset (bits) as the message length.

The receiver ORs `REMOTE` (2) into the flags of every message it reads. Message flags:

| value | name | meaning |
|---|---|---|
| 1 | USER_MESSAGE | raw user payload (no RpcHeader) |
| 2 | REMOTE | set locally on receipt; never on the wire |
| 4 | STATIC | static RPC (RpcHeader.Object = None, Method = static key) |
| 8 | UNRELIABLE | no sequence; not resent on loss; delivered out of band of the reliable order |
| 16 | TARGET_PLAYER | addressed to one player |
| 32 | TARGET_SERVER | addressed to the server only (all internal messages from clients) |
| 64 | INTERNAL | internal control message (§12.5) |
| 128 | NOT_TICK_ALIGNED | may be delivered before its tick |
| 256 | DUMMY | placeholder for a message too big to serialize; payload empty |
| ≥ 65536 | user flags | local only |

Payload size limit: a message payload is under 512 bytes. Message block budget (sender side): the
server stops adding messages once the packet passes 9088 bits (1136 bytes) from its start; the first
message of a packet may exceed the budget. A message that cannot fit alone is replaced by a DUMMY.

### 4.4 `TimeFeedback` (server→client only)

Four fields, each `vb(6)` of a fixed-point value: the float multiplied by 256, rounded half away from
zero, then `zz32`.

| # | field | meaning (seconds) |
|---|---|---|
| 1 | OffsetAvg | smoothed (expected − actual) arrival time of this client's packets (§10.3) |
| 2 | OffsetDev | median absolute deviation of the same series |
| 3 | RecvDeltaAvg | smoothed interval between this client's packets arriving |
| 4 | RecvDeltaDev | median absolute deviation of those intervals |

Exact definitions and client use: §10.3.

### 4.5 Destroy list

`ObjectDestroys` × NetworkId, each `vb(8)` of the raw u32 id (no zig-zag, no delta).

### 4.6 Object records

`ObjectUpdates` records in a row. `prevId` starts at 0 for every packet and becomes the id of each
record after it is written/read.

| # | field | encoding |
|---|---|---|
| 1 | id delta | `vb(6)` of `zz32(id − prevId)` |
| 2 | full | `bool` — the sender believes the receiver has not confirmed this object yet |
| 3 | header words | **only if full**: for header words 1…19 in order, `bool nonZero` then, if set, `vb(6)` of `zz64(word)`. Word 0 (the id) is never sent. |
| 4 | changed words | repeated: `vb(4)` index delta (> 0) from the previous word index, then the word value (below) |
| 5 | terminator | `vb(4)` of 0 |

The word-index cursor for field 4 starts at **19** when `full` is set (so the first data word index is
≥ 20) and at **0** otherwise (so changed header words 1…19 are sent through the same delta list).
Ordinary word value: `vb(6)` of `zz64(value)` (the 32-bit word sign-extended). A word index that falls
inside the transform block of §4.7 uses that encoding instead.

The pair (index delta, value) MAY be packed by the writer into a single ≤ 64-bit write when it fits;
the bit stream is identical to writing the two fields separately, so a reader never sees a difference.

Reader rules the server MUST follow (they are also what the stock client does):
- An index outside `[0, WordCount)` aborts the record: the reader rewinds to just after field 2 and
  skips fields 3–5 (each value consumed with its own encoding), discarding the record's words. The
  stream stays in sync only because every value is self-delimiting; there is no per-record length.
- Header words 1…6 (WordCount/BehaviourCount, Type, NestingRoot, NestingKey, Flags) are read-only after
  creation: a value arriving for them in field 4 is consumed and ignored.
- A record that is not `full` for an id the receiver does not know is skipped entirely (the SDK logs
  "Object does not exist … marked as confirmed").
- A `full` record for an unknown id **creates** the object from the header (§9.2).

### 4.7 The transform block (the one non-opaque data encoding)

An object whose header Flags contain `HasMainNetworkTRSP` (8) **and** whose WordCount is ≥ 34 has a
main transform at data words **22–28**: position x, y, z (three floats, words 22–24) and rotation
(quaternion x, y, z, w, words 25–28). These seven words are never sent as ordinary words. Whenever any
of them is to be sent, the writer rewinds the cursor to word 22 and emits one block:

| # | field | encoding |
|---|---|---|
| 1 | index delta to word 22 | `vb(4)` (the normal delta field) |
| 2 | position x, y, z | three × `vb(4)` of `zz32(round(f · 1024))` (round half away from zero) |
| 3 | rotation | `u32` — a compressed quaternion (smallest-three style packing implemented in `Fusion.Common`) |

After the block the cursor is at word 28; the next delta is relative to 28. Readers always decode the
block starting at word 22 (an index delta that lands on 23–28 is invalid). Decoding is lossy:
position snaps to 1/1024, the quaternion is decompressed and re-normalised.

Consequences for the server:
- It MUST know, per object, whether the block applies (header Flags bit 8 and WordCount ≥ 34) — even
  to *skip* a record, because the block's size differs from seven ordinary words. For an id it no
  longer knows (destroyed), the SDK remembers per connection which destroyed ids had a transform
  block, to keep skipping late records correctly; the server needs the same memory or must discard
  the rest of the packet.
- It SHOULD store the block **in wire form** (the three compressed position integers and the u32
  rotation, e.g. in words 22–25) and re-emit it verbatim. That is lossless and needs no float or
  quaternion maths. Re-encoding decoded floats is lossless for position within ±16384 units but the
  rotation round trip is not guaranteed bit-stable **[inferred]**.
- Any change inside 22–28 marks the whole block changed.

### 4.8 Wire-visible limits

| limit | value | consequence |
|---|---|---|
| object records per packet | 255 (u8 counter) | sender stops at 255 |
| destroys per packet | min(255, config `MaxObjectDestroysSentPerPacket`, default 32) | remainder next packet |
| messages per packet | 255 (u8 counter) | |
| message payload | < 512 bytes | |
| object WordCount | 20 … 32767 (signed 16-bit field) | server MUST accept any value in range |
| object data per packet | the sender abandons a record that would take the packet past 44 880 bytes | larger packets are fragmented by 07 in 1136-byte pieces |
| after a lost multi-fragment packet | the SDK sender limits packets to one fragment (1136 bytes) beyond the first record for a few sends | optional for our sender |

The SDK server additionally asserts `WordCount · 4 ≤ heap block size` (32 KB by default — Outbound's
9268-word object needs the 128 KB page, `fusion-shared-server.md` item 3). That is an SDK allocator
limit, not a wire limit; our server has no such constraint.

---

## 5. `NetworkObjectHeader` (words 0–19)

| word | field | type | written by | server reads? | server writes? |
|---|---|---|---|---|---|
| 0 | Id | NetworkId (u32) | creator | yes (record id) | for its own structs |
| 1 | WordCount (low 16) · BehaviourCount (high 16) | 2 × i16 | creator | **yes** — buffer size | structs |
| 2–3 | Type | NetworkObjectTypeId (2 × u32) | creator | no (opaque) | structs |
| 4 | NestingRoot | NetworkId | creator | no | — |
| 5 | NestingKey | i32 | creator | no | — |
| 6 | Flags | NetworkObjectHeaderFlags | creator | **yes** (Struct, TRSP, DestroyWhenStateAuthorityLeaves, AllowStateAuthorityOverride) | structs |
| 7 | InputAuthority | PlayerRef (raw) | SA client | accept from SA | — |
| 8 | StateAuthority | PlayerRef (raw) | creator; then **the server** | **yes** | **yes** (§12.6, §11.4) |
| 9 | PlayerData | per-connection interest flags | **the server, per recipient** | ignore inbound | yes (§14) |
| 10–19 | reserved | — | — | opaque | — |

Words 0–6 are fixed at creation ("read-only part"). The last `BehaviourCount` words of the object are
per-behaviour change ticks maintained by the SA client; to the server they are ordinary data words.

Header Flags:

| bit | name | server relevance |
|---|---|---|
| 1 | GlobalObjectInterest | AOI only (§14) |
| 2 | DestroyWhenStateAuthorityLeaves | **server duty on leave (§11.4)** |
| 4 | SpawnedByClient | informational |
| 8 | HasMainNetworkTRSP | selects the transform block (§4.7) |
| 16 | AllowStateAuthorityOverride | authority requests (§12.6) |
| 32 | Struct | structs (§7) |
| 64 | DontDestroyOnLoad | client only |
| 128 | StructArray | unused here |
| 256 | AreaOfInterest | AOI only |

`NetworkObjectTypeId` (words 2–3): kind = word3 & 3 (0 Prefab, 1 Custom, 2 InternalStruct,
3 SceneObject; both words 0 = invalid). Prefab: word2 = prefab id. InternalStruct: word2 = struct id
(PlayerData = 1). SceneObject: word2 = scene ref, word3 bits 2–23 object index, bits 24–31 load id.
RuntimeConfig / SceneInfo / PhysicsInfo carry an all-zero type.

---

## 6. Ids and player references

### 6.1 `PlayerRef`

A 32-bit raw value: **0 = None**, **−1 = MasterClient** (a symbolic "whoever is master"), **−10 =
Invalid**, raw > 0 = a real player with *index* raw − 1. In headers and structs the raw value is stored;
on the message wire the index is sent (§4.3). The session layer's `PlayerRef` integers (Join
Confirmation, PlayerRefMapping) are **indices**. Our server uses index = Photon actor number, so a
player with actor 2 has raw PlayerRef 3 in headers. (Current host: `PlayerRef.FromIndex(actor)`.)

### 6.2 `NetworkId`

A u32. 0 = none. **1–1023 are reserved**: 1 RuntimeConfig, 2 PlayerDataArray (unused in this build),
3 SceneInfo, 4 PhysicsInfo. Allocation in Shared mode:

| allocator | id layout | range |
|---|---|---|
| server | a counter starting at 1024 | 1024 … 0x7FFFF (bits 19–31 zero) — used for PlayerData structs |
| client | `(counter & 0x7FFFF) | (connCounter << 19)`, client counter also starting at 1024 | each client owns a disjoint 2^19-id range |

`connCounter` is the per-connection number the server hands out in the transport's **Accepted**
command (07): a room-wide counter incremented for every connection the server accepts, starting at 1.
The server MUST give every connection of a room a distinct, never-reused nonzero counter (13 usable
bits) — otherwise two clients mint colliding ids. The server never needs to validate client ids beyond
"not reserved, not already existing" (§9.2); there is no id-range negotiation message.

---

## 7. The global structs

All are objects with header Flags = `Struct` (32), BehaviourCount 0, words 20+ = the struct body
(little-endian fields, one per word unless noted). The server creates them; clients never create them.

### 7.0 Header words of the server's structs (read from the SDK)

The SDK builds every struct through one path: WordCount = 20 + body words, BehaviourCount 0, Flags =
`Struct` and nothing else, NestingRoot/NestingKey 0, InputAuthority and StateAuthority left at their
default **None (0)**, interest word 0, reserved words 0. Header words (word 0 = the id, never sent):

| word | RuntimeConfig (1) | SceneInfo (3) | PhysicsInfo (4) | PlayerData (1024+) |
|---|---|---|---|---|
| 1 (WordCount low 16, BehaviourCount high 16) | 29 | 33 | 30 | 23 |
| 2 (Type, first u32) | 0 | 0 | 0 | **1** (struct id) |
| 3 (Type, second u32; kind = low 2 bits) | 0 | 0 | 0 | **2** (InternalStruct) |
| 4, 5 | 0 | 0 | 0 | 0 |
| 6 Flags | 32 | 32 | 32 | 32 |
| 7 InputAuthority | 0 | 0 | 0 | 0 |
| 8 StateAuthority | 0 | 0 (§7.2) | 0 | **0 — None, not the player** |
| 9 interest | 0 | 0 | 0 | 0 |
| 10–19 | 0 | 0 | 0 | 0 |

So a `full` record for a struct carries only the non-zero words 1, 6 (and 2, 3 for PlayerData). No
`GlobalObjectInterest` or other flag bit is set on any struct, and the SDK never sets interest bits for
structs. The body sizes come from the SDK's struct layouts: RuntimeConfig 36 bytes (9 words),
PlayerData 12 bytes (3 words); SceneInfo and PhysicsInfo are allocated with 13 and 10 body words.

What the client requires of these values:
- **Flags must contain `Struct`.** The client's struct lookup checks the flag (a debug-build
  assertion), only objects created with the flag enter its struct list (PlayerData is found by scanning
  that list), and a non-struct object with an id ≥ 1024 is queued for a *prefab spawn* — a PlayerData
  without the flag would be treated as a network object.
- **PlayerData's Type must be exactly word2 = 1, word3 = 2.** The client compares the whole 64-bit
  type for equality (not just the kind) both to fire `PlayerJoined` on creation and to find a player's
  data later. The other three structs are found by their fixed ids; their type is ignored (SDK: 0).
- **WordCount** is checked only against the generic limits (≥ 20 and ≤ heap block size, an always-on
  assertion). There is no check that it matches the struct size, but the client reads body fields at
  fixed offsets, so a smaller WordCount would read past the object. Write the exact sizes above.
- **PlayerData.Player must be a real player** (raw > 0) when the struct is created (debug assertion).
- **StateAuthority stays None.** The client's "am I authority" test for SA None is "my PlayerRef equals
  RuntimeConfig.HostPlayer", which is false for every client when HostPlayer = 0, so no client treats
  itself as authority of a struct. Setting PlayerData's SA to the player itself would make that client
  consider itself the struct's authority — the SDK never does this; do not either.

### 7.1 RuntimeConfig — id 1, 29 words (20 + 9)

| word | field | value the server MUST write |
|---|---|---|
| 20 | TickRate.Client | simulation tick rate (Hz) |
| 21 | TickRate.ClientSend | client send rate (Hz) |
| 22 | TickRate.Server | server tick rate (Hz) |
| 23 | TickRate.ServerSend | server send rate (Hz) |
| 24 | ServerMode | `SimulationModes.Server` = 1 |
| 25 | PlayerMaxCount | room capacity |
| 26 | MasterClient | raw PlayerRef of the master client (§11.5) |
| 27 | HostPlayer | 0 (None) — there is no host player |
| 28 | Topology | `Shared` = 2 |

**PlayerMaxCount (read from the SDK):** the SDK server fills it from its own configured player count and
**no client code reads it** — the client takes only the tick rates (time sync), MasterClient and
HostPlayer from this struct. Nothing on the client is an array indexed by player index: players,
player data, connections and authorities are all looked up through dictionaries keyed by PlayerRef,
and the client's own input collection is filled by count, not by index. So a PlayerRef index ≥
PlayerMaxCount (e.g. actor 9 or 10 after rejoins in a 4-slot room) breaks nothing. Write the room's
capacity (Photon MaxPlayers) for fidelity; the value is informational.

Tick rates come from a validated table: the client rate must be one of 8, 10, 16, 20, 24, 30, 32, 50,
60, 64, 100, 120, 128, 240, 256 and each of the other three a divisor from that rate's list (e.g. 64 →
{64, 32, 16}; 32 → {32, 16, 8}). The current host resolves the SDK's `TickRate.Default` selection:
**Client 64, ClientSend 32, Server 64, ServerSend 32**. The SDK's built-in *Shared* selection would be
32/16/32/16. In Shared mode the client takes its timing **entirely** from this struct (its own project
setting is ignored), so this is the server's choice. The client's own preference arrives in its
`NetworkConfigSync` JSON on event 100 (`TickRateSelection`); the cloud plugin presumably honours it
**[inferred]**. Outbound has run live at 64/32/64/32 **[measured]**.

Authority: server only. Created before the first packet to anyone (the SDK creates it at the start of
the first update). A client cannot start its clock until it has received this struct.

### 7.2 SceneInfo — id 3, 33 words (20 + 13)

Opaque to the server: one flags word (low 4 bits = scene count; it also carries a version counter),
eight 32-bit scene refs, eight 16-bit load parameters packed two per word. The server creates it zeroed
at the first tick with header StateAuthority **None** (0) — the measured-working setting for Outbound —
and never writes it again.

Inbound SceneInfo records MUST be parsed and discarded while SA is None (the SDK skips them because a
pure server counts as the authority of SA-None objects). See §12.6 and §19 for the MasterClient
alternative the cloud plugin probably uses.

### 7.3 PhysicsInfo — id 4, 30 words (20 + 10)

Opaque (first word is a float TimeScale; the rest is physics-scene state). Created zeroed at the first
tick with SA None. Written by a client only through the scene-authority path (master client). The SDK
server accepts any client's PhysicsInfo record (structs are copied unconditionally); our server SHOULD
accept it only from the current master client, or ignore it (Outbound has no evidence of using it).

### 7.4 PlayerData — one per player, 23 words (20 + 3)

| word | field | written by |
|---|---|---|
| header 2–3 | Type = InternalStruct, struct id 1 | server |
| 20 | Player — raw PlayerRef of this player | server |
| 21 | Object — the player's avatar NetworkId (`Runner.GetPlayerObject`) | server, from `SetPlayerObject` (§12.5) |
| 22 | Actor — the player's Photon actor number | **server** |

Id: from the server's id counter (1024, 1025, … in creation order). Created when the player's relay
connection is accepted (§11.2); destroyed when it leaves (§11.4). For clients, **receiving a PlayerData
creation is the `PlayerJoined` event and its destruction is `PlayerLeft`** — there is no other join/leave
signal in the simulation layer.

`Actor` is read by the client for `Runner.GetPlayerActorId`, `GetPlayerUserId`, `SetMasterClient`
(which sends ChangeMasterClient with that actor) and the "committed players" filter (players whose
actor is present in the Photon room). The SDK server never writes it and **neither does the current
host** — a gap; our server MUST write it.

Authority: server only. Clients echo every struct back (§9.3); the server MUST NOT let an echo overwrite
Player, Object or Actor.

---

## 8. Replication model (what the server tracks and sends)

There are **no acknowledgements at this layer**. Every packet goes out on the transport's notify
channel; for every packet the transport later reports exactly one of *delivered* or *lost* (07). The
server keeps, per packet sent, an **envelope**: the stamp it was sent at (§8.1), the list of
(object id, previous-sent stamp, is-destroy) it carried, and the reliable messages it carried.

### 8.1 Per object (room-wide)

- `words[WordCount]` — latest value of every word (transform block in wire form, §4.7).
- `stamp[WordCount]` — for each word, the change stamp at which it last changed.
- `changedStamp` — max of `stamp[]`.

The SDK uses the server tick as the change stamp, assigned when it scans objects right before
building packets. Our server MAY use any monotonically increasing counter, with one hard rule: **a
word's stamp MUST be greater than the stamp of every envelope already sent**, or a change could be
considered acknowledged without ever having been sent. The simple way: stamp all changes applied
since the last send round with the stamp of the *next* send round.

A newly created object (client-created or server struct) has every non-zero word stamped; zero words
stay at stamp 0 (the receiver's freshly allocated object is all zeros, so they need not be sent).

### 8.2 Per connection, per object (`ObjectData`)

| field | meaning |
|---|---|
| `status` | `CreatedUnconfirmed` → `CreatedConfirmed` → (on destroy) `DestroyUnconfirmed` ⇄ `DestroyPending` → removed |
| `sentStamp` | stamp of the last packet that carried this object to this connection |
| `ackedStamp` | highest stamp of a *delivered* packet that carried this object |
| `interestFlags` + `interestStamp` | header word 9 as this connection sees it (§14), with its own change stamp |

### 8.3 What goes into a packet for connection C

In order (§4.1): pending reliable/unreliable messages for C (§12); TimeFeedback; destroys pending for
C; RuntimeConfig, SceneInfo, PhysicsInfo (each if it has changes C has not been sent); up to **5**
PlayerData structs with unsent changes, round-robin over all PlayerData; then every other object C
has an `ObjectData` for, until a limit of §4.8 is hit.

An object is written to C when `max(changedStamp, interestStamp) > sentStamp`. The record contains:
- `full` = (`status == CreatedUnconfirmed`); if full, header words 1…19 as in §4.6 field 3, with
  word 9 replaced by C's `interestFlags`;
- every word i (header words too when not full) with `stamp[i] > ackedStamp`, in index order, word 9
  substituting C's flags and C's `interestStamp`.

(The SDK selects words against `ackedStamp` in its default "Full" data-consistency mode, i.e. it
re-sends everything not yet confirmed in every packet until a delivery confirms it. The alternative
"Eventual" mode selects against `sentStamp` and relies on loss handling. Full is the safe choice.)

If no word qualifies, the record is rolled back, `sentStamp` is set to now, and nothing is sent.
Otherwise `sentStamp` = this packet's stamp and the envelope remembers (id, previous `sentStamp`).

Scheduling: the SDK keeps a five-level priority list per connection and rotates objects so that a
packet that fills up does not starve the same objects every time. Our server MAY simply iterate in a
rotating order starting after the last object that fitted; it MUST NOT always restart at the same
object when packets fill.

### 8.4 Delivered / lost

- **Delivered** envelope at stamp S: for each carried object, `ackedStamp = max(ackedStamp, S)`; a
  `CreatedUnconfirmed` object becomes `CreatedConfirmed` (from now on records to C are not `full`).
  For each carried destroy, delete C's `ObjectData` for that id. Release the carried messages.
  Also remember S as C's latest acknowledged tick (used by §10.3).
- **Lost** envelope: for each carried object, lower `sentStamp` and `ackedStamp` to the envelope's
  remembered previous `sentStamp` (if they are higher), so the words are selected again. For each
  carried destroy, re-queue it. Re-queue the carried **reliable** messages at the **front** of C's
  outgoing queue, in their original order, keeping their sequence numbers (§12.4).

An object is **confirmed** to C once any packet that carried it in `full` form is delivered. Until then
every record to C repeats the full header.

### 8.5 Destroys

When an object is destroyed (§9.4, §11.4), for every connection that has an `ObjectData` for it: set
`DestroyUnconfirmed` and queue the id. Writing it moves it to `DestroyPending`; delivery deletes the
`ObjectData`; loss re-queues it. The object's words are freed immediately; it is never written again.
Ids 1–1023 are never destroyed.

---

## 9. Applying what a client sends

### 9.1 Order of processing one inbound packet from connection C

1. Read the header. 2. Read the message block and queue each message (§12.2). 3. Record the packet's
`Tick` for time feedback (§10.3). 4. Read the destroy list (§9.4). 5. Read the object records (§9.2).

### 9.2 Object records

For each record (after parsing per §4.6):
- **Unknown id, `full`**: create the object from the header (WordCount from word 1). Reject (skip) if
  WordCount < 20, the id is reserved (≤ 1023) **[recommended — the SDK does not check]**, or Flags
  has `Struct`. Then apply the data words. C's `ObjectData` for it starts `CreatedConfirmed` (C created
  it, do not echo the creation). **Every other connection** gets a fresh `CreatedUnconfirmed`
  `ObjectData` so the object reaches everyone already in the room (plugin duty 2, §15).
- **Known id, `full`**: the SDK replaces the whole header from the record (WordCount mismatch is only
  logged). Our server SHOULD keep words 1–6 and 8 from creation, take 7 and 10–19 from the record.
- **Known id, delta**: store each received word whose index is 7 or ≥ 10; ignore 0–6, 8, 9.
- **Unknown id, delta**: skip.
- **Struct ids** (RuntimeConfig, SceneInfo, PhysicsInfo, any PlayerData): parse and discard (§9.3).

Authority check: the SDK server applies any client's update to any object. Our server SHOULD apply a
record only if C is the object's current state authority (resolving MasterClient, §11.5) or the record
creates the object; records from other connections are parsed and dropped. This blocks a former
authority's in-flight packets from overwriting the new authority after a transfer. **[deviation from
SDK, believed safe]**

A changed word is stored, its stamp set per §8.1. Nothing is sent back to the authority except what the
SDK does too (next paragraph).

Echo to the authority: the SDK server also schedules the object on C (the creator) and sends C its own
changes back; the client ignores them (it is the authority, so it neither copies the snapshot nor
reacts). Our server MAY skip sending an object's data words to its current authority, provided it
treats them as acknowledged for that connection (so a later loss of authority does not trigger a
resend of everything C wrote itself). Header changes (word 8) MUST still reach C.

Why the echo matters, and the rule that makes skipping it safe (read from the SDK). A client keeps, per
object, a timeline of *received* snapshots; each new received record starts from a copy of the previous
received snapshot (or, if it has never received one for that object, from a copy of its own current
state) and applies the record's words. When C **loses** state authority (a record arrives whose word 8
no longer resolves to C), C overwrites its live state with that received snapshot. If the server never
echoed C's own words, that snapshot holds whatever C last *received* — stale for every word C wrote
while it was authority, whenever C had received any record for the object before (it joined after the
creator, or this is its second authority period). The new authority does not resend words it does not
change, and the server treats them as acknowledged for C, so C keeps the stale values indefinitely.
Rule if the echo is skipped: **in the record that carries the word-8 change to the losing authority,
send every non-zero-stamped word** (reset that connection's `ackedStamp` for the object to 0 when the
authority moves away from it). The client applies all words of a record before it evaluates word 8, so
this delivers the correct state atomically. The alternative is to echo like the SDK.

### 9.3 Struct echoes ("clients clobber the structs")

Shared-mode clients run the same struct-writing step as the server: whenever a struct's words differ
from the client's private shadow copy — which is true after **every** update the client receives,
because a client that is not the struct's authority never refreshes its shadow — it sends the struct
back. So every RuntimeConfig, SceneInfo, PhysicsInfo and PlayerData change the server sends comes back
from every client one send later, possibly stale. The SDK server copies these over its own structs
(SceneInfo excepted), which is what wiped `PlayerData.Object` in live testing **[measured]** (plugin
duty 7). Our server MUST parse and discard inbound RuntimeConfig, SceneInfo and PlayerData records
(PhysicsInfo: §7.3). `SetPlayerObject` (§12.5) is the only legitimate way PlayerData.Object changes.

### 9.4 Destroys from a client

For each id: if it exists, is not reserved, is not a struct, and C is its state authority (the SDK
checks none of these; our server SHOULD), destroy it per §8.5. The destroy is queued to every connection
that has an `ObjectData` for it — including C, which ignores a destroy for an object it already freed.

---

## 10. Ticks, send rate and time synchronisation

### 10.1 The server clock

Server time starts at 0 when the room's simulation starts and advances with wall-clock time. The server
tick is `floor(time · TickRate.Client)` (the server tick *stride* is `Client / Server`, 1 with the
default rates). The SDK advances at most one second's worth of ticks per update call. The server keeps,
for the last `TickRate` ticks, the server time at which each tick was reached (§10.3).

### 10.2 When packets are sent

A send round happens when the tick has advanced by at least `TickRate.Client / TickRate.ServerSend`
ticks since the previous round (every 2 ticks = 31.25 ms at 64/32). In a round, every connected
connection gets **one** packet — except that a connection from which nothing has been received for
≥ 1 s is sent to at most every 0.5 s. A packet is sent even when it carries no records and no
messages: the header Tick and TimeFeedback alone are what keep the client's clock running.

### 10.3 Time feedback

For each packet from C carrying client tick T, the server computes
`offset = expectedTime(T) − now`, where `expectedTime(T)` is the server time at which tick T was
reached if T is in the remembered window, else `T / TickRate`. Offsets are only taken for increasing T.
It also records the interval since the previous packet from C (ignoring intervals under 1 ms; the first
sample is the nominal client send interval). Each series is a sliding window of `max(ClientSend, 5)`
samples; Avg is a smoothed mean, Dev the median absolute deviation. If the server's tick is more than
1 s ahead of the last tick C acknowledged (delivery feedback, §8.4), both series for C are cleared.

Exact statistics (read from the SDK):
- **Samples.** Offset sample = `expectedTime(T) − now`, in seconds, where `now` is the server time of
  the update in which the packet is processed and `expectedTime(T)` is the remembered time at which
  the server reached tick T, else `T × tickDelta`. **Positive = the client's packet for tick T arrived
  before the server reached T, i.e. the client is ahead.** Taken only when T is greater than the
  highest T already sampled for C. Interval sample = seconds since the previous packet from C; an
  interval under 1 ms is not recorded and does **not** restart the interval clock (it folds into the
  next one). The very first packet (and the first after a clear) records the nominal
  `1 / ClientSend` instead.
- **Window.** Each series is a ring of the last `N = max(ClientSend, 5)` samples (ClientSend from the
  tick-rate resolution, 32 at 64/32/64/32), oldest dropped first.
- **Avg = exponential smoothing over the window with α = 0.5**, recomputed from scratch each time:
  start with the oldest sample in the window, then for each newer sample in order,
  `acc = 0.5·acc + 0.5·sample`. Empty window → 0.
- **Dev = 1.4826 × median(|sample − median(window)|)** — the scaled median absolute deviation. Median
  of an even count is the mean of the two middle values; empty → 0.
- **Clear** (both series and the interval clock) when the server tick is more than 1 s ahead of C's
  last acknowledged tick; the highest sampled T is *not* cleared.

What the client does with them (read from the SDK): it starts from (0, 0.025, 0, 0.025) and blends
each received feedback into its copy with factor 0.5 per packet. Its **target input offset** is
`round(RecvDeltaAvg to a multiple of its send interval, at least one interval) × redundancy +
z(quantile) × max(RecvDeltaDev, its RTT median-abs-dev)` — about one to two send intervals plus a
jitter margin. Its **believed current offset** is OffsetAvg plus the clock corrections it has applied
since the latest server tick it has seen (older corrections are assumed to be reflected in the
feedback already). It speeds its input clock up when the believed offset is below target (by 1/8 of the
error when the error exceeds 50 ms, plus a proportional time-scale term), and resets its clocks if the
error is below −0.5 s or above 1 s. OffsetDev is only reported in its statistics.

**All-zero feedback is not neutral** (deduced from the code above, not tested): because the
believed offset falls back to 0 each time a server tick overtakes the client's corrections, the client
always sees itself behind target and keeps running its clock fast, so it drifts ahead until it hits
the "at most one second of ticks ahead of the latest server tick" cap (§10.4) and stays pinned there.
Nothing breaks outright in Shared mode (nothing is predicted or rolled back; the error never reaches the
reset thresholds), but the client's message ticks then run ~1 s ahead and its simulation is paced by the
cap. Send the real values.

### 10.4 What the client does with the server tick

- Before it has RuntimeConfig, a client does not start its clock. On the **first** packet that brings
  RuntimeConfig it resets its clock from (server tick, RTT) and starts simulating.
- It never runs more than **one second of ticks ahead of the latest server tick it received**. If the
  server tick stops advancing (or packets stop), the client stops simulating ~1 s later.
- If consecutive server packets jump by more than 1 s of ticks, the client treats it as a connection
  stall: it clamps its RTT estimate to 0.15–0.25 s and resets its clock.
- So the server tick MUST advance steadily in real time and every connection MUST keep receiving
  packets at the send rate. A room loop that stalls (GC pause, blocked listener thread) stalls every
  client a second later — see the listener-thread budget lesson in `fusion-shared-server.md` and memory.

### 10.5 Message tick gating

A receiver delivers a message only once its own tick has reached the message's Tick, unless
`NOT_TICK_ALIGNED` is set. Clients run ahead of the server, so the SDK server holds client messages for
roughly one one-way latency before it processes them; and a message forwarded with the server's tick
is deliverable on the receiving client immediately. Our server MAY process and forward client messages
immediately on receipt (it has no simulation to align with); per-source order MUST be preserved.

---

## 11. Player lifecycle

### 11.1 Identity chain

| layer | identity | who assigns |
|---|---|---|
| Photon room | actor number | GameServer |
| Fusion session (event 100) | 8-byte UniqueId, PlayerRef index | our server: UniqueId = `UniqueIdForActor(actor)`, index = actor |
| relay transport | the UniqueId presented in the Connect command; connection counter | client presents; server assigns counter (07) |
| simulation | PlayerRef (raw = actor + 1), PlayerData struct | server |

The server MUST register (UniqueId → actor, PlayerRef) when the Fusion **Join** is answered, before
the relay Connect can arrive. A Connect whose UniqueId is unknown is answered **Waiting** (07) and the
client retries. Other clients learn each other's (actor, PlayerRef, UniqueId) through the event-100
`PlayerRefMapping` message (session layer, handled in `GameServerHandler`); in the simulation layer
they learn each other only through PlayerData structs.

### 11.2 Join

When a relay connection is accepted for a registered UniqueId:
1. Create the connection state; give it its connection counter (07).
2. Create the player's PlayerData struct (§7.4: Player, Actor; Object = 0) with a server id. Every
   connection, including the new one, will receive it (structs are sent to all).
3. Give the new connection a `CreatedUnconfirmed` `ObjectData` for **every existing non-struct object**
   (the join sweep). Structs need nothing: they are written to every connection by §8.3.
4. Recompute the master client (§11.5).

What the new client receives first: RuntimeConfig (needed before anything else happens client-side),
SceneInfo, PhysicsInfo, PlayerData structs (5 per packet), then the world objects in full form. The
existing clients receive the newcomer's PlayerData → their `PlayerJoined` fires.

### 11.3 After join (client behaviour the server relies on)

The client spawns its avatar (a full record from it), then sends `SetPlayerObject` (§12.5). Outbound's
host only starts its join synchronisation (a burst of ~100–134 RPCs) once `GetPlayerObject(joiner)` is
non-null on the host, i.e. once the joiner's PlayerData.Object has reached it **[measured]**.

### 11.4 Leave

Triggered by the Photon actor leaving the room, or the relay connection closing / timing out.
1. Close the relay connection (07) if still open; drop its queues and `ObjectData`.
2. Destroy the player's PlayerData struct (§8.5) → every remaining client's `PlayerLeft` fires.
3. **For every object whose StateAuthority is the leaver: if its Flags have
   `DestroyWhenStateAuthorityLeaves`, destroy it; otherwise set StateAuthority = None** so the object
   can be claimed (§12.6). Objects with StateAuthority = MasterClient (−1) are left alone; they follow
   the new master automatically. **[inferred from Photon's documented Shared-mode behaviour — the SDK
   server does none of this, and no client code does it either]**
4. Recompute the master client (§11.5).

Leave ordering **[measured]** (black-box, sharedclient harness against the clean-room server): a stock
2.0.8 client that shuts down sends its relay Disconnect (reason Requested) roughly 15–30 ms **before**
its Photon OpLeave. Running the cleanup on that Disconnect makes the other clients see PlayerLeft (the
PlayerData destroy) before the Photon Leave event that moves the Photon master, so for that moment
`IsSharedModeMasterClient` and `RuntimeConfig.MasterClient` disagree. Server rule: a **Requested**
relay disconnect from an actor still registered in the Photon room defers the cleanup until that
actor's Photon leave, with a grace of at most 1 s; a **timeout** disconnect is cleaned up immediately.
With that, the remaining clients see PlayerLeft, the Photon master change and the RuntimeConfig
MasterClient change together. Session-layer counterpart: when the master leaves, `GameServerHandler`'s
Leave event carries MasterClientId (parameter 203) = the new lowest actor.

Same-packet delivery is fine (read from the SDK): a packet may carry the old master's PlayerData
destroy and the RuntimeConfig.MasterClient change together. While reading, the client only frees the
struct and queues PlayerLeft. It evaluates "am I the master client now" once per update, **after** the
packet is applied, by comparing RuntimeConfig.MasterClient with itself. At that point it re-activates
its master-client objects from their latest received snapshots and raises the authority callbacks.
Neither step depends on the leaver's PlayerData existing, so nothing in the client objects to receiving
both in one packet, or in either order.

Note `FusionServerFactory.DisconnectPlayer`'s comment says the SDK "frees its
DestroyWhenStateAuthorityLeaves objects"; it does not — the SDK only deletes the PlayerData struct.
The current host therefore leaves a leaver's objects in the world under a dead authority (they become
claimable only because its authority check treats a departed player as "no authority").

### 11.5 Master client

Two different "master client" notions exist on the client, and they should agree:

| notion | source | used for |
|---|---|---|
| `Runner.IsSharedModeMasterClient`, `IsSceneAuthority`, may spawn master-client objects | the **Photon room's** master (Realtime `LocalPlayer.IsMasterClient`, room property 248) | scene loading, physics info, spawning objects with SA = MasterClient (−1) |
| `RuntimeConfig.MasterClient` | the server (§7.1) | resolving SA/IA = −1: "player P is authority of a MasterClient object iff RuntimeConfig.MasterClient == P"; on change the client re-activates every master-client object locally and raises StateAuthorityChanged / IsSimulatedChanged |

Server rule: `RuntimeConfig.MasterClient` = raw PlayerRef of the Photon room's master actor, updated on
every join, leave and master change. Our GameServer advertises the lowest actor as master (property 248),
and the current host elects the lowest actor too, so they agree.

A client that is Photon master may ask to hand mastership to another player by sending the event-100
`ChangeMasterClient` message with that player's **actor** (taken from its PlayerData.Actor — another
reason Actor must be filled). The server then changes the Photon room master and RuntimeConfig together
**[inferred]**.

Measured quirk to preserve **[measured]**: the current host writes MasterClient only from its join/leave
hooks, and RuntimeConfig does not exist yet when the first player is added, so a **solo host sees
MasterClient = None** until a second player joins; then it becomes the lowest actor. A build that set it
from tick 1 *and* gave SceneInfo/PhysicsInfo to the master made Outbound's host stop sending its join
sync. Which of the two changes broke it is not known (§19). Plausible mechanism **[inferred]**: the
None→host transition makes the host's client re-activate its master-client objects and raise
StateAuthorityChanged(gained), which may be what kicks Outbound's world logic for the new player.

---

## 12. Simulation messages

### 12.1 Shapes

- **RPC** (flags without USER_MESSAGE, STATIC, INTERNAL): payload starts with an 8-byte `RpcHeader`
  — Object (NetworkId, u32), Behaviour (u16 index into the object's networked behaviours), Method (u16)
  — followed by game-defined arguments (opaque).
- **Static RPC** (STATIC): RpcHeader with Object = 0, Behaviour = 0, Method = static RPC key.
- **User message** (USER_MESSAGE): opaque bytes, no RpcHeader.
- **Internal** (INTERNAL): payload = i32 type (little-endian) + a fixed struct (§12.5). From clients
  always reliable, flags INTERNAL | TARGET_SERVER (96), Target None.

### 12.2 Receiving (per source connection)

Source: the server sets Source = C's PlayerRef for every message from C (clients send None).
Reliable messages carry a per-connection sequence starting at 1. The server keeps `lastDelivered[C]`:
- sequence ≤ `lastDelivered` → duplicate, drop;
- already queued with the same sequence → duplicate, drop;
- otherwise insert in sequence order.
Deliver from the head while the head's sequence is `lastDelivered + 1` (then increment). A gap waits
for the sender's resend. Unreliable messages have no sequence and are delivered as they arrive.

### 12.3 Relay rule (plugin duty 5)

For each delivered message from C that is not INTERNAL and not DUMMY:

| flags | action |
|---|---|
| TARGET_PLAYER, Target = P | forward to P only, if P is connected and P ≠ C |
| TARGET_SERVER | consumed by the server; not forwarded (our server ignores non-internal ones) |
| neither | forward to every connected player except C |

The server does not know RPC source/target authority masks (they live in game code); receiving clients
apply them and drop what is not for them, so over-forwarding is correct.

A forwarded copy has: same payload and bit length, same flags (REMOTE cleared on the wire), Source = C,
**Target cleared to None on the wire while TARGET_PLAYER stays set** (the receiving client then fills in
its own PlayerRef), Tick = the server's current tick, and a **new sequence from the destination
connection's own outgoing counter** (unreliable: none). Forwarding happens in delivery order, so each
destination sees each source's messages in the source's order.

Create-before-RPC hazard: a client that receives an RPC whose object it does not have yet drops it
(object unknown) or retries it (object known, instance not yet spawned). The SDK gives no ordering
guarantee beyond "messages are read before object records of the same packet". Our server SHOULD NOT
forward an RPC to P before the target object's creation has been written to P at least once (hold it
in P's queue until then) **[recommended]**.

### 12.4 Sending reliably

Per destination connection: `outSeq` (starts at 0; each reliable message takes `++outSeq` when queued)
and an ordered outgoing queue. Messages are written from the head of the queue (§4.3) and move into the
packet's envelope. Delivered → released. Lost → put back at the head of the queue in original order
with their original sequences. Unreliable messages are written once and never retried.

What the client does with the server's sequences (read from the SDK). A client has exactly one
connection (to the server), so it keeps **one** inbound sequence space for everything the server sends
reliably — forwarded RPCs from every source and anything the server originates — and expects it to
start at **1** and increase by exactly 1. On receipt it drops a reliable message whose sequence is ≤ the
last delivered one or equal to one already queued, and inserts the rest in sequence order (unreliable
messages are appended at the tail). Delivery walks the queue **from the head** and stops at the first
message that:
- is reliable and not exactly `last delivered + 1` — **a gap stalls the whole queue**, including
  unreliable messages queued behind it, until the missing sequence arrives; a sequence that is never
  sent stalls that client's messages forever;
- has a Tick greater than the client's current tick and no NOT_TICK_ALIGNED flag;
- is a reliable RPC whose target object is known but not yet spawned (the handler asks for a retry):
  it goes back to the head and blocks everything behind it until the next tick's attempt succeeds.
So `outSeq` per destination MUST be dense: allocate a sequence only for a message that will really be
queued, never skip or reuse one, and requeue lost ones with their original numbers.

### 12.5 Internal messages (plugin duty 6)

Payload = `i32 type` then the struct below (all little-endian). Clients only ever send these four, and
the SDK's server-side handler for all of them is an empty logging stub — the cloud plugin implements
them, and so must we. They are processed in the source's reliable order (§12.2) and never forwarded.

| type | name | payload after type | sent by the client when | server action |
|---|---|---|---|---|
| 3 | SharedModeSetAlwaysInterested | Object (u32), Interested (i32: 1/0), Player (i32 **index**) | `SetPlayerAlwaysInterested` with the client's AOI enabled | add/remove Object to that player's always-interested set (§14). Without AOI: no-op. |
| 4 | SharedModeRequestStateAuthority | Object (u32), Acquire (i32: 1 request / 0 release) | `RequestStateAuthority` (only if the client is not already SA) / `ReleaseStateAuthority` (only if it is) | §12.6 |
| 6 | SetPlayerObject | Object (u32) | `Runner.SetPlayerObject(LocalPlayer, obj)` — the client must be SA of `obj` | PlayerData(source).Object = Object; remember it (§9.3) |
| 7 | SetAreaOfInterest | Center (3 × f32), Radius (f32, clamped by the client to ≤ 300) | `AddPlayerAreaOfInterest` for itself | AOI only; ignore without AOI |

Type values 1, 2 and 5 are unused in this build. Unknown types: log and drop.

`SetPlayerObject` is load-bearing for Outbound **[measured]**: without it the joiner's PlayerData never
links its avatar and the host never runs its join sync.

### 12.6 State authority rules

Resolve the current authority first: SA raw −1 (MasterClient) means `RuntimeConfig.MasterClient`.

**Request (Acquire = 1) from player R for object X:**
- X unknown, reserved or a struct → ignore.
- Resolved current SA is R → nothing.
- X's Flags have `AllowStateAuthorityOverride` → grant.
- Current SA is None, or a player no longer in the room → grant.
- Otherwise → deny (no message; the client simply never becomes SA).
- Objects whose SA is MasterClient (−1) are **not** claimable by request; their authority moves only
  with the master. (`AllowStateAuthorityOverride` is never set on master-client objects by the client.)

**Release (Acquire = 0) from R:** if the resolved SA is R and X's SA is not −1 → set SA = None.

**Grant / release effect:** write header word 8 and stamp it. The next packets carry word 8 to every
connection: the old authority sees SA ≠ itself and stops simulating/sending X; the new authority sees
SA = itself, adopts its last received snapshot as its own state and starts sending changes. There is no
explicit reply message; header word 8 *is* the reply.

What the gaining client does, exactly (read from the SDK): when a record's word 8 resolves to the local
player and the object's current SA does not, the client copies the just-received snapshot into its live
state, its previous-tick state **and its send shadow**, marks the object active for sending, and raises
IsSimulatedChanged / StateAuthorityChanged(true). Because the shadow equals what it received, it then
sends only words it changes from that point on. Nothing else is needed from the server — no full
resend, no message — provided the record carrying word 8 also carries every word the client has not
yet confirmed, which the "Full" selection of §8.3 already guarantees (every record re-sends all words
newer than the connection's `ackedStamp`). The losing client: §9.2 (it adopts its received snapshot).
The client's own per-object replication status is unaffected (an object received via a `full` record
is already "confirmed" on the client's side), so its first update after taking over is a delta record.

These rules follow Photon's public description of Shared-mode authority **[inferred]** and match the
current host's handler, with one correction: the host's "no live authority" test treats an SA of −1 as
a departed player (it is not a valid player index) and would hand master-client objects to any requester.

Input authority (word 7) is not managed by the server in Shared mode: the SA client writes it (e.g.
`AssignInputAuthority`) and it replicates like any header word.

---

## 13. Reliable data transfer (`ReliableDataTransferModes`)

`Runner.SendReliableDataToPlayer` / `…ToServer` travel as transport **reliable-data** packets (07), not
as simulation messages. Each carries a `ReliableId` with, among others, `Source` (sender index),
`Target` (destination player index, or −1 for "the server"), a key and slice/total lengths.

Server duty (default modes = ClientToServer | ClientToClientWithServerProxy):
- `Target` = −1 → data for the server: accept and discard (no consumer in a relay server).
- `Target` = a player index other than −1 → **re-send the same ReliableId and slice** to that player's
  connection over the reliable channel; if the player is not connected, drop it.
- A client sending proxied data when the proxy mode is disabled is disconnected by the SDK; our server
  always allows it.

The server does not reassemble or interpret the data. Whether Outbound uses this path is unknown.

---

## 14. Interest management

With the default replication feature set (Scheduling, no area of interest) — which the current host
uses — the server sends **every object to every connection**, and that is a valid simplification for
Outbound.

Header word 9 is per recipient: bit 1 `InAreaOfInterest`, bit 2 `ForceInterest`. Without AOI the SDK
sets `InAreaOfInterest` (1) on every non-struct object's `ObjectData` when it is created, and 0 on
structs; for an object whose header Flags have `GlobalObjectInterest` (1) it also sets `ForceInterest`
(2), giving 3. Our server SHOULD send 1 (3 for global-interest objects) in word 9 for every object (0
for structs), stamped at creation. A
client with AOI enabled reads it to decide interest and raises ObjectEnterAOI/ExitAOI when it changes;
a client without AOI ignores it.

If a client's config has AOI enabled it will send `SetAreaOfInterest` and `SharedModeSetAlwaysInterested`;
with "everything to everyone" the server MAY ignore both (every object is already "in area"). Real AOI
(server-side cells from each object's main transform, per-player cells from SetAreaOfInterest, exits
when objects leave) is out of scope for the minimal server.

---

## 15. The plugin duties as normative requirements

`fusion-shared-server.md` lists seven things the cloud plugin does around the SDK server. Restated as
requirements on our own server (several stop being "workarounds" and become plain design):

| # | live finding | requirement |
|---|---|---|
| 1 | SDK needed a connection object for `PlayerRef.None` | No equivalent needed: our server has no local player and MUST NOT model one. Reading a client's object records MUST NOT depend on per-"local player" state. |
| 2 | client-created objects only reached later joiners | On creating an object from client C, the server MUST schedule it (`CreatedUnconfirmed`) on every other connection immediately (§9.2), and on every later joiner (§11.2). |
| 3 | heap page too small for a 9268-word object | The server MUST accept objects of any WordCount 20…32767 (§4.8) and SHOULD bound total room memory instead. |
| 4 | nobody writes `RuntimeConfig.MasterClient` | The server MUST write it, in step with the Photon room master (§11.5). |
| 5 | nobody forwards RPCs | The server MUST relay messages per §12.3. |
| 6 | internal messages swallowed | The server MUST implement the four internal messages (§12.5) and the authority rules (§12.6). |
| 7 | client struct echoes clobber PlayerData | The server MUST own all structs and discard inbound struct records (§9.3). |

Duties found while writing this spec, not in that list:

| # | requirement |
|---|---|
| 8 | Write `PlayerData.Actor` (§7.4). |
| 9 | On leave, destroy the leaver's `DestroyWhenStateAuthorityLeaves` objects and release the rest (§11.4). |
| 10 | Assign each relay connection a unique connection counter — clients build their NetworkIds from it (§6.2, 07). |
| 11 | Keep the server tick advancing and packets flowing at the send rate, even when idle (§10.2, §10.4). |
| 12 | Proxy client-to-client reliable data (§13). |

---

## 16. Minimal server

The smallest server that lets Outbound clients join, see each other and run RPCs. Each step is testable
on its own; steps 1–4 can be checked without a game by feeding captured or SDK-generated packets.

1. **Bit codec** (shared with 07): unary-prefix block varints, zig-zag, `v7`, pad / aligned bytes,
   64-bit header read/write. Unit-test against the SDK locally (the stock 2.0.8 DLL can encode and
   decode test buffers in a test project, the same way the Photon3Unity3D oracle is used).
2. **Packet codec**: header (§4.2), message envelopes (§4.3), TimeFeedback writer (§4.4), destroy list
   (§4.5), object records incl. header-present path and skip path (§4.6), transform block in wire form
   (§4.7). Round-trip tests; then decode a real client packet captured from the current host.
3. **Room state**: object table (words, stamps, header fields of §5), id counter from 1024, the four
   structs (§7) with server-only authority.
4. **Per-connection replication**: `ObjectData` (§8.2), packet builder (§8.3) with structs first,
   envelopes, delivered/lost handling (§8.4), destroys (§8.5). Rotating iteration instead of the
   five-level priority list.
5. **Inbound application** (§9): creation + schedule-to-others, deltas, SA check, struct echoes
   discarded, destroys.
6. **Room loop** (§10): wall-clock tick at RuntimeConfig rates, send rounds every
   `Client/ServerSend` ticks, idle-connection throttle, TimeFeedback series.
7. **Player lifecycle** (§11): registration from the Join, PlayerData on accept (Player, Actor), join
   sweep, leave cleanup incl. duty 9, MasterClient in RuntimeConfig (preserving the solo quirk, §11.5).
8. **Messages** (§12): per-connection in/out reliable sequencing with requeue on loss, relay rule,
   SetPlayerObject, RequestStateAuthority/Release, ignore SetAlwaysInterested/SetAreaOfInterest.
9. **Reliable-data proxy** (§13), if 07's transport exposes reliable packets to this layer.
10. Wire into `IFusionSessionHost` in place of `FusionSharedServer`, keeping the same
    `PlayerJoined` / `PlayerLeft` / `FeedRelay` / `Tick` surface and diagnostics (`objects:` dump,
    per-RPC log line).

Can be skipped for Outbound: AOI and always-interested sets (§14); the priority scheduler; the
"Eventual" data-consistency mode; the post-loss single-fragment send limit; host migration (the SDK's
host-migration snapshot path is ClientServer-host only); input handling (Shared sends no inputs);
server-side RPC invocation and authority masks; ChangeMasterClient (session layer) unless a title
calls `Runner.SetMasterClient`; PhysicsInfo/SceneInfo writes (keep SA None).

Size estimate (C#, excluding the transport of 07 and tests): packet codec ~400 lines, room/object
state + structs ~250, per-connection replication + envelopes ~350, inbound application ~200, room loop
+ time feedback ~150, lifecycle + master client ~150, messages + internal + authority ~300 — about
**1,600–2,000 lines**, plus roughly as much again in tests.

---

## 17. Corrections to existing docs and code

1. `docs/fusion-shared-server.md`, "The one required binary patch": does not apply to Outbound or to
   the libs in `server/fusion-libs/` (stock 2.0.8; no PluginVersion on the 2.0.x wire). The patch and
   `tools/fusion-patcher` target 2.1.x only (§1).
2. `docs/fusion-shared-server.md` says the libs come from "a Fusion 2.1.x Unity package", and
   `docs/fusion-protocol-map.md` says its map "was transcribed from Fusion 2.1.2" — but the working copy
   it names (`scratch/fusion/src/`) is the **2.0.8.1402** decompile, the same build as `fusion-libs/`.
   The session messages it lists (incl. PlayerRefMapping, ChangeMasterClient, ProtocolMessageVersion
   V1_6_0) are present in 2.0.8 too, so the map's content holds for Outbound; only the version label is
   off.
3. `FusionServerFactory.DisconnectPlayer` comment: the SDK does **not** free
   `DestroyWhenStateAuthorityLeaves` objects on leave — only the PlayerData struct (§11.4).
4. `FusionServerFactory` `HandleInternal` (RequestStateAuthority): the "no live authority" test
   (`!PlayerValid(cur)`) is true for SA = MasterClient (−1), so any player can take a master-client
   object (§12.6).
5. Nothing in the current host writes `PlayerData.Actor` (§7.4).
6. `FusionServerFactory` comment on SceneInfo ("ReadObjectUpdates treats the server as their authority
   and SKIPS every SceneInfo") is accurate only for SceneInfo; RuntimeConfig, PhysicsInfo and PlayerData
   echoes **are** applied by the SDK server (that is duty 7's root cause).

---

## 18. Fusion 2.1.x — known differences (not specified here)

Seen in the 2.1.2 decompile only, recorded so nobody reuses this document for a 2.1 title unchanged:
header word 10 becomes `PluginVersion` (u32) and word 9 `PlayerFlags`; the object record carries a
PluginVersion varint before the word list that clients write and the cloud plugin reads but does not
write back (the asymmetry `tools/fusion-patcher` reproduces); the replicator's write/read paths were
restructured. A 2.1.x server needs its own pass over that wire.

---

## 19. Open questions / unverified

1. **Outbound's own tick-rate / replication config.** Its `NetworkConfigSync` JSON (logged at Debug by
   `GameServerHandler`) has not been saved anywhere. Needed: `TickRateSelection`, `ReplicationFeatures`
   (does it enable AOI? — decides whether `SharedModeSetAlwaysInterested` ever arrives),
   `ObjectDataConsistency`, `ReliableDataTransferModes`. Until then: 64/32/64/32, no AOI, as measured.
2. **What the cloud plugin puts in RuntimeConfig** (tick rate source, ServerMode, PlayerMaxCount) and
   whether it honours the first client's `TickRateSelection`. [inferred: yes]
3. **SceneInfo / PhysicsInfo authority.** The client asserts, when the Photon master consumes an initial
   scene, that SceneInfo's SA is MasterClient (−1) or itself — strong evidence the cloud plugin creates
   them with SA = −1. Outbound evidently never takes that path (it drives scenes through room
   properties). Which half of the reverted change (MasterClient from tick 1 vs. SceneInfo/PhysicsInfo SA
   = master) broke Outbound's join sync is unknown; test them separately before changing either.
4. **Solo-host MasterClient = None.** Is the None → host transition (and the StateAuthorityChanged it
   triggers on the host's master-client objects) actually what makes Outbound's host start its join
   sync? Is the 9268-word world object a master-client object (SA −1)? If so, a solo host is not its
   authority until the second player arrives — worth confirming with the `objects:` dump.
5. **Leave semantics (§11.4)** are from Photon's public docs, not from plugin code or a capture. Not
   yet exercised with Outbound (a joiner leaving and returning works today without them).
6. **Authority rules (§12.6)** — same provenance; pickups (the first title feature that needs them)
   are still unverified on screen.
7. **Does Outbound use reliable data (§13), static RPCs, user messages, `NOT_TICK_ALIGNED` or
   unreliable RPCs?** The per-RPC log line would show the flags.
8. **Zero TimeFeedback.** Resolved by reading the client's time provider (§10.3): not neutral — the
   client would drift to the 1 s-ahead cap. Untested live; compute the real values.
9. **Transform-block quaternion codec** lives in `Fusion.Common` and was not studied; storing the block
   in wire form (§4.7) avoids needing it. If the server ever needs real positions (AOI), it must be
   specified then.
10. **Echo to the authority (§9.2).** The SDK echoes an authority's own changes back. Skipping it is
    safe only together with the "resend everything to the losing authority" rule in §9.2 (the client
    adopts its last *received* snapshot when it loses authority); untested live.
11. **Holding RPCs until the target object's creation has been sent (§12.3)** is a recommendation with no
    observed failure behind it; the SDK does not do it.
12. **`Smoothed` / median-absolute-deviation definitions** are now given exactly in §10.3. The one piece
    not read is the float → fixed-point step of §4.4 (it lives in `Fusion.Common`, which is not in the
    decompile set); the ×256 scaling and the `vb(6)` block size are confirmed.
13. The ~1 s idle throttle (§10.2) uses the transport's "connection idle time"; its exact definition
    belongs to 07.
14. **Server note — oversized unfragmented packets go reliable (deviation from the SDK's delivery
    choice; cross-reference 07 §1.2 and §7).** Our Photon layer cannot send an *unreliable* Photon
    datagram larger than 1200 bytes (the client's UDP receive buffer). Wrapping a Fusion datagram in
    event 101 costs 45 bytes (12 Photon header + 16 unreliable send command + 17 event/parameter
    framing), so a Fusion datagram over **1155 bytes** is raised as a **reliable** event (channel 1,
    split by Photon's own reliable fragmentation) instead of unreliable on channel 0. In practice this
    affects only unfragmented notify packets of **1156–1280 bytes**; fragment pieces (≤ 1136 bytes) and
    reliable-channel slices (≤ 1150) stay within the limit and keep the SDK's delivery modes.
    Client-side effect, read from the SDK: none that breaks anything. The client's Photon receive hands
    every event 101 to the Fusion transport regardless of how it was delivered, and the SDK itself
    already sends every fragment piece as a reliable channel-1 event, so a whole notify packet on that
    path is ordinary. Ordering is decided only by the notify sequence: channel-0 unsequenced and
    channel-1 reliable events are not ordered against each other by Photon, and the notify receiver
    discards any packet whose sequence is not newer than the last one it accepted. So a promoted packet
    that is overtaken by a later unreliable one (typically because a Photon resend delayed it) is
    silently dropped on arrival, missing from the client's ack mask, and reported **lost** to the server
    — normal loss handling (§8.4) resends its contents. Cost: a lost channel-1 command delays every
    later channel-1 event (Photon head-of-line), so under loss several promoted packets in a row can be
    wasted this way; correctness is unaffected. Keep the unreliable events **unsequenced** on channel 0
    as the SDK does: sequenced unreliable commands on the same channel as the reliable ones would be
    held behind, or discarded relative to, the reliable sequence by the Photon client. An equivalent
    alternative is to fragment such packets into two notify fragments (07 §7 — the receiver accepts
    any piece size), which travel the same reliable path anyway.
