# Fusion Session Join Flow (Outbound)

How Outbound's client joins a friend's session, reconstructed from the game's
own decompiled code (not from live captures), so the server can be built to
match without guessing. Source: the full IL2CPP decompile map of Outbound 0.6.0
(`/mnt/media/research/outbound/decomp`, built per
`legacy-docs/IL2CPP-GHIDRA-WORKFLOW.md` Step 5). All RVAs below are
`.text`-relative (== `dump.cs` `RVA:` comments).

## The client is stock Photon Realtime v4, Fusion fork

Outbound's networking is `Fusion.Photon.Realtime.LoadBalancingClient` — the
standard Photon Realtime v4 client with the full documented method set
(`ConnectToNameServer`, `OpGetRegions`, `ConnectToRegionMaster`,
`CallAuthenticate`, `OpJoinLobby`, `OpJoinRoom`, and the
`OnOperationResponse`/`OnStatusChanged`/`OnEvent` jump-table dispatch at
0x217fc10 / 0x2182110 / 0x2183280). Consequence: the whole join is the
documented LoadBalancing sequence. Only two things are game-specific, and both
are pinned from the binary below — the **GameList room-property contract** and
the **session-name identity**.

## Sequence (each step is one op the server must answer)

The joiner's `NetworkManager.Join(code, waitForRoomToBecomeAvailable)` (RVA
0x1BF76C0) sets the Fusion session name to the code and starts a Client-mode
`StartGame`. Fusion then drives:

1. **NameServer** — `OpGetRegions` (220) → region list;
   `Authenticate` (230) with the custom-auth token → Master address + **Secret**
   (parameter 221), which the client echoes on every later hop.
2. **Master** — reconnect, `Authenticate` (230) again (Secret echoed) → ready.
3. **`OpJoinLobby` (229)** with **P213 (LobbyName) = the session code**. The
   server answers, then must raise the **GameList event (230), parameter 222 =
   `{ roomName → propertiesHashtable }`**.
4. The client parses each room via `RoomInfo.InternalCacheProperties` (below)
   and caches it. When a room named after the code is present, open and
   visible, Fusion's matchmaking sends **`OpJoinGame` (226)** with that name →
   Master returns the GameServer address.
5. **GameServer** — reconnect, `Authenticate` (230), `OpJoinGame` (226) again →
   EnterRoom success (actor list + room properties) → **InRoom**, second actor
   joins.

`waitForRoomToBecomeAvailable` is why step 4 blocks rather than erroring: the
client sits in the lobby watching the GameList until the room appears, so an
empty or unparseable GameList leaves it waiting silently (the observed
"authenticated + JoinLobby then only keepalives, `Invalid session ID` on
screen" symptom).

## The GameList room-property contract (byte-for-byte)

Extracted from `RoomInfo$$InternalCacheProperties` (RVA 0x218CB30) and the
getters `get_IsOpen`@0x218D5B0 (reads field +0x38), `get_IsVisible`@0x218D5C0
(+0x39), `get_MaxPlayers`@0x218D5A0 (+0x20), `get_PlayerCount`@0x218D580
(+0x58). Each room's value Hashtable is parsed with these well-known **byte**
keys:

| Key | Meaning | Client field | Type rule |
|---|---|---|---|
| 251 | RemovedFromList | +0x10; **drops the room if truthy** | **strict bool** — wrong type throws |
| 255 or 243 | MaxPlayers | +0x20 via `Convert.ToInt32` | byte **or** int both accepted |
| 254 | **IsVisible** | +0x39 | **strict bool** |
| 253 | **IsOpen** | +0x38 | **strict bool** |
| 252 | PlayerCount | +0x58 via `Convert.ToInt32` | byte **or** int both accepted |
| 249 | (bool field +0x3a) | +0x3a | **strict bool** |
| 248 | MasterClientId | +0x48 | int |
| 250 | ref/object | +0x50 via `as`-cast | not strict (null if wrong) |
| other **string** keys | custom properties | `MergeStringKeys` | string-keyed only |

**The trap:** a strict-bool key (251, 253, 254, 249) sent as a **byte** throws
`InvalidCastException` *inside the client's event dispatch*. That silently
empties the cached room list and times the join out — there is no client-side
error, just a stall. `Convert.ToInt32` keys (252, 255, 243) tolerate a boxed
byte, so MaxPlayers/PlayerCount may be byte or int.

Note Fusion **swaps 253/254 versus vanilla PUN** (PUN: 254=IsOpen, 253=IsVisible;
Fusion: 253=IsOpen, 254=IsVisible). Follow Fusion's ordering, confirmed by the
getters above.

## Server conformance (`MasterServerHandler.BuildFusionSessionLobbyEvent`)

The server's Fusion session-lobby builder already matches this contract:
`IsOpen`(253)=bool, `IsVisible`(254)=bool, `PlayerCount`(252)=int,
`MaxPlayers`(255)=int, and it strips the host's byte-typed well-known keys
(251/250/248/243) before re-adding them with the correct types, passing the
game's own string/int custom props (scene, vehicleType, version, knownPlayers)
through untouched. `PhotonCodes.RoomProperty` uses the same numbers. So the
contract is satisfied in code.

## Remaining suspects if a live join still stalls after this

Both are code-identified and need no exploratory testing:

1. **Session-name identity.** Fusion searches for a room whose name equals the
   session name — the code, upper-cased by `Join`. The server keys the
   advertised room by the `OpJoinLobby` LobbyName (P213). The host must have
   registered its room under that exact string, case included, or the client's
   wait never matches.
2. **Game-level custom-property filter.** After caching, the game may reject a
   room whose custom props (version/scene/vehicleType) don't match the joiner's
   build. Ensure the host's stored version/scene pass through (they do — only
   the well-known byte keys are stripped).

