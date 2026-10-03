# Hosting Fusion's Shared-mode server (Outbound)

Outbound uses **Photon Fusion 2** in **Shared** topology. In Shared mode every client is a Fusion
*client*; the authoritative *server* simulation runs inside Photon Cloud's closed **Fusion plugin**.
There is no relay-only mode: a client sends its state to actor 0 (the plugin) and expects a real
Fusion server to hold the world state and replicate it to the other clients. A plain relay that only
forwards packets between peers does **not** work — the earlier solo-host path only survived because a
lone client happily accepts its own keepalives echoed back as if from the server.

Photon does **not** ship the Fusion plugin in the Server SDK (confirmed by Photon staff), and Photon
Server itself is Windows-only and license-gated. So to self-host Outbound we implement the plugin's
duties ourselves, clean-room, from the wire.

## How it works

The host is our own code: `OpenPak.Server.LoadBalancing/Fusion`, wired unconditionally in
`ListenersHostedService`. It links **no retail assembly** — no Fusion, no Photon client library, no
native lib, no Unity stub — so the released image is complete on its own. What it implements is
specified from black-box measurement in two documents:

- [`protocol/07-fusion-relay-transport.md`](protocol/07-fusion-relay-transport.md) — the relay
  transport carried in Photon **event code 101** messages (Connect/Accepted/NotifyData, acks,
  fragmentation, reliability), per room and per peer.
- [`protocol/08-fusion-shared-simulation.md`](protocol/08-fusion-shared-simulation.md) — the Shared-mode
  simulation: object and struct replication, the PlayerData/RuntimeConfig/SceneInfo/PhysicsInfo
  structs, master election, RPC and internal-message relay, spawn/despawn and leave handling.

Around that:

- **`GameServerHandler`** feeds inbound 101 packets to `IFusionSessionHost` and does not echo them;
  the host runs the relay transport and replicates state. On a Fusion **Join** (event 100) it assigns
  the actor a **stable 8-byte UniqueId** (`FusionProtocol.UniqueIdForActor`) — the client stores it from
  the Join Confirmation and presents it again on its 101 relay Connect, so the actor↔UniqueId↔PlayerRef
  mapping is known before that Connect arrives.
- The room is advanced from the Game listener's `OnTick` (~32 Hz). Each room ticks only on the listener
  LOOP that owns it (`UdpListener.Current`, an AsyncLocal — the loop hops pool threads between receives,
  so affinity is never keyed on a thread id), keeping the single-threaded simulation safe.
- **`Server.Fusion.SceneAuthorityMasterClient`** (default `false`, see `configuration.md`) gives
  SceneInfo/PhysicsInfo `StateAuthority = MasterClient` instead of `None`. Outbound's join sync was
  live-tested with `None`, so it stays off until the other setting is tested in the game.

The real-client harness that drives this host with the game's own Fusion client libraries lives
**outside the repo** (`scratch/fusion/sharedclient` in the OpenPak workspace) and keeps using retail
assemblies as a test instrument only.

**History.** The first host linked the retail `Fusion.Runtime.dll` and ran its internal
`Simulation.Server` headless behind a hand-written `UnityEngine` stub, with a Cecil patch for the
`StateReplicator`'s PluginVersion asymmetry. That patch only ever applied to Fusion **2.1.x**; Outbound
ships **2.0.8** (build 2.0.8.1402), which has no PluginVersion on the wire, so the clean-room host has
nothing to emulate there. The DLL host, the stub and the patcher were removed once the clean-room host
replaced them; the findings below were made against the DLL host and are kept because the same duties
still apply (spec 08 holds the byte-level form).

## The plugin's duties (now ours)

The closed `Fusion.Plugin` (Photon Cloud) does several things a bare Shared-mode server does not. Each
of these was found live with two Outbound clients; the clean-room host implements all of them (wire
detail in spec 08, section numbers in brackets where it has one). Items 1, 6 and 7 were
SDK-internals symptoms of the DLL host (a missing connection, swallowed internal messages); they are
kept for the behaviour they exposed, which the clean-room host must exhibit by construction:

1. **Object updates must be applied for the server's own player.** Hosting the retail simulation, a
   pure server's `LocalPlayer` is None with no connection registered, and the first client spawn threw a
   `NullReferenceException` mid-update. The transport still acked the packet, so the client marked its
   objects confirmed and the follow-ups logged "Object does not exist … marked as confirmed". The
   clean-room host has no such seam; the symptom to watch for is the same (spec 08 section 9).
2. **Scheduling client-created objects onto the other connections.** A client-created object must be
   queued for every other connection when it arrives (spec 08 section 8). Without it a new object only
   reaches players who join *later* (the PlayerJoined sweep).
