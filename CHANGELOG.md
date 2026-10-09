# Changelog — photon

Generated from git history on 2026-09-15. `git log` stays the source
of truth; this file is the readable summary.

## v0.12.0 — 2026-10-09

**Game versions are kept apart, as on Photon Cloud.** Rooms used to be keyed by name alone, so two builds
of one game that picked the same room name or session code landed in the same room. Every room is now
keyed by (AppId, AppVersion, name): two builds never see or join each other's rooms, and two clients of
the same build behave exactly as before. Authentication is never refused over a version.

- feat(lb): **rooms are keyed by the client's AppId (224) and AppVersion (220)** through
  `RoomRegistry.KeyFor`: Master CreateGame/JoinGame, the Fusion session-code lobby, the realm list's player
  counts and the Game-server join all resolve the room in the client's own build. A client that sends
  neither keeps the bare name as its key. The room's public name is unchanged for the client, nx-baas
  presence, federation, the admin console and logs (`RoomRegistry.PublicName`).
- feat(lb): **the token carries AppId and AppVersion.** Live Outbound and Moving Out 2 sessions name
  their build only to the Name Server and re-authenticate on the Master and Game servers with the token
  alone, so neither role ever saw param 224 or 220. The Name Server (and the LAN-mode Master) now seal
  both into the token; the old two-field token still validates.
- fix(account): **room presence is filed under the right title.** `PhotonTitles` mapped AppIds to titles,
  but the Game server never had an AppId (see above), so every room was published under the default
  title. It now has the one from the token.
- feat(federation): `RoomReport` carries `AppId`/`AppVersion`, so home files a node's room in its build.
- refactor(console): room commands resolve through `RoomRegistry.ResolveName` (public names, dash/case
  tolerant); `rooms` shows each room's AppVersion. The duplicate resolver in `ServerCommands` is gone.
- docs: `docs/protocol/01-connection-flow.md` and `docs/plugins.md` (plugin `RoomName` is the registry key).

## v0.11.0 — 2026-10-03

**Fusion is hosted by our own code, and the server needs no retail assemblies.** The Shared-mode host
that linked the retail `Fusion.Runtime.dll` (behind a hand-written Unity stub and a Cecil patch) is
replaced by a clean-room implementation specified in `docs/protocol/07-fusion-relay-transport.md` and
`08-fusion-shared-simulation.md`. The released image is complete on its own.

- feat(fusion): **clean-room relay transport** (`OpenPak.Server.LoadBalancing/Fusion`) for the event-101
  packets: Connect/Accepted/NotifyData, acks, fragmentation and reliability, per room and per peer.
- feat(fusion): **clean-room Shared-mode simulation** replaces the retail host: object and struct
  replication, master election, RPC and internal-message relay, spawn and despawn. Wired
  unconditionally in `ListenersHostedService`.
- fix(fusion): **a leaver's objects are destroyed** (or lose their state authority when they are not
  meant to be destroyed), instead of staying in the room for the others.
- fix(fusion): **the Photon Leave event names the new master**, in step with `RuntimeConfig`.
- fix(fusion): `FusionRelayProtocol` packet-type constants corrected.
- feat(config): `Server.Fusion.SceneAuthorityMasterClient` (default off) gives SceneInfo/PhysicsInfo to the
  master client instead of None; see `docs/configuration.md`.
- chore(fusion): removed `OpenPak.Server.Fusion` (the DLL-backed host), `OpenPak.Server.Fusion.UnityStub`,
  `tools/fusion-patcher`, the `FusionAvailable`/`FUSION_LIBS` build switch and `server/fusion-libs/`.
  `release.yml` no longer stages libs from the runner, so a missing copy can no longer fail a release.
  `OPENPAK_FUSION_PAGE_KB` no longer exists (no heap blocks). The harnesses that need the retail DLLs
  as test instruments moved to the workspace's `scratch/`.
- docs: `docs/fusion-shared-server.md` rewritten for the clean-room host; the Fusion version for Outbound
  is 2.0.8 (build 2.0.8.1402), not 2.1.x.

## v0.10.0 — 2026-09-28

**A second player can join a ClientServer session.** Moving Out 2's joiner reached the room for the
first time, completed Fusion's handshake, and then sat on "joining crew" for ever. Reproduced without
the game — `scratch/mo2/fusionhost` now runs a real Fusion 1.1.12 **client** beside a real host — and
the same run passes against this build: `StartGame -> Ok=True`, both sides list two players, and the
client's simulation ticks.

- fix(fusion): **the topology belongs to the room.** Only the creator's JoinGame carries parameter
  213. Kept per peer, a joiner had none, was treated as a Shared-mode peer and told
  `ConnectToShared`. A joiner now inherits the room's topology and its host actor.
