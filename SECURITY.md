# Security Notes

This is an independent, community server for a game whose multiplayer was built on a trusted
client model. Treat every value from a client as hostile. This file tracks the known security
posture and the work required to harden it.

## Identity — proven for verified (public) peers; asserted for LAN

**As of the Steam-ticket-validation work (2026-06-02), public peers' identity is proven.** The Name
Server requires a Steam **game-server auth ticket** from non-LAN peers and validates it via
`ISteamGameServer::BeginAuthSession` (Facepunch.Steamworks, AppID 311800, anonymous logon — no publisher
key). A validated peer's SteamID is signed into the auth token with a `verified` claim, and admin/anti-cheat
code trusts a SteamID's permission level **only when the connection is verified**. Public peers that present
no/invalid ticket **fail closed**. The real validator is the optional `BlackIce.Server.Steam` project; the
default build ships `NullSteamTicketValidator` (Steam-free), so without the Steam build a public server
rejects everyone — run it with the Steam build, or on LAN.

**LAN/loopback peers remain asserted, not proven** (the anonymous dev path): their SteamID is accepted
*unverified* and never granted admin/anti-cheat trust. The server still format-validates
(`SteamId.IsValidIndividual`). Below describes the residual posture for the LAN/asserted path and the
remaining hardening.

**Consequences if exposed to untrusted players:**
- Impersonating another player's account (and thus their permission level).
- Evading a ban by sending a different SteamID.
- Claiming the one-time bootstrap code under an arbitrary SteamID.

**Current mitigations:**
- Anonymous (tokenless / LAN) auth is restricted to loopback/private-range peers
  (`TrustedNetwork.IsLanOrLoopback`).
- Privilege grants (`promote`/`ban`) come from the **server console only** — there is no
  network path to escalate yet.
- Asserted SteamIDs are format-validated; malformed values get a throwaway identity.

**Required before exposing admin to the network (hard prerequisite for SP3 in-game commands
and the `/claim` bootstrap redemption):** prove SteamID ownership via Steam ticket validation.
Options:
1. **Steam game-server auth** — the server runs `steam_api` and validates the client's
   `GetAuthSessionTicket` via `ISteamGameServer::BeginAuthSession`. No publisher key needed;
   the client mod must send the ticket.
2. **Steam Web API `AuthenticateUserTicket`** — needs the game's *publisher* Web API key, which
   a third party cannot obtain for Black Ice. Not viable here.

Until ownership is proven, run the server **privately / with trusted players**, and keep
privilege changes on the console.

## Other notes
- The shared `secret` for HMAC auth tokens is currently a hardcoded placeholder
  (`change-me-platform-sp1`); move it to config/secret storage before any public deployment.
- The repo ships only original code + protocol docs; never commit the game's binaries, the
  decompiled sources, packet captures, or `oplog.jsonl` (which contains live credentials).
