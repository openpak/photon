# Changelog — photon

Generated from git history on 2026-09-15. `git log` stays the source
of truth; this file is the readable summary.

## Unreleased

- docs(protocol): knownPlayers is SHA-1 of the player's nickname [3b2206d]
- fix(lb): a player's Photon UserId must survive a re-login [458fadc]
- README: a tag is the deploy [31b8434]
- Release workflow like every other OpenPak service: tag → ghcr, amd64 and arm64 [53fa261]
- Stop tracking the shared-docs symlink: it dangles outside the Openpak workspace [a875248]


## v0.1.1 — 2026-09-08



## v0.1.1 — 2026-09-08

- A health port, because UDP cannot answer for itself [11a0e62]
- Container image: built by hand, since Fusion's assemblies cannot live in the repo [3993f6a]
- Publish rooms to the nx-baas game API (OPENPAK_BAAS_URL) [33612d7]
- Per-title settings out of the code: OPENPAK_TITLE_ID, OPENPAK_FUSION_PAGE_KB [cb6b9b9]
- Rename BlackIce -> OpenPak, drop the PC-client side, configurable loopback master [e6a1eb9]
- OpenPak port: AGPL-3.0, account-agnostic room presence [a5b5b12]
- NameServer/Master: hand 127.0.0.1 only to loopback peers; LAN peers get the advertised host [94672b1]
- fix(core): keep one pending UDP receive across loop iterations -- cancelled receives leaked ~6M socket ops [1311ba5]
- fix(fusion): disconnect leavers from the room sim and honour client despawns [56c8c68]
- fix(fusion): apply internal messages, handle unsequenced eNet fragments, keep player links — fresh and returning joins work [18696ea]
- fix(fusion): key room-sim tick ownership on the listener loop, not a thread id [39dbda3]
- feat(fusion): target Fusion 2.0.8 + plaintext session events — two players in-game [2e6c97c]
- feat(fusion): host Fusion Shared-mode server for two-peer relay [faf6f9b]
- Fusion join: advertise host custom props (version/scene); eviction + timeout hardening [6e64f4b]
- feat: implement and ground-truth-verify Fusion's Join/Start bootstrap [83e369b]
- fix: four bugs blocking a real Switch client through full LoadBalancing [153b9c9]
- feat(lb,photon): GetRegions, response encryption, second NameServer port [72b0618]
- test(lb): full arena pipeline through the real relay (RelayFrom -> killfeed -> arena -> respawn) [348912f]
- feat(lb): arena scores real deaths and respawns at round reset [7e2fc03]
- feat(lb): killfeed detects real deaths (KilledPlayerRemote), retires HP-summing [23412b8]
- feat(lb): ServerRpc Teleport/BecomeTangible respawn builders [68e3f68]
- feat(photon): PunRpcInfo resolves shortcut RPCs and exposes args [c4dfafd]
- feat(core): ArenaOptions respawn toggle + spawn point [f2349db]
- feat(lb): KillBus death channel (DeathNotice/Died) [1c048eb]
- feat(photon): RpcShortcuts table to resolve shortcut-indexed RPCs [c2d7f7b]
- refactor(photon): single Vector3 custom-type factory, reused by bots [9b0622e]
- docs(plan): real arena down-and-respawn implementation plan [9d5fc1b]
- docs(spec): real arena down-and-respawn design [d5561c2]
- docs(protocol): map the live player death & respawn RPC sequence [f7ff6a0]
- docs(protocol): record live-capture findings; oplogger __type cleanup [a0949cd]
- feat(oplogger): full payload decode for live protocol-verification (v0.2) [69e5be4]
- feat(debugvisuals): /dbg top — log the 10 costliest renderers by name [d011f65]
- feat(plugin): perf heatmap (/dbg perf) + chat suppression covers all PUN RPC overloads [8f16d4e]
- fix(plugin): debug visuals via /dbg chat commands (not keybinds) + render fixes [bff71cf]
- feat(plugin): client debug-visual overlays — grid, navmesh, colliders, entity markers [35e05c6]
- fix(chat): chunk long /command replies into multiple ServerMessages (oversized line disconnected the client) [d28a779]
- fix(data): AccountService.Find reads fresh (AsNoTracking) so live promote/ban is seen by listeners [aa70e47]
- security: gate LAN chat-admin elevation behind an explicit TrustLanAdmin opt-in (default off) [c6a3061]
- fix(chat): trusted-LAN operators get their account level in chat (verification only runs on public peers) [3884685]
- feat(chat): in-game /commands gated by verified access level, with /help and /commands [4717c65]

- … 101 earlier commits omitted (see `git log`)
