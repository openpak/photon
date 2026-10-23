# OpenPak.Server — Photon-compatible game server

OpenPak's server for Unity games on Photon: a clean-room implementation of Photon's
transport and LoadBalancing protocol, plus a clean-room host for Photon Fusion's own
Shared-mode simulation. Self-host the titles you own instead of depending on Photon Cloud.

Titles it currently serves:

- **Outbound** (Nintendo Switch, Photon Fusion Shared mode) — the rig that drives it is
  [`servers/outbound`](../outbound).
- **Moving Out** (Nintendo Switch, Photon PUN 2) — AppId `a956fef2-5ccf-4c52-9df1-adb2b1b6953b`,
  name server `ns.exitgames.com`. Mapped, but **dormant**: the Switch build links PUN and ships
  the AppId while exposing no online mode at all (no online/lobby/matchmaking string in any of
  its language tables), so nothing will ever connect with it unless a patch adds the menu.
  Its sequel does *not* belong here yet either: Moving Out 2 is gated behind Team17's own
  backend before it ever reaches Photon (`nx-baas/docs/t17-backend.md`).
- **We Were Here** (Nintendo Switch, Photon Fusion) — AppId `6f95ce9e-3a91-40ce-b11e-49c0864676a9`.
  Its Vivox voice and PlayFab backend are separate services this repo does not answer.
- **Black Ice** (PC, Photon PUN) — the codebase's origin. Its multiplayer runs a
  *master-client authority* model where one player simulates the world for everyone, so
  moving authority to the server also kills client-side cheating. The PC-side client mods
  and asset tooling are not in this repo; they live in an earlier copy of the Photon server.


## Health and player counts

Every listener here speaks UDP, and a UDP port cannot be probed: nothing distinguishes a bound
socket from a dead one to anything outside the process. So the server also opens one TCP port
(`Server.HealthPort`, default **8080**, `0` disables it):

| Path | Answers |
| --- | --- |
| `/health` | `{"status":"ok"}` — it is running |
| `/players` | `{"players":N,"rooms":N,"detail":[…]}` — counted from live room membership, so it cannot drift from what is actually being relayed |

An idle server answers `players: 0` rather than failing: "nobody is playing" and "not running"
are exactly what this endpoint exists to tell apart. The website's status page reads it.

## Building the image

**By the release workflow.** The image needs nothing beyond the repository: the Fusion Shared-mode
host is our own code (`OpenPak.Server.LoadBalancing/Fusion`), with no retail assemblies to stage.
`release.yml` builds and pushes it, so a `v*.*.*` tag is the deploy, like every other OpenPak service.

A hand build from a workstation works the same way:

```sh
podman build --platform linux/amd64,linux/arm64 --manifest ghcr.io/openpak/photon:build .
podman manifest push --all ghcr.io/openpak/photon:build docker://ghcr.io/openpak/photon:latest
ssh <deploy host> 'podman pull ghcr.io/openpak/photon:latest &&
                   systemctl --user restart openpak-photon'   # drops live games
```

## Status

**Phase 1 — Photon transport + connect flow** (in progress, branch `phase1-connect`). The
server foundation is built and tested: GpBinary v1.8 codec, eNet transport, Oakley-768 DH +
AES encryption, UDP server core, and Name/Master/Game role handlers. See
`docs/superpowers/plans/`.

The client's full session-join dialogue and its GameList room-property contract were
reconstructed from Outbound's own decompiled code (no live capture needed) —
[`docs/protocol/06-fusion-session-join.md`](docs/protocol/06-fusion-session-join.md). The
server's Fusion session-lobby event conforms to that contract.

## Building & running (CLI)

Requires the **.NET 8 SDK**. No game files are needed to build or test the server:

```bash
dotnet build server/OpenPak.Server.sln          # build the server
dotnet test  server/OpenPak.Server.sln          # run the test suite
dotnet run --project server/OpenPak.Server.Host 127.0.0.1   # run, advertising 127.0.0.1
```

On first run the server writes a documented `openpak.server.json` next to the binary, applies the
database schema, prints a one-time admin bootstrap code, and starts the Name/Master/Game listeners.
Rooms can be served from region nodes in other countries; see [docs/federation.md](docs/federation.md).
Configure it via that file or `OPENPAK_*` environment variables — see
[`docs/configuration.md`](docs/configuration.md). Stop with Ctrl-C (graceful shutdown).

## Building & running (Visual Studio)

Requires **Visual Studio 2022 (17.8+)** with the **.NET 8 SDK**. A local copy of Black Ice is only
needed for the full interop **oracle** tests and the BepInEx plugins; the server itself builds and
tests without it.

1. Open **`OpenPak.sln`** at the repo root — it contains the server and the client plugins.
2. Set **`OpenPak.Server.Host`** as the startup project (right-click → *Set as Startup Project*).
3. Press **F5**. Launch profiles are provided (Run/Debug dropdown):
   - *OpenPak.Server (local LAN)* — advertises `127.0.0.1` (same-PC testing).
   - *OpenPak.Server (advertise this PC on LAN)* — edit the IP in the profile to this machine's
     LAN address so other PCs can connect.
   - *OpenPak.Server (require Name Server token)* — disables anonymous LAN auth.

Run tests via **Test → Run All Tests**. (With the game's `Photon3Unity3D.dll` present, the full
interop oracle suite runs too; without it, those tests are skipped and the rest still run.)

### Connecting a client (no mod required)

The server speaks the stock Photon protocol, so an **unmodified game client can connect and play** —
once it reaches our Name Server the server drives Name Server → Master → Game itself (via
`AdvertisedHost`). Two ways to point a client at us:

- **Built-in LAN mode (the mod-free direct connection).** Black Ice has a native LAN mode: enable it and
  enter the server's IP/port (`127.0.0.1` / `5055`), then connect — the completely unmodified client walks
  the stack into the match.
- **`OpenPak.Redirect` (optional client mod).** Use it when you want the game's **normal online startup /
  server browser** to surface **our custom realm list** instead of Photon Cloud. It's optional — the
  redirect's only job is the startup/server-list experience; gameplay works without it.

### Client mods vs. the server plugin system

"Plugins" means **two different things** in this repo, and they don't mix:

- `plugins/` — **client-side** BepInEx mods that run *in the game* (redirect,
  op-logger, MOTD, ticket spike). Building them copies their DLLs into the game's `BepInEx/plugins`.
- [`docs/plugins.md`](docs/plugins.md) — the **server-side** plugin system (anti-cheat, game modes,
  mutators, arena, killfeed, …) that runs in the server process.

## Legal / scope

This is a clean, independent interoperability project in the tradition of OpenRA and
TrinityCore. It contains **only original code and protocol documentation**. It does not
contain, redistribute, or depend on the game's copyrighted binaries, assets, or
decompiled source — those are analysis-only artifacts kept locally and excluded from
version control. You must own a legitimate copy of Black Ice to use this software.

## Origin

This server began as a fork of **BlackIce.Server** by Black Ice Server (Nate), an
independent Photon PUN server for the Unity game Black Ice, released under the GNU General
Public License v3.0. Its history is kept in this repository with the original authorship.
OpenPak's work since the port (2026-09-07) is AGPL-3.0-only; GPLv3 section 13 allows the
combination, and the upstream-authored code remains available under the GPL-3.0.

## License

AGPL-3.0-only — see `LICENSE`. Network use triggers section 13: if you run a
modified server for others, offer them its source.
