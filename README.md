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

## Building & running (CLI)

Requires the **.NET 8 SDK**. No game files are needed to build or test the server:

```bash
dotnet build server/BlackIce.Server.sln          # build the server
dotnet test  server/BlackIce.Server.sln          # run the test suite
dotnet run --project server/BlackIce.Server.Host 127.0.0.1   # run, advertising 127.0.0.1
```

On first run the server writes a documented `blackice.server.json` next to the binary, applies the
database schema, prints a one-time admin bootstrap code, and starts the Name/Master/Game listeners.
Configure it via that file or `BLACKICE_*` environment variables — see
[`docs/configuration.md`](docs/configuration.md). Stop with Ctrl-C (graceful shutdown).

## Building & running (Visual Studio)

Requires **Visual Studio 2022 (17.8+)** with the **.NET 8 SDK**. A local copy of Black Ice is only
needed for the full interop **oracle** tests and the BepInEx plugins; the server itself builds and
tests without it.

1. Open **`BlackIce.sln`** at the repo root — it contains the server and the client plugins.
2. Set **`BlackIce.Server.Host`** as the startup project (right-click → *Set as Startup Project*).
3. Press **F5**. Launch profiles are provided (Run/Debug dropdown):
   - *BlackIce.Server (local LAN)* — advertises `127.0.0.1` (same-PC testing).
   - *BlackIce.Server (advertise this PC on LAN)* — edit the IP in the profile to this machine's
     LAN address so other PCs can connect.
   - *BlackIce.Server (require Name Server token)* — disables anonymous LAN auth.

Run tests via **Test → Run All Tests**. (With the game's `Photon3Unity3D.dll` present, the full
interop oracle suite runs too; without it, those tests are skipped and the rest still run.)

### Connecting a client (no mod required)

The server speaks the stock Photon protocol, so an **unmodified game client can connect and play** —
once it reaches our Name Server the server drives Name Server → Master → Game itself (via
`AdvertisedHost`). Two ways to point a client at us:

- **Built-in LAN mode (the mod-free direct connection).** Black Ice has a native LAN mode: enable it and
  enter the server's IP/port (`127.0.0.1` / `5055`), then connect — the completely unmodified client walks
  the stack into the match.
- **`BlackIce.Redirect` (optional client mod).** Use it when you want the game's **normal online startup /
  server browser** to surface **our custom realm list** instead of Photon Cloud. It's optional — the
  redirect's only job is the startup/server-list experience; gameplay works without it.

### Client mods vs. the server plugin system

"Plugins" means **two different things** in this repo, and they don't mix:

- [`plugins/`](plugins/README.md) — **client-side** BepInEx mods that run *in the game* (redirect,
  op-logger, MOTD, ticket spike). Building them copies their DLLs into the game's `BepInEx/plugins`.
- [`docs/plugins.md`](docs/plugins.md) — the **server-side** plugin system (anti-cheat, game modes,
  mutators, arena, killfeed, …) that runs in the server process.

## Legal / scope

This is a clean, independent interoperability project in the tradition of OpenRA and
TrinityCore. It contains **only original code and protocol documentation**. It does not
contain, redistribute, or depend on the game's copyrighted binaries, assets, or
decompiled source — those are analysis-only artifacts kept locally and excluded from
version control. You must own a legitimate copy of Black Ice to use this software.

## License

GPLv3 — see `LICENSE`.
