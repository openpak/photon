# Steam Game-Server Ticket Validation — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Prove SteamID ownership server-side (Steam game-server `BeginAuthSession`) so a trusted, non-spoofable
networked identity exists, and gate admin/anti-cheat (the fly/speed exemption) on it.

**Architecture:** A swappable `ISteamTicketValidator` (default `NullSteamTicketValidator`; real
`SteamGameServerValidator` in an optional, conditionally-built `BlackIce.Server.Steam` project using
Facepunch.Steamworks). Ticket validation runs at the **NameServer** auth step (first identity assertion).
Validation is async; the response is marshalled back to the listener thread via a listener action queue
(the existing `OnMaintenance` pattern) — no op-router signature change. The verified SteamID is signed into the
`AuthToken` (`verified` claim) and carried to Master/Game; `PeerConnection` records `SteamId`+`IsVerified`;
the `MovementValidationInterceptor` exempts only verified admins.

**Tech Stack:** .NET 8 (server), Facepunch.Steamworks (optional Steam project), Steamworks.NET (client plugin,
already proven by the spike), xUnit, BepInEx/Harmony (client).

**Spec:** `docs/superpowers/specs/2026-06-02-steam-ticket-validation-design.md`
**Facts:** AppID **311800**; `steam_api64.dll` at `Black Ice_Data/Plugins/x86_64/`.

**Refinements vs spec (from reading the code):** identity originates at `NameServerHandler` (param 225), so
validation goes there (not Master). The async response is sent via a NameServer listener action queue
(reusing the `UdpListener.OnMaintenance` + `AdminActionQueue` marshalling pattern) rather than a router change.

---

## File structure

**Server (core, Steam-free):**
- Create `server/BlackIce.Server.LoadBalancing/Auth/ISteamTicketValidator.cs` — abstraction + result types.
- Create `server/BlackIce.Server.LoadBalancing/Auth/NullSteamTicketValidator.cs` — default (Unavailable).
- Modify `server/BlackIce.Server.LoadBalancing/AuthToken.cs` — add a signed `verified` claim.
- Modify `server/BlackIce.Server.*/PeerConnection.cs` — add `SteamId` + `IsVerified`.
- Modify `server/BlackIce.Server.LoadBalancing/NameServerHandler.cs` — async ticket validation, fail-closed for
  public, LAN bypass; mint verified token; respond via the action queue.
- Modify `server/BlackIce.Server.LoadBalancing/MasterServerHandler.cs` + `GameServerHandler.cs` — set
  `peer.IsVerified`/`peer.SteamId` from the validated token's `verified` claim.
- Modify `server/BlackIce.Server.LoadBalancing/Authority/MovementValidationInterceptor.cs` — exemption predicate.
- Modify `server/BlackIce.Photon/PhotonCodes.cs` — ticket param code.
- Modify `server/BlackIce.Server.Host/ListenersHostedService.cs` + `Program.cs` — wire validator (DI), the
  NameServer action queue drain, the movement exemption, config.
- Modify `server/BlackIce.Server.Core/AnticheatOptions.cs` — `AdminExemptLevel` + (already) `Enforce`.

**Server (optional Steam project):**
- Create `server/BlackIce.Server.Steam/BlackIce.Server.Steam.csproj` — references Facepunch.Steamworks; built
  only when `-p:SteamEnabled=true` (or the package restores), like `PhotonOracleDll`.
- Create `server/BlackIce.Server.Steam/SteamGameServerValidator.cs` — real `BeginAuthSession`.

**Client:**
- Create `plugins/BlackIce.SteamAuth/BlackIce.SteamAuth.csproj` + `SteamAuthPlugin.cs` — mint + send the ticket.

**Tests:**
- Create `server/BlackIce.Server.Tests/Auth/SteamTicketAuthTests.cs` — FakeValidator: verified/rejected/timeout,
  LAN bypass, verified-token round-trip.
- Create `server/BlackIce.Server.Tests/Auth/AuthTokenVerifiedClaimTests.cs` — claim round-trip + tamper.
- Modify `server/BlackIce.Server.Tests/Authority/...` — movement exemption test.

---

## Tasks (TDD, commit after each)

**Task 1 — `ISteamTicketValidator` + `NullSteamTicketValidator`.** Interface
`Task<SteamAuthResult> ValidateAsync(byte[] ticket, ulong assertedSteamId, CancellationToken)`; result enum
`Verified|Rejected|Unavailable`. Null returns `Unavailable`. *Test:* Null → Unavailable.

