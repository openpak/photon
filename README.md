# BlackIce.Server

An independent, open-source server implementation for the Unity game **Black Ice**,
created for interoperability, preservation, and server-authoritative anti-cheat.

Black Ice's multiplayer runs on Photon PUN with a *master-client authority* model:
one player simulates the shared world for everyone, which makes client-side cheating
trivial. This project reimplements the server side of that protocol so the game can run
on infrastructure you control, and so world authority can move from an untrusted player
to the server.

## Status

**Phase 1 — Photon transport + connect flow** (in progress, branch `phase1-connect`). The
server foundation is built and tested: GpBinary v1.8 codec, eNet transport, Oakley-768 DH +
AES encryption, UDP server core, and Name/Master/Game role handlers. See
`docs/superpowers/plans/`.

## Building & running (Visual Studio)

Requires **Visual Studio 2022 (17.8+)** with the **.NET 8 SDK**, and a local copy of Black Ice
(its DLLs are referenced for the test oracle and the BepInEx plugins).

1. Open **`BlackIce.sln`** at the repo root — it contains the server and the client plugins.
2. Set **`BlackIce.Server.Host`** as the startup project (right-click → *Set as Startup Project*).
3. Press **F5**. Launch profiles are provided (Run/Debug dropdown):
   - *BlackIce.Server (local LAN)* — advertises `127.0.0.1` (same-PC testing).
   - *BlackIce.Server (advertise this PC on LAN)* — edit the IP in the profile to this machine's
     LAN address so other PCs can connect.
   - *BlackIce.Server (require Name Server token)* — disables anonymous LAN auth.

Run tests via **Test → Run All Tests** (62 tests).

### Connecting the game (LAN mode)

Black Ice has a built-in LAN mode. With the server running: in the game, enable LAN mode and set
the server IP/port (`127.0.0.1` / `5055`), then connect. The client walks Master → Game on your
server. (The `BlackIce.Redirect` BepInEx plugin is an optional alternative that sets this via a
config file.)

Building the plugin projects copies their DLLs into the game's `BepInEx/plugins` folder
automatically.

## Legal / scope

This is a clean, independent interoperability project in the tradition of OpenRA and
TrinityCore. It contains **only original code and protocol documentation**. It does not
contain, redistribute, or depend on the game's copyrighted binaries, assets, or
decompiled source — those are analysis-only artifacts kept locally and excluded from
version control. You must own a legitimate copy of Black Ice to use this software.

## License

GPLv3 — see `LICENSE`.
