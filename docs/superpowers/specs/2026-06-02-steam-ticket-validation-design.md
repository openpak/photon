# Steam Game-Server Ticket Validation — Design

**Date:** 2026-06-02
**Status:** design (pending implementation plan)
**Closes:** the SECURITY GATE in `CLAUDE.md` / `SECURITY.md` — "Identity is asserted, not proven."

## Problem

The player's SteamID arrives as the Photon `UserId`, read from the local Steam registry by the client
mod. The server cannot prove the client owns it, so a modified client can send any SteamID — enabling
impersonation, ban evasion, and bogus privilege levels. Consequently all admin/privileged actions are
**console-only**, and the new client-side fly/speed movement plugin cannot be gated by admin level (a normal
user could install it and cheat, and the server has no trustworthy way to exempt real admins).

The client-side feasibility spike (`plugins/BlackIce.SteamTicketSpike`) already proved a BepInEx plugin can
mint a Steam-validated `GetAuthSessionTicket`. The remaining piece is **server-side validation** via
`ISteamGameServer::BeginAuthSession`, plus wiring the proven identity into the auth handshake and the
admin/anti-cheat gating.

## Goals

1. Establish a **trusted, non-spoofable networked SteamID** for public connections.
2. Wire that verified identity into admin-level checks and the `MovementValidationInterceptor`, so a player is
   exempt from movement enforcement (i.e. allowed to fly/speed) **only if verified AND account level ≥ Admin**.
3. Keep the server build and the full test suite **Steam-free** (Steam is an optional, conditionally-built
   dependency, mirroring the optional Photon-oracle pattern).
4. Preserve the existing **anonymous-LAN** path for dev/CI/offline play.

## Non-goals (unblocked by this work, but separate follow-ups)

- Networked `promote`/`ban` and the `/claim` bootstrap redemption (these become *possible* once identity is
  trusted, but are out of scope here).
- Replacing the placeholder HMAC secret (tracked in `SECURITY.md`; must move to config before public deploy —
  the verified-identity claim relies on token integrity, so this is noted as a hard pre-deploy prerequisite).

## Decisions (from brainstorming)

| Decision | Choice |
|---|---|
| Scope | End-to-end: server validation **and** a production client plugin that sends the ticket |
| Trust policy | Ticket **required for public** (non-LAN) peers; **LAN/loopback exempt** (anonymous path retained) |
| Server Steam library | **Facepunch.Steamworks** (.NET-native `SteamServer` API; client stays on Steamworks.NET) |
| Async auth | Add a **`Task<OperationResponse>`** path to the op router for the auth op (await the verdict) |

Known facts: Steam **AppID = 311800**; `steam_api64.dll` ships at
`Black Ice_Data/Plugins/x86_64/steam_api64.dll`.

## Architecture

### 1. `ISteamTicketValidator` (abstraction — `BlackIce.Server.LoadBalancing` or `.Core`)

```
enum SteamAuthOutcome { Verified, Rejected, Unavailable }
record SteamAuthResult(SteamAuthOutcome Outcome, ulong SteamId, string? Reason)
interface ISteamTicketValidator {
    Task<SteamAuthResult> ValidateAsync(byte[] ticket, ulong assertedSteamId, CancellationToken ct);
}
```

- **`NullSteamTicketValidator`** (default; core/test/LAN) — always returns `Unavailable`. No Steam dependency.
- **`SteamGameServerValidator`** (real) — lives in a new **conditionally-built** `BlackIce.Server.Steam`
  project that references **Facepunch.Steamworks** + `steam_api64.dll`. It:
  - Initializes `SteamServer` for AppID 311800 with an anonymous logon (no publisher key — per SECURITY.md
    option 1), on host startup; pumps callbacks on a dedicated thread.
  - On `ValidateAsync`: registers a `TaskCompletionSource` keyed by the asserted SteamId, calls
    `BeginAuthSession(ticket, steamId)`, and completes the TCS from the `OnValidateAuthTicketResponse`
    /`SteamServer.OnValidateAuthTicketResponse` callback. Always calls `EndAuthSession` afterward.
  - Returns `Verified(steamId)` only on the Steam `OK` response; `Rejected(reason)` otherwise.
  - The project is excluded from the build (host falls back to `NullSteamTicketValidator`) when the Steam
    native deps/AppID aren't present, exactly like `PhotonOracleDll`.

### 2. Auth handshake (`MasterServerHandler.OpAuthenticate`)

- A new Photon auth param (`PTicket`, a `byte[]`) carries the client's ticket.
- **LAN/loopback + `AllowAnonymousLan`** → existing anonymous path, unchanged; identity is **not** marked
  verified (so it stays console-only for admin).