**Task 2 — `AuthToken` signed `verified` claim.** Add `Mint(userId, bool verified, secret)` (body `userId|0/1`,
HMAC over the body) and `record struct AuthIdentity(UserId, Verified)`; `Validate` returns
`Result<AuthIdentity>`; legacy `userId.sig` tokens validate as unverified (back-compat). *Tests:* flag
round-trips; user-id recovered; tampered flag fails (sig is over the body); legacy token → unverified. Update
the `Validate(...).TryGet(out var steamId)` call sites in Master/Game + existing auth tests.

**Task 3 — `PeerConnection.SteamId` + `IsVerified`.** Plain fields set during auth; trusted by gating only
when `IsVerified`.

**Task 4 — NameServer async ticket validation (core).** Inject `ISteamTicketValidator`, a post-to-listener
`Action<Action>` (drained in `UdpListener.OnMaintenance`, the `AdminActionQueue` pattern), and an
`isLan` predicate. Add ticket param to `PhotonCodes.Param` (prefer PUN's 217 custom-auth-data if the client
sets `AuthValues.AuthData`, else 220). LAN+anon → unchanged, `Mint(verified:false)`. Public → ticket required;
`ValidateAsync` (3s timeout); Verified → ban-check + `Mint(verifiedId, verified:true)` rc 0; else rc -1
(fail-closed). Response posted to the listener thread, then `peer.SendResponse`. *Tests* (FakeValidator):
verified→rc0+verified token; rejected/absent/timeout→rc-1; LAN→rc0+unverified.

**Task 5 — Master/Game honor the claim.** On token validate, set `peer.SteamId = ident.UserId;
peer.IsVerified = ident.Verified`. *Test:* a verified token marks the peer verified; an anon/legacy one does not.

**Task 6 — `MovementValidationInterceptor` exemption.** Add an injected
`Func<string room, int actor, bool> isMovementExempt`; when it returns true, always `Forward` (skip
enforcement). Host wires it to: room session → actor → `PeerConnection` → `IsVerified && AccountService
.Find(SteamId)?.Level >= AnticheatOptions.AdminExemptLevel`. *Tests:* verified-admin actor exempt; verified
non-admin enforced; unverified enforced.

**Task 7 — `AnticheatOptions.AdminExemptLevel`** (default `PlayerLevel.Admin`) + host config; ensure
`Enforce` can be turned on for movement. *Test:* option round-trips through config.

**Task 8 — Host wiring.** DI `ISteamTicketValidator` (Null by default; real if the optional project +
`SteamEnabled`/env present). Give the NameServer listener an action queue drained in `OnMaintenance`. Wire the
movement exemption predicate (accounts + room registry). Boot still green, Steam-free. *Verify:* full suite +
boot.

**Task 9 — optional `BlackIce.Server.Steam` real validator (Facepunch.Steamworks).** New csproj referencing
Facepunch.Steamworks, conditionally built (`Condition` on `SteamEnabled==true`, like `PhotonOracleDll`).
`SteamGameServerValidator : ISteamTicketValidator`: `SteamServer.Init(311800, anonymous)`, pump callbacks,
`BeginAuthSession` → `OnValidateAuthTicketResponse` completes a TCS keyed by SteamId → `EndAuthSession`
(try/finally). Host selects it when present. *Validation:* manual/integration (needs Steam) — cannot run in
the Steam-free CI suite.

**Task 10 — client `plugins/BlackIce.SteamAuth`.** Extends the proven spike: on PUN auth, mint
`GetAuthSessionTicket`, await the OK callback, inject bytes into `AuthenticationValues` (Harmony patch the
auth-values build / connect). Auto-deploys to `BepInEx/plugins`. *Validation:* manual in-game.

**Task 11 — security review + docs.** Run the `security-reviewer` subagent on the auth/identity/token diff.
Update `CLAUDE.md` SECURITY GATE status + `SECURITY.md` (identity now proven for verified peers; note the HMAC
secret must still move to config before public deploy). Re-enable the fly/speed plugin's reliance on the
server exemption (it no longer self-gates; the server enforces).

## Self-review

- **Spec coverage:** validator abstraction (T1,9), verified identity end-to-end (T2-5), public-required/LAN-
  exempt/fail-closed (T4), movement gating (T6-8), client plugin (T10), Steam-free build/tests (T1,8,9),
  security review + docs (T11). ✓
- **Type consistency:** `SteamAuthResult`/`SteamAuthOutcome`, `AuthIdentity(UserId,Verified)`,
  `ISteamTicketValidator.ValidateAsync`, `isMovementExempt(room,actor)` used consistently across tasks. ✓
- **No router change** (refinement): async realized via the listener action queue; existing handlers untouched. ✓
- **Out of scope (noted):** networked promote/ban, `/claim`, HMAC-secret-to-config (tracked).
