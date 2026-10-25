# Photon

OpenPak's Photon Server.

| | |
| --- | --- |
| Networks | Nintendo Switch, PC |
| Protocol | Photon Realtime / LoadBalancing, Photon Fusion, Photon Quantum 2 |
| Image | `ghcr.io/openpak/photon` |
| Licence | AGPL-3.0-only |

## Titles

| Game | Network | Title ID | Module |
| --- | --- | --- | --- |
| [Outbound](https://openpak.org/titles#t-1) | Nintendo Switch | `0100ED9024EB8000` | Fusion Shared |
| [Moving Out](https://openpak.org/titles) | Nintendo Switch | `0100C4C00E73E000` | PUN 2 |
| [We Were Here](https://openpak.org/titles#t-72382) | Nintendo Switch | — | Fusion |
| [Stumble Guys](https://openpak.org/titles#t-72378) | Nintendo Switch | — | Quantum 2 |
| [Black Ice](https://openpak.org/titles) | PC | — | PUN |

## Networks

| Network | Reached through |
| --- | --- |
| Nintendo Switch | Photon Name Server hosts `ns.photonengine.io` and `ns.exitgames.com` |
| PC | Black Ice's native LAN mode (`127.0.0.1`, port `5055`), or the optional `OpenPak.Redirect` client mod |

## What it serves

| Listener | What | Port |
| --- | --- | --- |
| NameServer | Region and master lookup | UDP 5058 |
| MasterServer | Lobby, matchmaking, room creation | UDP 5055 |
| GameServer | Rooms, Fusion Shared and Quantum 2 sessions | UDP 5056 |
| Health | `/health`, `/players` | TCP 8080 |

## Run

```bash
dotnet run --project server/OpenPak.Server.Host 127.0.0.1
```

## Docs

- [protocol.md](docs/protocol.md) — hosts, endpoints, wire formats, protocol tables
- [evidence.md](docs/evidence.md) — captures, traces, confirmed vs assumed
- [deploy.md](docs/deploy.md) — ports, env vars, image, build and run
- [notes.md](docs/notes.md) — design rationale, history, status, upstream credit
- [admin-commands.md](docs/admin-commands.md) — admin and debug commands
- [configuration.md](docs/configuration.md) — configuration file and `OPENPAK_*` variables
- [debugging.md](docs/debugging.md) — debugging and instrumentation toolkit
- [federation.md](docs/federation.md) — rooms in other regions
- [federation-openpak.md](docs/federation-openpak.md) — how federation works
- [fusion-protocol-map.md](docs/fusion-protocol-map.md) — Fusion's session protocol, mapped from the SDK
- [fusion-shared-server.md](docs/fusion-shared-server.md) — hosting Fusion's Shared-mode server (Outbound)
- [game-modes.md](docs/game-modes.md) — server-side custom game modes
- [large-servers.md](docs/large-servers.md) — large servers and player-count limits
- [plugins.md](docs/plugins.md) — server-side plugins
- [design/](docs/design/) — design documents
- [protocol/](docs/protocol/) — protocol study files and generated tables
- [superpowers/](docs/superpowers/) — plans and specs

## Licence

AGPL-3.0-only. See [LICENSE](LICENSE).
