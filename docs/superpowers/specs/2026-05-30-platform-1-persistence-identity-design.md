# Server Platform — Sub-project 1: Persistence & Identity Foundation (Design)

**Project:** Black Ice independent server (open-source, GPLv3)
**Group:** Server Platform (1 of 3 — Persistence/Identity → Realms → In-game commands)
**Date:** 2026-05-30
**Status:** Approved design — pending spec review

---

## 1. Context

Phase 1 is complete: the real client connects through our Name → Master → Game servers,
joins a room, and plays — verified live, with an always-on test room advertised in the
lobby browser. The server currently holds all state in memory and treats every connection
anonymously.

This sub-project adds the **persistence + identity foundation** the rest of the platform
(realms, in-game admin commands) builds on: a pluggable database, SteamID-keyed accounts
auto-created on first connect, a 4-level permission model, and a one-time owner bootstrap.

**Key finding:** the vanilla client only sends `PhotonNetwork.NickName` (a player-chosen
character name), not the SteamID — `OnCustomAuthentication*` are empty stubs. So robust
SteamID identity requires our client mod to transmit it. Players already use our redirect
mod to connect, so this is a natural extension; the server falls back to nickname/minted
UserId when the SteamID is absent (e.g. native LAN mode without the mod).

## 2. Goals & Definition of Done

A connecting player (whose mod sends their SteamID) gets a persistent **Account + Profile**
auto-created at level **Player** on first connect; on first server startup the server prints
a **one-time bootstrap code** to its console; the server **console command loop** can
`promote`/`demote`/`ban`/`unban`/`list`; **banned** players are refused at authentication.
Backend is **SQLite by default**, swappable to **MySQL** by config.

Out of scope (later sub-projects): realm/ruleset storage (SP2); in-game chat commands and the
in-game `/claim <code>` redemption (SP3); gameplay/profile data beyond placeholders.

## 3. Architecture

New project **`BlackIce.Server.Data`** (EF Core), referenced by `Server.Core` /
`Server.LoadBalancing` / `Server.Host`.

- ORM: **EF Core 8**. Providers: `Microsoft.EntityFrameworkCore.Sqlite` (default) and
  `Pomelo.EntityFrameworkCore.MySql` (optional). Selected at startup by config:
  `Database:Provider` = `Sqlite` | `MySql`, `Database:ConnectionString`
  (default `Data Source=blackice.db`).
- `BlackIceDbContext` with `DbSet<Account>`, `DbSet<Profile>`, `DbSet<ServerState>`.
- EF Core **migrations** committed in-repo; `DbContext.Database.Migrate()` on startup creates/
  upgrades the schema.
- `AccountService` (the single entry point used by handlers) wraps the context with the
  resolve-or-create, promote/demote, ban, and bootstrap logic. Repositories kept minimal.

## 4. Schema (entities)

| Entity | Fields |
|---|---|
| **Account** | `SteamId` (string, PK / unique identity), `DisplayName` (string), `Level` (PlayerLevel 0–3, default Player), `IsBanned` (bool), `CreatedUtc`, `LastSeenUtc` |
| **Profile** (1:1 Account) | `SteamId` (FK/PK), `PlaytimeSeconds` (long, 0), `Notes` (string, "") — placeholders, extensible |
| **ServerState** (single row) | `Id` (=1), `BootstrapCode` (string, nullable), `BootstrapClaimed` (bool) |

```csharp
public enum PlayerLevel { Player = 0, Mod = 1, Admin = 2, Console = 3 }
```

`Account.Level` is the permission representation; finer-grained permissions are deferred.

## 5. Identity resolution

- **Client mod** (extend `BlackIce.Redirect`): before `ConnectUsingSettings`, set
  `PhotonNetwork.AuthValues = new AuthenticationValues(steamId)` where
  `steamId = SteamUser.GetSteamID().m_SteamID.ToString()` (Steamworks.NET is referenced by the
  game). This sends the SteamID as the Photon **UserId** in the Authenticate operation.
- **Server**: the Authenticate handler reads the UserId from the operation parameters
  (`ParameterCode.UserId` / the auth data) and calls
  `AccountService.ResolveOrCreate(steamId, displayName)` → finds or creates `Account` (+`Profile`),
  updates `LastSeenUtc`/`DisplayName`, returns the account.
- **Fallback:** when no SteamID is present, use the nickname or the minted UserId as the
  identity key (marked non-authoritative). This keeps native-LAN-without-mod usable.
- **Ban enforcement:** if `account.IsBanned`, Authenticate returns a non-zero return code and
  the connection is refused.

> The minted auth token (Phase 1) now encodes the resolved SteamId so Master/Game trust the
> identity established at the first authenticating server.

## 6. Bootstrap + console control

- On startup, if **no Account has Level == Console**, ensure a `ServerState.BootstrapCode`
  exists (generate a random ~10-char code if missing, `BootstrapClaimed = false`), and print it
  prominently to the server console.
- The server **console** operates at level Console implicitly. The Host gains a background
  **console command loop** (reads stdin lines):
  - `promote <steamId> <level>` / `demote <steamId> <level>` — set `Account.Level`.
  - `ban <steamId>` / `unban <steamId>` — toggle `IsBanned`.
  - `list` — list accounts (SteamId, name, level, banned).
  - `code` — reshow the bootstrap code.
  - `help`.
- The in-game `/claim <code>` redemption (promotes the caller to Console, sets
  `BootstrapClaimed = true`) and in-game promote/ban commands are **Sub-project 3** (they need
  the chat-RPC command framework). SP1 ships the model, the code, and the console path so the
  server is administrable immediately.

## 7. Integration

- `NameServerHandler` / `MasterServerHandler` / `GameServerHandler` `Authenticate` resolve the
  account via `AccountService` and reject banned accounts.
- The Host constructs the `BlackIceDbContext`/`AccountService` from config, runs migrations,
  performs the bootstrap-code step, and starts the console loop alongside the UDP listeners.
- Existing Phase 1 behavior (connect → room) is preserved; identity resolution is additive.

## 8. Error handling

- DB unavailable at startup → log a clear fatal error and exit (the server needs its store).
- A transient DB error during resolve-or-create → log and refuse that authentication rather
  than crash the listener.
- Malformed/absent identity → fallback path, never fatal.

## 9. Testing

- EF Core **SQLite in-memory** (or a temp file) for `AccountService` tests:
  - First connect creates Account at level Player + a Profile; second connect updates LastSeen,
    does not duplicate.
  - Bootstrap code generated once; a claim sets `BootstrapClaimed` and promotes to Console;
    re-claim is rejected (one-time).
  - `promote`/`demote` change level; `ban` causes Authenticate to refuse.
  - Provider-swap smoke: the same model migrates under the SQLite provider (MySQL provider
    wired but not integration-tested without a MySQL instance).
- Existing 66 tests continue to pass.

## 10. Out of scope (SP1)

- Realm/ruleset persistence and multi-realm advertising (Sub-project 2).
- Chat-RPC decoding, in-game commands, in-game `/claim` (Sub-project 3).
- Gameplay/profile data beyond placeholder fields.
- MySQL integration testing (provider is wired and config-selectable).
