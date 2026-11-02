# Protocol: photon

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

## Connecting a client (no mod required)

The server speaks the stock Photon protocol, so an **unmodified game client can connect and play** —
once it reaches our Name Server the server drives Name Server → Master → Game itself (via
`AdvertisedHost`). Two ways to point a client at us:

- **Built-in LAN mode (the mod-free direct connection).** Black Ice has a native LAN mode: enable it and
  enter the server's IP/port (`127.0.0.1` / `5055`), then connect — the completely unmodified client walks
  the stack into the match.
- **`OpenPak.Redirect` (optional client mod).** Use it when you want the game's **normal online startup /
  server browser** to surface **our custom realm list** instead of Photon Cloud. It's optional — the
  redirect's only job is the startup/server-list experience; gameplay works without it.

## Titles and identifiers

- **Moving Out** (Switch, PUN 2): AppId `a956fef2-5ccf-4c52-9df1-adb2b1b6953b`, name server
  `ns.exitgames.com`. Mapped but dormant: the Switch build links PUN and ships the AppId but
  exposes no online mode. Moving Out 2 is gated behind Team17's own backend before it reaches
  Photon (`nx-baas/docs/t17-backend.md`).
- **We Were Here** (Switch, Fusion): AppId `6f95ce9e-3a91-40ce-b11e-49c0864676a9`. Its Vivox
  voice and PlayFab backend are separate services this repo does not answer.
- **Black Ice** (PC, PUN): master-client authority model (one player simulates the world for
  everyone), so moving authority to the server also removes client-side cheating.
- **Outbound** (Switch, Fusion Shared): driven by [`outbound`](../../outbound).

## Session-join contract

The client's full session-join dialogue and its GameList room-property contract were
reconstructed from Outbound's own decompiled code, no live capture needed:
[protocol/06-fusion-session-join.md](protocol/06-fusion-session-join.md). The server's Fusion
session-lobby event conforms to it.

## Two kinds of plugins

- `plugins/` is client-side BepInEx mods that run in the game (redirect, op-logger, MOTD,
  ticket spike); building them copies their DLLs into the game's `BepInEx/plugins`.
- [plugins.md](plugins.md) is the server-side plugin system (anti-cheat, game modes, mutators,
  arena, killfeed) that runs in the server process.
