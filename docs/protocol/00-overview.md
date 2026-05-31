# Black Ice Protocol — Overview

This directory documents the network protocol of **Black Ice** (Unity 2020.3.49, Mono
backend) as observed during Phase 0 reconnaissance. Every code and field below traces to a
machine-generated table in [`generated/`](generated/) or to a live capture from the
in-process op-logger. No values are invented.

## Networking stack

Black Ice multiplayer is built on **Photon PUN** (Photon Unity Networking) over Photon's
proprietary reliable-UDP transport. There is no dedicated game server: clients connect to
Photon's hosted infrastructure and one client is elected **master client**, holding
authority over the shared world (enemy spawns, AI, damage, loot).

## Server topology (observed live)

Photon LoadBalancing uses a three-server handshake. All three were captured in a single
solo launch:

```
Client ──► Name Server      (region routing + first Authenticate)
       ──► Master Server     45.67.211.164:5055   (lobby, matchmaking, CreateGame)
       ──► Game Server        188.241.71.81:5056   (room: "Black Ice Public Game #18")
```

> The specific IPs are dynamically assigned by Photon and will differ per session/region;
> what matters is the *flow* (Name → Master → Game) and that the address is returned in
> operation-response parameter `230` (`ParameterCode.Address`).

## Key finding for the project

Black Ice runs through Photon **even in solo play** — it auto-connects at startup and
immediately creates/joins a public room, rather than using `PhotonNetwork.OfflineMode`.
This means the full connect → room → gameplay flow is observable without a second player.

The 85 `[PunRPC]` handlers perform **no authority checks of their own** (none reference
`IsMasterClient`); they trust whoever invoked them. This is the structural reason
client-side cheating is trivial and is the central target for Phase 3 (server authority)
and Phase 4 (anti-cheat).

## Documents

| Doc | Contents | Primary evidence |
|---|---|---|
| [01-connection-flow.md](01-connection-flow.md) | Name→Master→Game handshake, auth | op-log timeline |
| [02-operations.md](02-operations.md) | Operation/Event/Parameter/Error codes | `generated/photon_constants.csv` |
| [03-rpc-catalog.md](03-rpc-catalog.md) | The 85 game RPCs + authority analysis | `generated/rpcs.csv` |
| [04-serialization.md](04-serialization.md) | GpBinary + serialize-view payload layouts | `generated/serialize_views.csv` |
| [05-transport.md](05-transport.md) | Reliable-UDP command/packet framing | Photon3Unity3D enet layer (static) |

## Generated tables

| File | Rows | What |
|---|---|---|
| `generated/photon_constants.csv` | 132 | Photon OperationCode/EventCode/ParameterCode/ErrorCode |
| `generated/rpcs.csv` | 85 | `[PunRPC]` handlers (type, method, params, authority hint) |
| `generated/serialize_views.csv` | 6 | `OnPhotonSerializeView` payload call order |
| `generated/instantiations.csv` | 1 | Literal prefab names to `PhotonNetwork.Instantiate` |
| `generated/connection-timeline.sample.txt` | — | Sanitized live op timeline (no credentials) |

Regenerate the tables with: `dotnet run --project tools/BlackIce.Recon.Catalog`.