- fix(fusion): **a client is told who its server is.** `Start.RemoteServerID` is the host's actor,
  not 0.
- feat(fusion): **`ReflexiveInfo` (message 3).** A `GameMode.Client` only ever connects from
  `HandleReflexiveInfoMessage`, on the message whose ActorNr is that RemoteServerID. A joiner is
  asked for its own (`JoinRequests.ReflexiveInfo`), which it sends once its runner exists; that is
  answered with the host's, addresses unset, so the client connects over the relay. The host is
  told who is coming.
- feat(fusion): **relay traffic (101) is carried between the peers** of a ClientServer room, to the
  actors each packet names. It used to be handed to a hosted simulation that a session with a real
  host does not use.
- chore(unitystub): `Behaviour.isActiveAndEnabled`, `Physics.Simulate`, `Physics.SyncTransforms`,
  for the headless harness.

## v0.9.0 — 2026-09-27

Moving Out 2 entered its room and was dropped a second later, with the client resending its 2,814-byte
`NetworkConfigSync` six times. Both causes are now measured, reproduced locally against Photon's own
client library, and fixed.

- fix(fusion): **build a room's Fusion simulation off the listener thread.** Allocating its heap takes
  ~300 ms locally and ~850 ms on the production box, and it ran inside `PlayerJoined` — on the single
  Game listener loop. For that whole window the server read no datagrams and acknowledged nothing, so the
  client (whose retransmit timer is ~100 ms) resent its fragmented config sync until it gave up. The
  trace made it unambiguous: nine datagrams the client stamped 200 ms apart were all received inside
  3 ms, once the thread came back. The simulation is now built on a task and published when ready, with
  the joins that arrived meanwhile applied to it; the owning listener loop is still captured on that loop,
  so ticking is unchanged. Measured after: **0 retransmits and one copy of the config sync** instead of
  six, with the real client library.
- fix(fusion): **never send a peer a PlayerRefMapping for itself, and hold back the others until its
  runner exists.** The client's `HandleStartMessage` awaits `NetworkRunner.Initialize` — which is what
  creates its simulation — while `HandlePlayerRefMapping` calls
  `_runner._simulation.RegisterUniqueIdPlayerMapping(...)` with no null guard. Ours arrived 1 ms after
  Start, inside that window, on a null simulation. A peer never needs its own mapping anyway (the Join
  Confirmation already carries its PlayerRef and UniqueId), and another player's is now released on the
  first Fusion packet the peer sends after its Start, which proves the runner is up.

## v0.9.3 — 2026-09-27

Found by running a **real Fusion 1.1.12 host** headlessly against this server
(`scratch/mo2/fusionhost`, built today from the SDK): it joined, was answered, and never got a Join
Confirmation back — because our decoder read Fusion 2 fields out of a Fusion 1 Join.

- fix(fusion): **gate the Join's Fusion 2 fields on the peer's serialization major, not its protocol
  version.** `PlayerRef`, `EncryptionKey`/`EncryptionKeySecret` and `PlayerCounterRef` do not exist in
  a Fusion 1.x Join at ANY protocol version — its body ends at `UniqueId`. A 1.x client using the
  default `V1_6_0` (10) therefore had 24 bytes read as if they held four more fields: the decode ran
  off the end, returned null, and the peer was never answered. Moving Out 2 escaped it only because it
  uses `V1_2_3` (6). The Confirmation encoder is gated the same way, so a 1.x peer is not sent fields
  it would read our CustomData out of. With this, a stock 1.1.12 host now completes the whole
  handshake against us and reports **"Fusion Simulation Startup Done."**, `StartGame Ok=True`, and
  stays running — so the server side of a ClientServer host session is correct.
- feat(fusion): log the client's `NetworkConfigSync` as decoded JSON at Debug. It is the only
  description a Fusion client gives of its own setup (tick rate, heap pages, object counts), and it can
  be replayed into a stock host of the same SDK version to test whether a config alone reproduces a
  failure.
