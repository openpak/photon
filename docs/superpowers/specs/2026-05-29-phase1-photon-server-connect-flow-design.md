# Phase 1 — Photon Transport + Connect Flow (Design)

**Project:** Black Ice independent server (open-source, GPLv3)
**Phase:** 1 of 5 — Photon transport, serialization, Name/Master/Game connect flow, client redirect
**Date:** 2026-05-29
**Status:** Approved design — pending spec review

---

## 1. Context

Phase 0 documented Black Ice's protocol (see `docs/protocol/`). The game uses **Photon PUN**
over reliable UDP, walking a three-server flow: **Name Server** → **Master Server** →
**Game Server**. The client auto-connects at startup and creates/joins a room even solo, so
reaching the in-room state is observable without a second player.

Phase 1 builds the server side of that flow from scratch in C#/.NET so the real client
connects to our infrastructure and reaches the in-room state, with Photon Cloud never
contacted. No gameplay relay yet — that is Phase 2.

**Licensing constraint:** the server is GPLv3 and ships **no Photon code**. Photon's
`Photon3Unity3D.dll` / `PhotonRealtime.dll` are used **only as test-time serialization
oracles** (referenced by test projects, never redistributed). All protocol code is
clean-room, informed by the Phase 0 documentation.

## 2. Goals & Definition of Done

The **unmodified-except-redirect** Black Ice client connects through our Name → Master →
Game servers and reaches the in-room state (`Join` event code 255 received), with the room
created/registered server-side. All three hops run on our process; `ns.exitgames.com` /
Photon Cloud is never contacted.

Out of scope: relaying events/state between clients, world simulation, anti-cheat
(Phases 2–4).

## 3. Architecture

One server process exposing three logical roles (Approach A). A single .NET application
listens on the three standard Photon UDP ports and routes each connection to the correct
role-handler; redirect responses hand back our own ports.

| Port (UDP) | Role |
|---|---|
| 5058 | Name Server |
| 5055 | Master Server |
| 5056 | Game Server |

### Solution layout (`server/`)

| Project | Responsibility | Depends on |
|---|---|---|
| `BlackIce.Photon` | Clean-room transport (eNet reliable UDP) + GpBinary (`Protocol16`) serialize/deserialize | — |
| `BlackIce.Photon.Crypto` | Encryption handshake (Photon DH / token), isolated | `BlackIce.Photon` |
| `BlackIce.Server.Core` | UDP socket loop, peer/connection lifecycle, operation dispatch | `BlackIce.Photon`, `.Crypto` |
| `BlackIce.Server.LoadBalancing` | NameServer / MasterServer / GameServer role-handlers + in-memory room registry | `.Core` |
| `BlackIce.Server.Host` | Console entrypoint + config (ports, bind address) | `.LoadBalancing` |
| `BlackIce.Photon.Tests` | Round-trip serialization tests vs the real Photon DLL oracle | `BlackIce.Photon` + Photon DLL (test-only) |
| `BlackIce.Server.Tests` | Transport, dispatch, role-handler unit tests | all |

Client redirect lives in the existing `plugins/BlackIce.OpLogger` (or a sibling
`plugins/BlackIce.Redirect`): read `ServerAddress` / `Port` from a BepInEx config entry and
assign `PhotonServerSettings.AppSettings.Server` at startup, keeping `UseNameServer = true`.
This is the realmlist-style UX — the player edits one config line.

## 4. Data flow

```
Client ──CT_CONNECT──► NameServer:5058
        ◄─CT_VERIFYCONNECT─ (server assigns PeerID)
        ──[encryption handshake if required]──►
        ──Authenticate(220=GameVersion, 224=AppId, 210=Region)──►
        ◄─Authenticate resp(230=Master addr → our :5055, 221=minted token, 225=UserId)─
   (client disconnects, connects to Master)
Client ──CT_CONNECT──► MasterServer:5055
        ──Authenticate(token 221)──► ◄─resp─
        ──JoinLobby(229)──► ◄─resp + GameList(230) event─
        ──CreateGame(227)──► ◄─resp(230=Game addr → our :5056)─
   (client disconnects, connects to Game)
Client ──CT_CONNECT──► GameServer:5056
        ──Authenticate(token 221)──► ◄─resp─
        ──CreateGame/JoinGame(227/226)──► ◄─resp(actor number, room props)─
        ◄─Join(255) event─                        ← DONE: client is in-room
```

The token minted at the Name Server is the trust thread: Master and Game validate it rather
than re-authenticating from scratch. Token format is our own (e.g. signed/opaque blob the
server can verify); the client treats `221` as opaque and replays it.

## 5. Encryption (highest-risk unknown)

Photon UDP may establish datagram encryption around `Authenticate`. The Phase 0 op-log did
**not** show `ExchangeKeysForEncryption` (250), suggesting token-based auth without
per-session Diffie-Hellman — but this is not assumed.

**First implementation step is a spike:** instrument the client's crypto path (decompiled
`ExitGames.Client.Photon.Encryption`) to confirm exactly which handshake, if any, the client
performs, then implement precisely that in `BlackIce.Photon.Crypto`. Documented fallback if
the client requires encryption we cannot yet satisfy: disable it client-side via the BepInEx
mod for Phase 1 and revisit. Expect iteration here.

## 6. Testing strategy

- **Serialization (TDD, unit):** round-trip every `GpType` and representative operations
  (`Authenticate`, `CreateGame`, `JoinGame`) asserting our bytes equal the real
  `Photon3Unity3D.dll` encoder's output (referenced test-only as oracle). Exact and
  deterministic; needs no packet capture.
- **Transport (unit):** packet/command header encode-decode; sequence/ack logic;
  CONNECT→VERIFYCONNECT handshake.
- **Role-handlers (unit):** each operation produces the expected response parameters
  (e.g. Master `CreateGame` returns a Game Server address in param 230).
- **Integration (acceptance):** launch the real client with the redirect mod pointed at
  `127.0.0.1`; assert the op-logger records the client reaching `Join` (255). The Phase 0
  op-logger is the integration probe.

## 7. Error handling

- Malformed/garbage packets are logged and dropped, never fatal (the server must survive
  hostile input — groundwork for Phase 4).
- Unknown operations return a Photon error `returnCode`, not a disconnect.
- Per-peer state is torn down on disconnect/timeout.
- The server is authoritative for every value it sends; the client receives only what we
  decide, even in Phase 1.

## 8. Milestones (for the plan)

1. **Protocol library** — GpBinary serialize/deserialize, validated against the DLL oracle.
2. **Transport** — eNet header/command framing, reliable channel (CONNECT/VERIFYCONNECT/
   ACK/SENDRELIABLE), sequencing. Fragmentation and unreliable channels deferred to Phase 2
   unless connect requires them.
3. **Encryption spike + handshake.**
4. **Server core** — UDP loop, peer lifecycle, operation dispatch.
5. **Name Server** — Authenticate → Master address + token.
6. **Master Server** — Authenticate, JoinLobby/GameList, CreateGame → Game address.
7. **Game Server** — Authenticate, CreateGame/JoinGame → actor number + Join event; room registry.
8. **Redirect mod** — config-driven `AppSettings.Server` override (realmlist UX).
9. **Integration** — real client reaches in-room through our server.

## 9. Out of scope (Phase 1)

- Relaying RaiseEvent / serialize-view between clients (Phase 2).
- World simulation / server authority over gameplay (Phase 3).
- Anti-cheat (Phase 4).
- Fragmentation and unreliable channels, unless the connect flow requires them.
- Persistence / database (in-memory room registry only).