The Fusion state gate `<Join>b__0` (RVA 0x1BFDFA0) is only a runner-state
bitmask (`state & 0x1600000`), not a room-content check; room matching lives
entirely in Fusion's matchmaking, driven by the GameList contract above.


## Host teardown: "connection interrupted (1020)"

The host's `NetworkManager$$OnDisconnectedFromServer` (RVA 0x1BF92E0) is
**reactive** — it maps the Photon `DisconnectCause` into a
`NetworkPlatformFailedReason` (default `Unknown`=16), then `Shutdown`s and shows
the generic "connection interrupted" screen. The host does not self-terminate on
an "invited nobody" timer. So a host that drops ~2-4 min after opening Invite
Friends was **evicted by the server**: the Invite Friends system applet suspends
the game, the host stops answering keepalive pings, and the listener evicts any
peer silent past `Server:Listener:DeadTimeoutSeconds` (**default 120 s**). Raise
it via `OPENPAK_Server__Listener__DeadTimeoutSeconds` (note the `OPENPAK_`
prefix and `Server__` segment — a bare `Listener__…` is ignored), or hold the
host's slot while its room has a live registration instead of evicting on inbound
silence alone.


**Durable fix (implemented):** the listener now gives a peer that has actually
joined a room (`SessionStatus.InRoom`) a much longer eviction grace
(`Server:Listener:InRoomDeadTimeoutSeconds`, default 1800 s) than a pre-room peer
(`DeadTimeoutSeconds`), via `UdpListener.EvictionGrace(status)`. A short base
timeout can't survive an applet left open, so hosting is protected by keying the
grace to session stage, while a peer stuck at connect/auth is still reclaimed
quickly. The 600 s `DeadTimeoutSeconds` bump remains as defence in depth.


## Validated 2026-09-02: custom props (version) are required, not just typing

A live two-player join (actor=2) confirmed the real blocker: the joiner caches
the room but Fusion will NOT send OpJoinGame unless the GameList entry carries the
host's custom props — above all **`version`** (listed by the host under key 250).
The server must (a) persist the creator's incoming `GameProperties(248)` at
EnterRoom and (b) build the lobby GameList from the object-keyed game-property
store so string keys like `"version"`/`"scene"` are advertised. Byte-keyed
`Room.Properties` cannot hold them. Typing the well-known keys correctly is
necessary but insufficient on its own.


> **Scope caveat (be honest):** this fix makes the JOIN HANDSHAKE complete
> (`actor=2`), repeatably. It does **not** give a working two-player session —
> both peers still fail ~120 s after entering gameplay on Fusion's real-time
> transport (PRUDP-Lite P2P freeze + two-peer 101 relay addressing). See the
> Outbound `HANDOFF.md` "NEXT SESSION" plan. Do not cite `actor=2` as a working
> join.

## Decoded 2026-09-20: `knownPlayers` is SHA-1 of the player *nickname*

`knownPlayers` is one of the host's custom string props (above). Its value is a
**comma-separated list of base64 SHA-1 digests, one per player the host's world
already has a character for**, and the digest's preimage is the player's
**Nintendo account nickname** — not the Photon UserId, not the BAAS account id.

Recovered from a live prod trace (room `QL4M9E`, `openpak-photon` at
`--trace`), where the property grew by one entry as each player first entered:

```
knownPlayers = "wbxJU9VY0Px27eeRu4v+bY3Z3Ts=,5xtgI35B95UsuQd//qIa7J7UJ3w="
               └ SHA1("<user-a>")             └ SHA1("<user-b>")
```

Both preimages were confirmed by direct digest comparison, not guessed. The
property is written by the host and reaches the joiner twice: once in the
lobby `GameList` (before it decides to send `OpJoinGame`) and again in the
`EnterRoom` game properties.

### Why the server must care

We only pass this through — but it is the game's own **returning-player
switch**, so it must agree with the identity we mint. A player whose nickname
is listed is one the host's world expects to already have a character for; a
player who is not listed gets the character-creation path.

That makes the Photon UserId a **persistent** identifier, not a per-session
one. Until 2026-09-20 `AccountIdentityRegistry.Register` hashed the whole
`nex:` credential, which embeds a per-mint expiry
(`<pid>.<username>.<expiry>`), so the same account received a different UserId
on every login — four in one day for one account, observed. A host could then
say "I know this nickname" while holding no character under the id the joiner
now presents. `Register` hashes `StableKey(identity)` instead: the pid for
`nex:`, the device id for `bsdid:`. The full credential still goes to nx-baas
verbatim — only that service can verify its HMAC.

> **Not yet pinned:** that the game *crashes* (rather than degrading) on that
> disagreement is inference from the timing of three failed joins on
> 2026-09-20, not from the decompile. The sessions that failed 16 s and 30 s
> after `players=2` were both joiners already listed in `knownPlayers` and both
> arriving with a freshly rotated UserId; the one that survived was a first
> visit. Worth confirming against `NetworkManager` in the decomp map before
> citing it as established.
