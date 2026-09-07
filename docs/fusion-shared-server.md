# Hosting Fusion's Shared-mode server (Outbound)

Outbound uses **Photon Fusion 2** in **Shared** topology. In Shared mode every client is a Fusion
*client*; the authoritative *server* simulation runs inside Photon Cloud's closed **Fusion plugin**.
There is no relay-only mode: a client sends its state to actor 0 (the plugin) and expects a real
Fusion server to hold the world state and replicate it to the other clients. A plain relay that only
forwards packets between peers does **not** work — the earlier solo-host path only survived because a
lone client happily accepts its own keepalives echoed back as if from the server.

Photon does **not** ship the Fusion plugin in the Server SDK (confirmed by Photon staff), and Photon
Server itself is Windows-only and license-gated. So to self-host Outbound we run Fusion's **own**
server simulation headless.

## How it works

Fusion's shipped `Fusion.Runtime.dll` already contains the server: the internal
`Fusion.Simulation.Server` class (Shared topology) is exactly what the cloud plugin hosts. It needs no
Unity runtime to *simulate* — only the `UnityEngine` facade for math, unsafe memory, and JSON. So:

- **`OpenPak.Server.Fusion.UnityStub`** — a hand-written `UnityEngine.dll` covering the exact surface
  Fusion references (Vector/Quaternion/Matrix/Mathf are real; profiler/scene/physics are inert). No
  Unity code; original.
- **`OpenPak.Server.Fusion`** — links Fusion's assemblies and hosts one `Simulation.Server` per room
  (`FusionSharedServer`). Each room's simulation talks to its clients through `RelaySocket`, an
  in-process `INetSocket` whose datagrams are the payloads of Photon **event code 101** messages:
  inbound = what a client raised, outbound = RaiseEvent'd back to the addressed actor.
- **`GameServerHandler`** feeds inbound 101 packets to `IFusionSessionHost` and no longer echoes them;
  the hosted server runs the NetPeerGroup transport (Connect/Accepted/NotifyData) and replicates state.
- On a Fusion **Join** (event 100), the handler assigns the actor a **stable 8-byte UniqueId**
  (`FusionProtocol.UniqueIdForActor`) — the client stores it from the Join Confirmation and presents it
  again on its 101 relay Connect, so the server is told the actor↔UniqueId↔PlayerRef mapping before
  that Connect arrives (`Simulation.RegisterUniqueIdPlayerMapping`), or it answers the Connect "Waiting".
- The room simulation is advanced from the Game listener's `OnTick` (~32 Hz). Each room ticks only on
  the listener LOOP that owns it (`UdpListener.Current`, an AsyncLocal — the loop hops pool threads
  between receives, so affinity is never keyed on a thread id), keeping the single-threaded simulation safe.

## The one required binary patch

The SDK's `StateReplicator` is symmetric: in Shared topology it writes a `PluginVersion` varint before
each object's word list **and** expects to read one. The real cloud plugin is **asymmetric** — it reads
the client's PluginVersion but does not write one back (the SDK client reader never consumes it). Hosting
the stock server against a stock client therefore desyncs (the client parses RuntimeConfig as 0 words +
garbage ids). `tools/fusion-patcher` (Mono.Cecil) applies exactly that asymmetry to a **server copy** of
`Fusion.Runtime.dll`: skip the PluginVersion write when the simulation is a `Server`, read it when it is
a `Server`. With the patch a headless client reaches RuntimeConfig + first snapshot and ticks in lockstep.

## Producing the libs (local only — never committed)

The Fusion assemblies are copyrighted and gitignored (`server/fusion-libs/`, `*.so`). From a licensed
Photon Fusion 2.1.x Unity package:

1. Copy `Fusion.Runtime.dll`, `Fusion.Sockets.dll`, `Fusion.Common.dll`, `Fusion.Log.dll`,
   `Fusion.Realtime.dll`, `PhotonClient.dll`, the managed `NanoSockets.dll`, and the Linux
   `libnanosockets.so` out of the package.
2. Build `Photon.Realtime.dll` from the package's `PhotonRealtime/Code/*.cs` against `PhotonClient.dll`
   and the UnityStub (Fusion.Runtime references it by assembly name; the package ships it as source).
3. Patch the runtime: `dotnet run --project tools/fusion-patcher -- patch <in>/Fusion.Runtime.dll server/fusion-libs/Fusion.Runtime.dll`
   (set `PATCHER_SEARCH` to the UnityStub output dir so Cecil resolves references).
4. Drop all of the above into `server/fusion-libs/`. The build detects them and links the Fusion host;
   without them the server still builds and falls back to the solo-host relay path.

## What the SDK server does NOT do on its own (the plugin's job, now ours)

Fusion.Runtime ships `Simulation.Server` with the Shared-topology code paths, but the closed
`Fusion.Plugin` (Photon Cloud) does several things around it. Each of these was found live with two
Outbound clients and is implemented in `FusionServerFactory` / `FusionSharedServer`:

1. **A `SimulationConnection` for `PlayerRef.None`.** A pure Server's `LocalPlayer` is None and nothing
   registers a connection for it, but `StateReplicator.ReadObjectUpdates` (Shared branch) dereferences
   `GetSimulationConnectionForPlayer(LocalPlayer)` on every object update → `NullReferenceException`
   on the first client spawn. The transport still acks the packet, so the client marks its objects
   confirmed and the follow-ups log "Object does not exist … marked as confirmed".