3. **Object size.** Outbound's master-client world object is 9268 words (37 KB), larger than the SDK's
   default 32 KB heap block, which tripped an assert in the DLL host. The clean-room host stores objects
   as plain word arrays and has no block limit, so `OPENPAK_FUSION_PAGE_KB` is gone.
4. **Master client.** Nothing in the client SDK writes `RuntimeConfig.MasterClient`; the server does,
   electing the lowest actor on join/leave (spec 08 section 7.1; on leave it now names the new master in
   the Photon Leave too). The struct only exists after the sim's first Update, so a solo host becomes
   master when the second actor joins — keep it that way
   (electing from tick 1 + handing SceneInfo/PhysicsInfo to MasterClient made the host stop its join
   sync to the joiner; reverted).
5. **RPC relay.** Client RPCs are simulation messages and nobody else forwards them: targeted → that
   player, otherwise every other connection, as a Fusion Host does. Client-to-client reliable data is
   proxied in the same way. Each RPC is logged with its decoded header (`obj/beh/method`).
6. **Internal simulation messages.** Messages flagged internal (64) — `SetPlayerObject`,
   `SharedModeRequestStateAuthority`, `SharedModeSetAlwaysInterested`, `SetAreaOfInterest` — must be
   applied by the server (spec 08 section 9). The retail simulation swallowed them on a pure server;
   without `SetPlayerObject` the joiner's PlayerData struct never links its avatar, so the host's
   `Runner.GetPlayerObject(joiner)` is null and the host never runs its join sync. They apply in normal
   tick/sequence order (`SetPlayerObjectId`, header `StateAuthority` write, `Add/RemoveAlwaysInterested`).

**Transport prerequisite (not Fusion-specific, but found here):** Fusion clients on Photon Realtime v4
send every relay packet above the MTU as eNet command type **15** (`CT_EG_SEND_FRAGMENT_UNSEQUENCED`,
flags 3; 14 is the reliable-unsequenced twin of 6). Same 20-byte fragment sub-header as type 8, but
its own sequence space and it must be acked with type **16** (`CT_EG_ACK_UNSEQUENCED`) — the client
matches acks by (seq, channel, ack-type-is-unsequenced). Unhandled, the host's ~100-RPC world-sync
burst was silently dropped and mis-acked, the client retransmitted until its ~10 s reliable timeout and
Fusion reported `ShutdownReason` 1014 (`PhotonCloudTimeout`; Outbound shows 1000 + ShutdownReason).
`EnetPeer` handles 14/15 and keys reassembly by (channel, startSeq) so interleaved groups survive.

7. **Clients clobber the structs.** Shared-mode clients write and send their copies of RuntimeConfig/SceneInfo/PhysicsInfo
   and every PlayerData struct back; the retail server copied any struct snapshot over
   its own. The clean-room host parses and discards inbound RuntimeConfig, SceneInfo and PlayerData
   records (spec 08 section 9.3). A copy taken before `SetPlayerObject` was applied wiped the player→avatar link
   (`Object=None`), every client's `GetPlayerObject` goes null, and the host drops out of its own player
   registry — the "works one round in three" symptom. The server records each player's object and
   re-asserts it every tick.

**Game-level notes:** a joining Outbound client keeps NO local character save (FS-traced: "Save and
Quit" writes only settings), so it decides new-vs-returning from the room's `knownPlayers` at join;
`RoomRegistry` keeps a non-empty `knownPlayers` over the host's own empty overwrite (the host writes
the saved list and then "" 1 ms later at host time, and only re-broadcasts when the list changes).
The first `JoinGame` response carries the room's current game properties, as on Photon Cloud. The
host runs the full Initialize* burst for players its save has not serialized and only
`RequestToggleInitializedRpc` for ones it has.

## Status

- Live (2026-09-06, night): fresh AND returning joins work repeatably — both players see each other,
  the joiner's character loads and it spawns beside the host. Needed items 6–7, the type-15 transport
  fix, and the knownPlayers rule above. Still to verify on screen: pickups (state-authority requests)
  — see earlier-outbound/HANDOFF.md, dated sections.
- Rooms whose clients vanish without `OpLeave` keep ticking until `DeadTimeoutSeconds` evicts the
  peers (600 s in the test rig); several idle sims at once is the visible memory growth.
- Clean-room host (2026-10): replaces the DLL host; a leaver's objects are now destroyed and the
  Photon Leave names the new master. Verified against the real-client harness; Outbound two-player on
  the Switch against this host is the next check (`next-session.md`). The sections above describe the
  duties as first found against the DLL host.
