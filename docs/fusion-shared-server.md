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

- **`BlackIce.Server.Fusion.UnityStub`** — a hand-written `UnityEngine.dll` covering the exact surface
  Fusion references (Vector/Quaternion/Matrix/Mathf are real; profiler/scene/physics are inert). No
  Unity code; original.
- **`BlackIce.Server.Fusion`** — links Fusion's assemblies and hosts one `Simulation.Server` per room
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
  the listener thread that owns it (thread affinity), keeping the single-threaded simulation safe.

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

## Status

- Headless single client and **two clients** join one hosted server, reach RuntimeConfig, and tick in
  lockstep — proven in the bring-up spike. The two-peer relay path (the whole point) is exercised.
- **Remaining gate:** a live two-instance Outbound run (host + joiner) through this server, confirming a
  session that holds and plays past the old ~15 s relay-death. See earlier-outbound/HANDOFF.md.