- **Public peer** → the ticket is **required**:
  - Missing ticket → auth fails (`-1`).
  - `ValidateAsync` → **Verified(steamId)** → mint the `AuthToken` over the **verified** SteamId (the asserted
    `UserId` is ignored) with a signed **`verified`** claim; respond OK + `UserId` = verified.
  - **Rejected / Unavailable** → auth fails (`-1`) — **fail closed**.

### 3. Async response path (op router)

Add an async variant so a handler can return `Task<OperationResponse>`; the listener awaits it and sends the
response when it completes, with a **~3s timeout → reject**. Scoped to the auth op; the existing synchronous
handlers are unchanged. The Steam callback (on the validator's callback thread) completes the awaited task;
the send is marshalled back to the listener thread.

### 4. Verified identity propagation & gating

- The `AuthToken` gains a signed **verified** flag alongside the userId, so the Game-server hop can
  distinguish a Steam-proven identity from an anon-LAN one. The `PeerConnection`/room membership records
  `SteamId` + `IsVerified`.
- **Account level is trusted only for verified connections.** `AccountService.Find(steamId)?.Level` is
  honored for gating only when `IsVerified`; an unverified (anon-LAN) peer is treated as `Player`.
- **`MovementValidationInterceptor`** gains an exemption predicate: skip enforcement for a (room, viewId)
  whose owning connection is **verified AND level ≥ a configured threshold (default `Admin`)**. Everyone else
  is enforced — illegitimate speed/teleport/fly is dropped (snap-back). This is the mechanism that lets an
  admin use the fly/speed plugin while a normal user cannot, regardless of what client they run.

### 5. Client production plugin (`plugins/BlackIce.SteamAuth`)

Extends the proven spike: on the game's PUN authentication, mint `GetAuthSessionTicket`, wait for the
`GetAuthSessionTicketResponse_t` `OK`, and inject the ticket bytes into PUN's `AuthenticationValues` custom
auth params (Harmony patch on the auth-values construction / connect call) so the real `OpAuthenticate`
carries the ticket. Reuses the spike's Steamworks.NET calls. Auto-deploys to `BepInEx/plugins`.

## Data flow

```
client plugin mints ticket
  → NameServer → Master OpAuthenticate { UserId(asserted), ticket }
  → server BeginAuthSession(ticket) → Steam ValidateAuthTicketResponse (verified SteamId)
  → Master mints AuthToken over VERIFIED SteamId (verified=true) → response OK
  → client → Game OpAuthenticate { token } → Game trusts verified identity
  → admin/anti-cheat (movement exemption, future networked admin) honor it
```

## Error handling

- Steam unreachable / validator `Unavailable` + public peer → **reject** (fail closed). LAN unaffected.
- Malformed/rejected ticket → auth `-1`.
- Validation callback timeout (~3s) → reject.
- `SteamServer` init failure at startup → log a clear warning, host uses `NullSteamTicketValidator` → public
  auth rejected (fail closed), LAN still works.
- `EndAuthSession` always called (try/finally) so sessions don't leak.

## Testing

- **Steam-free suite stays green.** Auth-path tests use `NullSteamTicketValidator` and a `FakeValidator`
  returning `Verified`/`Rejected`/delayed (timeout). The real `SteamGameServerValidator` lives in the
  optional project, excluded when Steam deps are absent.
- New tests:
  - Public auth without a ticket → rejected; with a `Verified` fake → OK + token carries the verified SteamId.
  - `Rejected`/`Unavailable`/timeout → auth fails, no token.
  - LAN/loopback anonymous path unchanged; anon identity is **not** marked verified.
  - `AuthToken` round-trips the `verified` flag and is tamper-evident (HMAC).
  - `MovementValidationInterceptor` exempts a verified-admin (room,viewId) and enforces everyone else.
- **Security review:** run the `security-reviewer` subagent on the auth/identity/token changes before merge.

## Component boundaries

- `BlackIce.Server.Steam` (new, optional): the only place that references Facepunch.Steamworks /
  `steam_api64.dll`. Implements `ISteamTicketValidator`. No other project depends on Steam.
- `MasterServerHandler` / op router: gain the async auth path + ticket param; depend only on
  `ISteamTicketValidator` (DI).
- `AuthToken`: gains the signed `verified` claim.
- `MovementValidationInterceptor`: gains the exemption predicate (depends on a verified-level lookup, injected).
- `plugins/BlackIce.SteamAuth` (new client): mints + sends the ticket.
