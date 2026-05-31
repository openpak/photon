# Security Notes

This is an independent, community server for a game whose multiplayer was built on a trusted
client model. Treat every value from a client as hostile. This file tracks the known security
posture and the work required to harden it.

## Identity is asserted, not proven (KNOWN LIMITATION)

The player's SteamID arrives as the Photon `UserId` (the client mod reads it from the local
Steam registry and sends it). **The server cannot currently prove the client owns that SteamID**
— a modified client could send any SteamID. The server format-validates it
(`SteamId.IsValidIndividual`) as defense-in-depth, but that does not stop spoofing a *real*
SteamID.

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
