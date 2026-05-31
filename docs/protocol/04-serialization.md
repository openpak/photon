# Serialization

Photon serializes operation/event parameters with **GpBinary** (Photon's type-tagged binary
format). Tags and custom-type registrations below are extracted from `Photon3Unity3D.dll`
and `Assembly-CSharp.dll`.

## GpBinary type tags (`GpType`, byte)

Tags are ASCII-letter codes written before each value.

| Tag | Dec | Type | Tag | Dec | Type |
|---|---|---|---|---|---|
| `*` | 42 | Null | `i` | 105 | Integer |
| `D` | 68 | Dictionary | `n` | 110 | IntegerArray |
| `a` | 97 | StringArray | `k` | 107 | Short |
| `b` | 98 | Byte | `l` | 108 | Long |
| `c` | 99 | **Custom** | `o` | 111 | Boolean |
| `d` | 100 | Double | `p` | 112 | OperationResponse |
| `e` | 101 | EventData | `q` | 113 | OperationRequest |
| `f` | 102 | Float | `s` | 115 | String |
| `h` | 104 | Hashtable | `x` | 120 | ByteArray |
| | | | `y` | 121 | Array |
| | | | `z` | 122 | ObjectArray |

A `Custom` (`c`, 99) value is followed by a 1-byte **custom type code** then the type's
own serialized bytes.

## Registered custom types (`PhotonPeer.RegisterType`)

From `Assembly-CSharp` (the type-code is the byte after the `Custom` tag):

| Code | ASCII | Type | Role |
|---|---|---|---|
| 65 | `A` | `AffixPacket` | item/enemy affix data on damage events |
| 67 | `C` | `Color` (Unity) | |
| 68 | `D` | `DamagePacket` | **the damage model** (see layout below) |
| 70 | `F` | `Factions` | faction/allegiance |
| 82 | `R` | `Rect` (Unity) | |
| 88 | `X` | `XPPacket` | experience grants |

## `DamagePacket` wire layout (41 bytes, fixed)

From `SerializeDamagePacket`. **Every field is authored by the sending client** — the core
anti-cheat concern:

| Offset | Field | Type | Notes |
|---|---|---|---|
| 0 | Damage | int32 | damage amount (client-decided) |
| 4 | Originator | int32 | source PhotonView/actor |
| 8 | Faction | int32 | enum |
| 12 | Location.x | float32 | impact point |
| 16 | Location.y | float32 | |
| 20 | Location.z | float32 | |
| 24 | Level | int32 | |
| 28 | Type | int32 | damage type enum |
| 32 | TypeWeapon | int32 | weapon type enum |
| 36 | combined | int32 | bitfield: bit0 Crit, bit1 WeakPoint, bit2 Returnable, bits3–5 AggroMult (0–7) |
| 40 | (padding/1 byte) | | serializer writes 41 bytes total |

> A server enforcing authority (Phase 3/4) must recompute `Damage`, `Crit`, and
> `WeakPoint` from authoritative weapon/target state rather than trusting these fields.

## `OnPhotonSerializeView` payloads (continuous sync)

Stream call order per component, from [`generated/serialize_views.csv`](generated/serialize_views.csv).
`SendNext` (write on owner) / `ReceiveNext` (read on remotes) appear in field order:

| Component | Sent values | Likely meaning |
|---|---|---|
| `NetworkSyncPosition` | 7 × SendNext | position (xyz) + rotation + velocity components |
| `BuffManager` | 2 × SendNext | buff id + stacks/duration |
| `NetworkSyncEnemyStats` | 2 × SendNext | health + state |
| `GunAnimationController` | 2 × SendNext | anim state + flags |
| `NetworkSyncTurretHead` | 1 × SendNext | aim angle |
| `NetworkSyncHealth` | 1 × SendNext | health |

These are delta-streamed at `PhotonNetwork.SerializationRate`; a server must accept,
validate, and rebroadcast them (Phase 2 relay, Phase 3 validation).
