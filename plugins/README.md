# Client-side mods (BepInEx) — NOT the server plugin system

> ⚠️ **This folder is CLIENT-side.** Everything here is a **BepInEx/Harmony mod that runs inside the
> *Black Ice* game client** (`netstandard2.0`, Mono). These are recon / bring-up / convenience tools you
> install into the game's `BepInEx/plugins`. They are **not** the server's plugin system.

**Don't confuse these three things:**

| Thing | Where | Runs in | What it is |
|---|---|---|---|
| **Client mods** | `plugins/` *(this folder)* | the **game client** (BepInEx/Mono) | Harmony patches: redirect, op-logger, MOTD, ticket spike |
| **Server plugins (built-in)** | `server/BlackIce.Server.LoadBalancing/Plugins/` | the **server** (.NET 8) | anti-cheat, game modes, mutators, arena, killfeed, … |
| **Server plugins (external)** | the server's runtime `server-plugins/` dir | the **server** (.NET 8) | drop-in plugin DLLs loaded at startup / via `plugin load` |

The **server-side** plugin system is documented in [`../docs/plugins.md`](../docs/plugins.md). This README
is only about the **client** mods below.

## The client mods here

| Mod | Purpose | Required? |
|---|---|---|
| **`BlackIce.Redirect`** | Points the game's Photon **Name Server** at a BlackIce.Server at startup, so the game's normal online connect / **server browser shows our custom realm list** instead of Photon Cloud. | **Optional** — see below |
| **`BlackIce.OpLogger`** | Logs the client's Photon operations/events — the recon tool used to reverse-engineer the protocol. | Optional (dev only) |
| **`BlackIce.Motd`** | Renders the server's `ServerMessage` lines (e.g. MOTD) in the client UI. | Optional |
| **`BlackIce.BigLobby`** | Raises the client's room-size ceiling so a match can hold more than the stock ~8 players on a large BlackIce realm. **Experimental** (client unverified above 8). See its [README](BlackIce.BigLobby/README.md) and [`../docs/large-servers.md`](../docs/large-servers.md). | Optional |
| **`BlackIce.SteamTicketSpike`** | Dev spike: mints a Steam-validated game-server ticket from a BepInEx plugin (groundwork for server-side ticket auth). | Optional (dev only) |

## You do NOT need a client mod to play on our server

The server speaks the **stock Photon protocol**, so an **unmodified game client can connect and play** on
our stack. Once a client reaches our **Name Server**, the server hands back the Master/Game addresses
(from the server's `AdvertisedHost`) and drives the whole handshake — Name Server → Master → Game → into
the match — with no client changes.

There are two ways to point a client at our server:

1. **Built-in LAN mode (no mod, the easy direct connection).** Black Ice has a native LAN mode: enable it
   and enter the server's IP/port (e.g. `127.0.0.1` / `5055`). The completely unmodified client connects
   straight through our stack. This is the "default direct connection" path and needs nothing from this
   folder.
2. **`BlackIce.Redirect` (a client mod, optional).** Use this when you want the game's **normal online
   startup flow / server browser** to surface **our custom realm list** (rather than typing an IP in LAN
   mode). Toggle it with `Enabled = true/false`; `false` falls straight back to Photon Cloud.

   *(A network-level redirect — a `hosts`/DNS entry mapping the Photon Name Server hostname to the
   BlackIce server — is a third, zero-mod way to push even the online flow onto our stack.)*

**In short:** direct play on our stack = built-in LAN mode, no mod. The redirect mod is an optional
convenience whose job is making the startup/server-list experience show our realms. Either way, set the
server's `AdvertisedHost` to an address the client can reach (see [`../docs/configuration.md`](../docs/configuration.md)).

## Build / deploy

```bash
dotnet build BlackIce.sln          # builds these client mods (and the server + tools)
```

These target `netstandard2.0` (BepInEx/Mono) and auto-deploy into the game's `BepInEx/plugins` on build.
They reference the game's BepInEx/Unity/Photon DLLs, which are **not** committed (game-derived; see `NOTICE`).