2. **Scheduling client-created objects onto the other connections.** `NetworkRunner` does this after a
   spawn (`Replicator.OnObjectSpawnedLocal`); our `ICallbacks.RemoteObjectCreated` does the same.
   Without it a new object only reaches players who join *later* (the PlayerJoined sweep).
3. **Heap page size.** Outbound's master-client world object is 9268 words (37 KB); the SDK default
   32 KB block trips `AllocateObject`'s assert. `Heap.PageShift = _128Kb`.
4. **Master client.** `SimulationRuntimeConfig.MasterClient` is written by nobody in the SDK.
   `RoomSim.ElectMaster` writes the lowest actor on join/leave. Note the struct only exists after the
   sim's first Update, so a solo host becomes master when the second actor joins — keep it that way
   (electing from tick 1 + handing SceneInfo/PhysicsInfo to MasterClient made the host stop its join
   sync to the joiner; reverted).
5. **RPC relay.** Client RPCs are `SimulationMessage`s handed to `ICallbacks.OnMessage`; nothing else
   forwards them. `OnMessage` relays like `NetworkRunner.OnMessage` does on a Host: targeted → that
   player, otherwise every other connection. Client-to-client reliable data is already proxied inside
   `Simulation` (`ReliableDataTransferModes` default). Each RPC is logged with its decoded `RpcHeader`
   (`obj/beh/method`).
6. **Internal simulation messages.** `Simulation.OnMessageInternal` is a log-only stub in the SDK: every
   `FLAG_INTERNAL` (64) message — `SetPlayerObject`, `SharedModeRequestStateAuthority`,
   `SharedModeSetAlwaysInterested`, `SetAreaOfInterest` — is swallowed on a pure server, and
   `DeliverMessages` never routes them to `OnMessage`. Without `SetPlayerObject` the joiner's PlayerData
   struct never links its avatar, so the host's `Runner.GetPlayerObject(joiner)` is null and the host
   never runs its join sync. No assembly patch: `OnBeforeTick` (called right before `DeliverMessages`)
   walks each connection's `MessagesIn` and swaps `FLAG_INTERNAL` for a private user flag; the SDK then
   delivers the message through `OnMessage` in normal tick/sequence order and `HandleInternal` applies
   it (`SetPlayerObjectId`, header `StateAuthority` write, `Add/RemoveAlwaysInterested`).

**Transport prerequisite (not Fusion-specific, but found here):** Fusion clients on Photon Realtime v4
send every relay packet above the MTU as eNet command type **15** (`CT_EG_SEND_FRAGMENT_UNSEQUENCED`,
flags 3; 14 is the reliable-unsequenced twin of 6). Same 20-byte fragment sub-header as type 8, but
its own sequence space and it must be acked with type **16** (`CT_EG_ACK_UNSEQUENCED`) — the client
matches acks by (seq, channel, ack-type-is-unsequenced). Unhandled, the host's ~100-RPC world-sync
burst was silently dropped and mis-acked, the client retransmitted until its ~10 s reliable timeout and
Fusion reported `ShutdownReason` 1014 (`PhotonCloudTimeout`; Outbound shows 1000 + ShutdownReason).
`EnetPeer` handles 14/15 and keys reassembly by (channel, startSeq) so interleaved groups survive.

7. **Clients clobber the structs.** `StateReplicator.SendPacket` only skips ClientServer clients, so
   Shared-mode clients run `WriteStructs` and send their copies of RuntimeConfig/SceneInfo/PhysicsInfo
   and every PlayerData struct back; the server's `ReadObjectUpdates` copies any struct snapshot over
   its own. A copy taken before `SetPlayerObject` was applied wipes the player→avatar link
   (`Object=None`), every client's `GetPlayerObject` goes null, and the host drops out of its own player
   registry — the "works one round in three" symptom. The server records each player's object and
   re-asserts it in `OnBeforeTick`; the dump prints `P=/O=/A=` per PlayerData struct.

**Game-level notes:** a joining Outbound client keeps NO local character save (FS-traced: "Save and
Quit" writes only settings), so it decides new-vs-returning from the room's `knownPlayers` at join;
`RoomRegistry` keeps a non-empty `knownPlayers` over the host's own empty overwrite (the host writes
the saved list and then "" 1 ms later at host time, and only re-broadcasts when the list changes).
The first `JoinGame` response carries the room's current game properties, as on Photon Cloud. The
host runs the full Initialize* burst for players its save has not serialized and only
`RequestToggleInitializedRpc` for ones it has.

Diagnostics on stdout: `[FusionHost] room …` per second, `[FusionHost] remote object …` per client
spawn, `[FusionHost] rpc …` per relayed RPC, `[FusionHost] objects: …` every 5 s (RuntimeConfig
master/host, per-object authority/ChangedTick, per-connection Status/TickSent/TickAcknowledged).

## Status

- Live (2026-09-06, night): fresh AND returning joins work repeatably — both players see each other,
  the joiner's character loads and it spawns beside the host. Needed items 6–7, the type-15 transport
  fix, and the knownPlayers rule above. Still to verify on screen: pickups (state-authority requests)
  — see earlier-outbound/HANDOFF.md, dated sections.
- Rooms whose clients vanish without `OpLeave` keep ticking until `DeadTimeoutSeconds` evicts the
  peers (600 s in the test rig); several idle sims at once is the visible memory growth.