- The UnityEngine stub gains `partial` on a few types and drops an over-strict `new()` constraint on
  `GameObject.AddComponent<T>` (Unity constrains it to `Component` only; the extra constraint rejected
  Fusion's own call sites). No behaviour change for the server; it lets a harness compile the same stub
  source against a different Fusion version.

## v0.9.2 — 2026-09-27

- fix(fusion): **never send PlayerRefMapping to a Fusion 1.x peer.** Message id 8 is PlayerRefMapping
  only in Fusion 2; in Fusion 1.x it is `DummyTrafficSync`. Moving Out 2 runs **Fusion 1.1.8**, and
  running our 35-byte mapping through the Fusion 1.1.12 SDK's own deserializer produced *two*
  DummyTrafficSync messages — the first telling the client to send dummy traffic every 100 ms. The
  peer's serialization version comes in its own Join, so that is the gate. `docs/fusion-protocol-map.md`
  now carries the 1.x registry and what else 1.1.12 does differently (its `ConfirmJoin` has no timeout,
  `Initialize` only resolves true, and a Host's Start resolves the cloud operation immediately) — all
  read from the SDK, not inferred.

## v0.9.1 — 2026-09-27

- fix(fusion): hold relay packets (event 101) that arrive while a room's simulation is still being
  built, and replay them into it in order — a client that brings up its relay transport the moment it
  is started no longer has to rely on Fusion's own Connect retries. Bounded at 256 packets per room.
  Follows v0.9.0, where that build moved off the listener thread.

## v0.8.0 — 2026-09-27

- fix(transport): **fragment outgoing messages over the MTU.** Photon's client reads every datagram
  into a buffer exactly one MTU long (`SocketUdp.ReceiveLoop`: `new byte[MTU]`, default 1200), so a
  bigger datagram is truncated and then thrown away by its command parser — measured against the real
  client library, which logged `Exception while reading commands from incoming data:
  System.ArgumentException` on a 4,035-byte response and never acknowledged it. This server had no
  outgoing fragmentation, so **any** reliable message over ~1.15 KB was silently unreadable by every
  client. `EnetPeer.WrapReliableFragmented` now splits such a message into type-8 fragments with
  consecutive sequence numbers, `UdpListener` packs at most one MTU per datagram, and an oversized
  datagram is logged as a warning. Verified with the real client: the 4 KB response now arrives
  reassembled.
- docs(fusion): `docs/fusion-protocol-map.md` records what the client itself requires, measured with
  its own library instead of inferred — our fragment ACKs are correct (confirmed both against the
  library and against Moving Out 2's decompiled `RemoveSentReliableCommand`, Photon 4.1.6.23), and our
  Join Confirmation / Start / PlayerRefMapping deserialize correctly in the SDK's own
  `ProtocolSerializer`. The remaining transport gap is named: the server never retransmits its own
  unacknowledged reliable commands, and the client dispatches a channel strictly in sequence order.

## v0.7.0 — 2026-09-27

- fix(core): **acknowledge a packet's reliable commands before doing its application work.**
  `PeerConnection.HandlePacket` queued the ACKs and sent them only after handling every payload in
  the packet — and that work can take most of a second, because a Fusion Join creates the room's
  whole simulation (heap pages, SDK init). eNet retransmits an unacknowledged reliable command on a
  timer, so Moving Out 2's client resent its Join and its 2.8 KB `NetworkConfigSync` until the peer
  was dropped ~3 s after entering the room. Measured from the trace: an ACK **830 ms** behind the
  command it answered (`03.084 RECV seq=1` → `03.914 SEND Acknowledge seq=1`). Now the ACKs for a
  packet leave first, then the lifecycle callbacks, then the payloads in arrival order. Every title
  benefits; this one could not get past it.

## v0.6.1 — 2026-09-27

- docs: **`docs/fusion-protocol-map.md`** — Fusion's session protocol transcribed from the SDK
  (`Fusion.Sockets.dll` + `Fusion.Runtime.dll`, decompiled): the message registry and every body in
  serialization order, the version header and its major-version gate, all the enums
  (`PluginGameMode`, `PeerMode`, `JoinRequests`, `StartRequests`, `ProtocolMessageVersion`,
  `Topologies`), and the client's own join/start state machine with the exact shutdown strings.
  Two things it settles: a **ClientServer peer never connects to actor 0**, so `fed=0`/`emitted=0` on
  such a room is correct rather than a symptom; and `ConnectToShared` is read only in the Shared
  branch, so it means nothing to a ClientServer client.
- fix(fusion): the Join Confirmation echoes the peer's own `PeerMode` instead of asserting `Client`.
  A ClientServer host announces itself as `PeerMode.Server`.

## v0.6.0 — 2026-09-27

- feat(fusion): **host the room's simulation in the topology the client asked for.** `Topologies` in
  `Fusion.Runtime` is `{ ClientServer = 1, Shared = 2 }` (decompiled), and this server built every
  simulation as Shared. Outbound is Shared and unaffected; Moving Out 2 asks for ClientServer — a
  dedicated-server session, which on Photon Cloud the plugin hosts — and was being given a Shared one.
  `FusionServerFactory.CreateServer` now takes the topology, `IFusionSessionHost.PlayerJoined` carries
  it from the JoinGame parameter, and a ClientServer client is answered with `StartRequestsNone`
  instead of `ConnectToShared` (that flag brings up Fusion's own relay transport to the plugin, which
  is a Shared-mode arrangement).
- Every earlier combination was measured on the title: ConnectToShared with a Shared simulation held
  the room ~5 s, either flag with no simulation ~1 s, no answer at all 15 s. Pairing the requested
  topology with a simulation in that topology is the combination that had never been tried.

## v0.5.3 — 2026-09-27

- fix(lb): host the Fusion simulation for a ClientServer room too — back to the behaviour that
  measured furthest. The full matrix on Moving Out 2: answered with nothing, it sends one Join Request
  and times out after 15 s; answered without a hosted simulation it drops the room in ~1 s (either
  Start flag); answered with a hosted simulation it lasts ~5 s and exchanges real traffic. It wants a
  server simulation talking to it, which fits what ClientServer means in Fusion — a **dedicated
  server** session, hosted on Photon Cloud by the plugin. Hosting one in ClientServer topology rather
  than Shared is the next piece of work; the topology is recorded and logged
  (`PeerRoomState.FusionTopology`) for it.

## v0.5.2 — 2026-09-27

- fix(lb): a ClientServer peer is answered again, with the flag that measured best. Three
  observations of Moving Out 2 bracket it: Join Confirmation + `Start(ConnectToShared)` with a hosted
  Shared simulation held its room ~5 s; `Start(StartRequestsNone)` with no simulation dropped it in
  ~1 s; answering nothing at all left the client sending one event and timing out after 15 s of
  silence. So the answer is required and `ConnectToShared` is the one it accepts. What ClientServer
  changes is the rest: no hosted simulation for its room, and its event-100/101 traffic reaches the
  other actors instead of being consumed by a simulation that was feeding and emitting nothing.

## v0.5.1 — 2026-09-27

- fix(lb): a **ClientServer** peer is not answered with Fusion server protocol at all. The Join
  Confirmation and Start this server sends exist to stand in for Photon Cloud's plugin at actor 0,
  which only exists in Shared topology; a ClientServer client *is* the server and never asked for
  them. Sending them made Moving Out 2 drop the room one second after entering it — faster than the
  five it lasted with only the flags wrong. Its event-100 traffic is now relayed to the other actors,
  which is what plain Photon does, and nothing is injected.

## v0.5.0 — 2026-09-27

- feat(lb): **honour the Fusion topology the client asks for** (JoinGame parameter 213). Outbound
  joins in `Shared`, where the authoritative simulation belongs to Photon Cloud's plugin and
  `OpenPak.Server.Fusion` hosts it in its place. Moving Out 2 joins in `ClientServer`, where the host
  *client* owns the simulation and the server is only a relay — it was being told `ConnectToShared`,
  fed into a hosted Shared simulation and answered as actor 0, and dropped the session about five
  seconds after joining (`fed=0 emitted=0` on the room, then `eNet DISCONNECT`). A `ClientServer`
  peer now gets `StartRequestsNone`, is not announced to the hosted simulation, and its event-101
  relay traffic is not swallowed. A client that names no topology keeps the Shared behaviour, so
  nothing else changes.

## v0.4.0 — 2026-09-25

- feat(lb): a room is filed under the title whose Photon **AppId** its peer authenticated with
  (param 224, `OPENPAK_PHOTON_TITLES=<appid>=<titleid>,…`), not under one hardcoded
  `OPENPAK_TITLE_ID`. One server already hosts more than one game, and Moving Out's rooms would
  otherwise have been published as Outbound's. `OPENPAK_TITLE_ID` stays as the fallback for a
  client that sends no AppId or an unmapped one.
- docs: Moving Out (Switch, PUN 2, AppId `a956fef2-…`, name server `ns.exitgames.com`) is a title
  this server can serve. Moving Out 2 is not: it is gated behind Team17's own backend.

## v0.3.1 — 2026-09-24

- ci: release builds carry the Fusion relay: the workflow stages the libs from the self-hosted
  runner's own copy and fails when any is missing, so a tag deploys again. First release that
  reaches prod since the hand-built `sha-3b2206d`; it brings the v0.3.0 ban refusal below.

## v0.3.0 (never deployed: built without the Fusion libs)

- feat(lb): an OpenPak account banned in the admin is refused at the Name Server with the
  existing rc=-3 "Account banned". An account-identity login (`nex:` / `bsdid:`) waits for the
  Switch adapter's `/internal/photon-identity`, which answers 403 `account_banned` for it
  (website/docs/ban-lookup.md). No adapter configured, or an adapter outage, is not a ban. Live
  rooms are not re-checked; the refusal lands at the next Name Server login. No new settings.
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
