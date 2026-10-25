# Notes: photon

## Overview

Former README title: `# OpenPak.Server — Photon-compatible game server`

OpenPak's server for Unity games on Photon: a clean-room implementation of Photon's
transport and LoadBalancing protocol, plus a clean-room host for Photon Fusion's own
Shared-mode simulation. Self-host the titles you own instead of depending on Photon Cloud.

## Titles it currently serves

Titles it currently serves:

- **Outbound** (Nintendo Switch, Photon Fusion Shared mode) — the rig that drives it is
  [`servers/outbound`](../../outbound).
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

## Status

**Phase 1 — Photon transport + connect flow** (in progress, branch `phase1-connect`). The
server foundation is built and tested: GpBinary v1.8 codec, eNet transport, Oakley-768 DH +
AES encryption, UDP server core, and Name/Master/Game role handlers. See
`docs/superpowers/plans/`.

The client's full session-join dialogue and its GameList room-property contract were
reconstructed from Outbound's own decompiled code (no live capture needed) —
[`docs/protocol/06-fusion-session-join.md`](protocol/06-fusion-session-join.md). The
server's Fusion session-lobby event conforms to that contract.

## Client mods vs. the server plugin system

"Plugins" means **two different things** in this repo, and they don't mix:

- `plugins/` — **client-side** BepInEx mods that run *in the game* (redirect,
  op-logger, MOTD, ticket spike). Building them copies their DLLs into the game's `BepInEx/plugins`.
- [`docs/plugins.md`](plugins.md) — the **server-side** plugin system (anti-cheat, game modes,
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
