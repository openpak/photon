# BlackIce.BigLobby

> **CLIENT-side** BepInEx mod (runs in the *Black Ice* game, not the server). One of the client mods in
> [`../README.md`](../README.md) — not part of the server plugin system.

> **Experimental.** Raises the client's room-size ceiling so a match can hold more than the stock ~8
> players on a BlackIce server. The game is only *verified* for ~8 per match; larger sizes are unproven —
> see **Caveats**.

## Why this exists

> **This mod is optional — it is NOT required to join a >8 realm.** Room capacity is enforced
> **entirely by the BlackIce server** (`GameServerHandler.EnterRoom` admits joins against the realm's
> configured `MaxPlayers`). An **unmodified client joins a realm configured for 32 players just fine.**

The **server already supports large rooms** — a room structurally holds thousands of actors, and a realm's
`MaxPlayers` is operator-set (`0` = unlimited; see [`../../docs/large-servers.md`](../../docs/large-servers.md)).
The remaining ~8 is a **client-side in-match assumption**, not a connection gate: the client that *creates*
a room calls PUN with the game's small baked-in `RoomOptions.MaxPlayers` (~8). The server ignores that for
the capacity gate, but the **creating client's own local PUN/HUD** still believes the small number, which is
where odd rendering/UI can show up in a big match.

This mod raises that requested capacity at the **public PUN API boundary**, so the room-creator's local
view matches the big realm. It's a **smoothing / make-it-verifiable** aid, not a requirement to connect.

## What it patches

Public PUN surface only — no game internals:

- **`RoomOptions` construction** — when the game leaves `MaxPlayers` at its small default (or `0`), raise it
  to the configured ceiling. It only ever **enlarges**; a cap the game deliberately set higher is left alone.

That single seam covers both hosting and the capacity the client will accept, regardless of exactly how the
game creates its rooms.

## Config

`BepInEx/config/blackice.biglobby.cfg`:

| Key | Default | Meaning |
|---|---|---|
| `Lobby.MaxPlayers` | `32` | Room capacity to request. **Clamped to 1–255** (PUN's `MaxPlayers` is a byte). |

The **server's realm `MaxPlayers` must be raised to match** — both sides have to agree. Set it via config
(`docs/configuration.md`) or the `realm` console commands. (The server clamps its advertised lobby count to
255 too, since the browser slot is byte-typed.)

## Caveats — read before using

- **Not a stability guarantee.** This overrides a *client design assumption*; it does not make the game's
  rendering, HUD, or netcode correct at large sizes. Those were built for small matches and are
  **unverified above 8**. Treat big matches as experimental (it's on the live-verification roadmap).
- **Hard ceiling 255** — the Photon wire format carries `MaxPlayers` as a byte.
- **No-op alone** — without the server's realm cap raised to match, nothing changes.

## Build / deploy

```bash
dotnet build BlackIce.sln          # builds the client mods (and the server + tools)
```

Targets `netstandard2.0` (BepInEx/Mono) and auto-deploys into the game's `BepInEx/plugins` on build. It
references the game's BepInEx/Unity/Photon DLLs, which are **not** committed (game-derived; see `NOTICE`).
