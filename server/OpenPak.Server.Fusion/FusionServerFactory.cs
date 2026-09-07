#if FUSION_LIBS
using Fusion;
using Fusion.Sockets;

namespace OpenPak.Server.Fusion;

/// <summary>
/// Constructs and drives a headless Fusion <c>Simulation.Server</c> in Shared topology. Fusion's
/// public entry point (<c>NetworkRunner.StartGame</c>) assumes a live Unity player loop and prefab
/// database, so we build the simulation directly through its (publicized) internals — a
/// <c>NetworkRunner</c> exists only to satisfy the fields the simulation reads. Isolated here so all
/// use of Fusion internals sits in one file.
/// </summary>
internal static class FusionServerFactory
{
    private static bool _logInit;

    public static void EnsureLogInitialized()
    {
        if (_logInit) return;
        _logInit = true;
        // Wires Fusion's InternalLogStreams (LogWarn/LogError/LogException are otherwise null and every
        // swallowed exception vanishes). SimulationMessage trace channel prints why a queued RPC is
        // not being delivered ("Not handling RPC: tick/sequence ...").
        try { Log.InitializeForConsole(new LogSettings(LogLevel.Info, TraceChannels.SimulationMessage)); }
        catch (Exception e) { Console.WriteLine($"[FusionHost] Fusion log init failed: {e}"); }
    }

    public static Simulation CreateServer(int playerCount, INetSocket socket)
    {
        var cfg = new NetworkProjectConfig();
        cfg.Simulation.Topology = Topologies.Shared;
        cfg.Simulation.PlayerCount = playerCount;
        cfg.Simulation.TickRateSelection = TickRate.Default;
        cfg.Simulation.ObjectDataConsistency = SimulationConfig.DataConsistency.Full;
        // A title's player object must fit one heap block: Outbound's is 9268 words (37 KB), and the
        // SDK default 32 KB makes AllocateObject assert ("37072 <= 32768"). 128 KB by default
        // (PageCount default = 32 MB per allocator, two per room); OPENPAK_FUSION_PAGE_KB=32|64|128|256
        // raises it for a title with a bigger object. ponytail: env knob, not config schema —
        // move it into openpak.server.json if a second Fusion title ever needs a different value.
        cfg.Heap.PageShift = Environment.GetEnvironmentVariable("OPENPAK_FUSION_PAGE_KB") switch
        {
            "32" => PageSizes._32Kb,
            "64" => PageSizes._64Kb,
            "256" => PageSizes._256Kb,
            _ => PageSizes._128Kb,
        };

        var runner = new NetworkRunner();
        runner._config = cfg;
        runner._callbacks = new List<INetworkRunnerCallbacks>();

        var callbacks = new NoopCallbacks();
        var args = new SimulationArgs
        {
            Mode = SimulationModes.Server,
            Address = NetAddress.FromActorId(0),
            Socket = socket,
            Config = cfg,
            Callbacks = callbacks,
        };
        var sim = new Simulation.Server(args);
        callbacks.Sim = sim;
        sim.Runner = runner;
        runner._simulation = sim;
        // A pure Server's LocalPlayer is PlayerRef.None and the SDK never registers a connection for
        // it, yet StateReplicator.ReadObjectUpdates (Shared branch) dereferences that connection on
        // every object update -> NullReferenceException on the first client spawn, object never
        // allocated, nothing forwarded. Photon's closed Fusion.Plugin registers one; so do we.
        sim._playersConnections[PlayerRef.None] = new SimulationConnection(sim);
        // Skip NetworkRunner.OnRuntimeConfigReady (Unity-side spawn queue / behaviour updater) — the
        // headless server never spawns prefabs; it only relays state words between clients.
        runner.OnGameStartedInvoked = true;
        return sim;
    }

    /// <summary>Tell the simulation a peer's uniqueId -> PlayerRef mapping (the plugin's Join-time step);
    /// without it the simulation answers a relay Connect with "Waiting" and the client never joins.</summary>
    public static void RegisterPlayer(Simulation sim, int actorId, byte[] uniqueId, int playerRef)
        => sim.RegisterUniqueIdPlayerMapping(actorId, uniqueId, PlayerRef.FromIndex(playerRef));

    /// <summary>The plugin's Leave-time step: close the player's relay connection so the simulation
    /// deletes its PlayerData, frees its DestroyWhenStateAuthorityLeaves objects and tells the other
    /// clients PlayerLeft. Without it a leaver stays in the world as a ghost and the host never
    /// serializes it, so its return is treated as a first arrival.</summary>
    public static void DisconnectPlayer(Simulation sim, int playerRef)
        => ((Simulation.Server)sim).Disconnect(PlayerRef.FromIndex(playerRef), null);

    /// <summary>
    /// The cloud plugin's Shared-mode bookkeeping that the SDK server never does on its own: publish the
    /// master client in the replicated RuntimeConfig, and give SceneInfo/PhysicsInfo to the master client.
    /// The server creates those structs with StateAuthority None; with LocalPlayer also None,
    /// <c>ReadObjectUpdates</c> then treats the server as their authority and SKIPS every SceneInfo the
    /// host sends, so a joiner never learns which scene to load and never spawns its avatar.
    /// Idempotent; called every tick because the structs only exist after the first Update.
    /// </summary>
    public static unsafe void SetMasterClient(Simulation sim, int actor)
    {
        if (!sim.TryGetStructData<SimulationRuntimeConfig>(NetworkId.RuntimeConfig, out var rc)) return;
        var p = PlayerRef.FromIndex(actor);
        if (rc->MasterClient == p) return;
        rc->MasterClient = p;
        Console.WriteLine($"[FusionHost] master client -> {p}");
    }

    // Tried and REVERTED (room KMK3EE): also handing SceneInfo/PhysicsInfo StateAuthority to
    // PlayerRef.MasterClient and electing the master from tick 1. With that, the host never sent its
    // join sync to the joiner. Keep the server's SceneInfo at StateAuthority None (its updates from
    // clients are skipped — the game drives scenes via Photon room properties instead).

    /// <summary>Per-object, per-connection send state — bring-up diagnostics for state forwarding.</summary>
    public static unsafe string Dump(Simulation sim)
    {
        var sb = new System.Text.StringBuilder();
        if (sim.TryGetStructData<SimulationRuntimeConfig>(NetworkId.RuntimeConfig, out var rc))
            sb.Append($"rc.master={rc->MasterClient} rc.host={rc->HostPlayer}; ");
        foreach (var (id, m) in sim._metaLookup)
        {
            if (m.IsStruct && id != NetworkId.SceneInfo && m.Type != NetworkObjectTypeId.PlayerData) continue;
            // nz = non-zero words the server holds; marked = words with a change tick;
            // pending(pN) = words changed after that peer's last ack. No Shadow read (its getter allocates).
            int nz = 0, marked = 0;
            var raw = m.Raw; int* ch = m.Changes;
            for (int i = 0; i < m.WordCount; i++) { if (raw[i] != 0) nz++; if (ch[i] > 0) marked++; }
            sb.Append($"{id}[{m.Type} w={m.WordCount} nz={nz} marked={marked} SA={m.StateAuthority} chg={m.ChangedTick} scan={m.ScannedTick}");
            if (m.Type == NetworkObjectTypeId.PlayerData && sim.TryGetStructData<Simulation.PlayerSimulationData>(id, out var pd))
                sb.Append($" P={pd->Player} O={pd->Object} A={pd->Actor}");   // the player->avatar link every client's GetPlayerObject reads
            foreach (var c in sim._connections.Values)
            {
                if (!c.TryGetObjectData(id, out var d)) { sb.Append($" p{c.Player.PlayerId}:none"); continue; }
                int pending = 0; for (int i = 0; i < m.WordCount; i++) if (ch[i] > d.TickAcknowledged) pending++;
                sb.Append($" p{c.Player.PlayerId}:{d.Status}/sent={d.TickSent}/ack={d.TickAcknowledged}/pending={pending}");
            }
            sb.Append("] ");
        }
        return sb.ToString();
    }

    public static void Dispose(Simulation sim)
    {
        try { sim.ShutdownNativeSocket(); sim.Dispose(); } catch { /* best-effort teardown */ }
    }

    private sealed unsafe class NoopCallbacks : Simulation.ICallbacks
    {
        public Simulation? Sim;
        public bool IsSharedModeMasterClient => false;
        public bool CanReceivePlayerJoinLeaveCallbacks => true;
        public PlayerRef LocalPlayerRef => PlayerRef.None;
        public void OnTick() { }
        public void OnServerStart() { }
        public void OnClientStart() { }
        // Internal simulation messages (FLAG_INTERNAL): the SDK's Simulation.OnMessageInternal is a
        // log-only stub — the cloud plugin implements them. DeliverMessages routes flag-64 messages there
        // and never to OnMessage, so OnBeforeTick (called right before DeliverMessages) swaps the flag for
        // a private marker; the SDK then delivers the message through OnMessage in normal tick/sequence
        // order and we apply it here. No assembly patching.
        private const int FlagOurInternal = SimulationMessage.FLAG_USER_FLAGS_START;

        // Shared-topology CLIENTS also run WriteStructs (only ClientServer clients skip SendPacket), so
        // each client periodically sends its own copy of every PlayerData struct back and the server's
        // ReadObjectUpdates copies that snapshot over its own. A copy taken before SetPlayerObject was
        // applied carries Object=None and clobbers the link; last writer wins (that is why some rounds
        // had the host in its own list and most did not). The server is the authority: re-assert.
        private readonly Dictionary<PlayerRef, NetworkId> _playerObjects = new();
        private int _reasserts;

        private int _qlogTick;
        public void OnBeforeTick()
        {
            if (Sim is null) return;
            foreach (var (player, obj) in _playerObjects)
            {
                var pd = Sim.GetPlayerSimulationData(player, create: false);
                if (pd == null || pd->Object == obj) continue;
                pd->Object = obj;
                if (++_reasserts <= 20 || _reasserts % 500 == 0)
                    Console.WriteLine($"[FusionHost] PlayerData {player} Object clobbered -> re-asserted {obj} (#{_reasserts}) tick={(int)Sim.Tick}");
            }
            bool logQ = (int)Sim.Tick - _qlogTick >= 64;   // diagnostics: stuck inbound queues, ~1/s
            if (logQ) _qlogTick = (int)Sim.Tick;
            foreach (var c in Sim._connections.Values)
            {
                var h = c.MessagesIn.Head;
                if (logQ && h != null)
                    Console.WriteLine($"[FusionHost] msgq {c.Player} count={c.MessagesIn.Count} expectSeq={c.MessagesInSequence + 1} head: seq={h->Sequence} tick={h->Message->Tick} flags={h->Message->Flags} src={h->Message->Source} tgt={h->Message->Target} simTick={(int)Sim.Tick}");
                for (var e = h; e != null; e = e->Next)
                    if (e->Message->GetFlag(SimulationMessage.FLAG_INTERNAL))
                        e->Message->Flags = (e->Message->Flags & ~SimulationMessage.FLAG_INTERNAL) | FlagOurInternal;
            }
        }

        private void HandleInternal(SimulationMessage* m)
        {
            var type = Simulation.GetMessageInternalType(m);
            switch (type)
            {
                case SimulationMessageInternalTypes.SetPlayerObject:
                {
                    // Client: "my player object is X". Lands in the replicated PlayerData struct, which is
                    // what every other client's Runner.GetPlayerObject(player) reads.
                    var d = Simulation.GetMessageInternalData<SimulationMessageInternal_SetPlayerObject>(m);
                    Sim!.SetPlayerObjectId(m->Source, d.Object);
                    _playerObjects[m->Source] = d.Object;   // re-asserted every tick, see OnBeforeTick
                    // Read-back diagnostic: the dump showed O=None for the host after this "applied".
                    var pd = Sim.GetPlayerSimulationData(m->Source, create: false);
                    Console.WriteLine($"[FusionHost] internal SetPlayerObject {m->Source} -> {d.Object} valid={Sim.PlayerValid(m->Source)} readback={(pd == null ? "null" : $"P={pd->Player} O={pd->Object} A={pd->Actor}")} lookup={string.Join(",", Sim._playerDataLookup.Select(kv => $"{kv.Key}:{kv.Value}"))}");
                    break;
                }
                case SimulationMessageInternalTypes.SharedModeRequestStateAuthority:
                {
                    var d = Simulation.GetMessageInternalData<SimulationMessageInternal_SharedModeRequestStateAuthority>(m);
                    if (!Sim!.TryGetMeta(d.Object, out var meta)) { Console.WriteLine($"[FusionHost] internal RequestStateAuthority {m->Source} {d.Object}: no such object"); break; }
                    var cur = meta.StateAuthority;
                    PlayerRef next = cur;
                    if (d.Acquire != 0)
                    {
                        // ponytail: any requester wins if the object allows override or has no live authority.
                        if (cur != m->Source && ((meta.Flags & NetworkObjectHeaderFlags.AllowStateAuthorityOverride) != 0 || cur.IsNone || !Sim.PlayerValid(cur)))
                            next = m->Source;
                    }
                    else if (cur == m->Source) next = PlayerRef.None;
                    if (next != cur) meta.StateAuthority = next;   // header word; the server's scan re-sends it to every client
                    Console.WriteLine($"[FusionHost] internal RequestStateAuthority {m->Source} obj={d.Object} acquire={d.Acquire} {cur} -> {next}");
                    break;
                }
                case SimulationMessageInternalTypes.SharedModeSetAlwaysInterested:
                {
                    var d = Simulation.GetMessageInternalData<SimulationMessageInternal_SharedModeSetAlwaysInterested>(m);
                    var conn = Sim!.GetSimulationConnectionForPlayer(PlayerRef.FromIndex(d.Player));
                    if (conn != null && Sim.TryGetMeta(d.Object, out var meta))
                    {
                        if (d.Interested != 0) conn.AddAlwaysInterested(meta); else conn.RemoveAlwaysInterested(meta);
                    }
                    Console.WriteLine($"[FusionHost] internal SetAlwaysInterested from {m->Source} player={d.Player} obj={d.Object} interested={d.Interested}");
                    break;
                }
                default:
                    Console.WriteLine($"[FusionHost] internal {type} from {m->Source} ignored");   // SetAreaOfInterest: AOI is off
                    break;
            }
        }

        public SimulationMessageResult OnMessage(SimulationMessage* m)
        {
            // The cloud plugin's relay role for RPCs: a Shared-mode server receives every client RPC and
            // must forward it — targeted ones to their player, the rest to every other connection. The
            // receiving client applies its own authority/target masks before invoking, so over-forwarding
            // is filtered there. Dropping these (the old "Ignored") left the joiner waiting on the host's
            // join handshake and it never spawned its avatar.
            if (Sim is null || m->GetFlag(SimulationMessage.FLAG_DUMMY)) return SimulationMessageResult.Handled;
            if (m->GetFlag(FlagOurInternal)) { HandleInternal(m); return SimulationMessageResult.Handled; }
            int sent = 0;
            bool targeted = m->IsTargeted();
            var tgt = targeted ? m->Target.ToString() : "all";   // read BEFORE forward (Fusion clears Target)
            // RpcHeader is a plain read of the message payload (no simulation-state access, unlike Shadow).
            var raw = SimulationMessage.GetRawData(m);
            RpcHeader h = default;
            if (raw.Length >= RpcHeader.SIZE) fixed (byte* p = raw) h = RpcHeader.Read(p, out _);
            if (targeted)
            {
                if (m->Target.IsRealPlayer && m->Target != m->Source && Sim.ForwardMessage(m, m->Target, required: true)) sent++;
            }
            else
            {
                foreach (var c in Sim._connections.Values)
                    if (c.Player.IsRealPlayer && c.Player != m->Source && Sim.ForwardMessage(m, c.Player, required: false)) sent++;
            }
            Console.WriteLine($"[FusionHost] rpc from {m->Source} target={tgt} flags={m->Flags} tick={m->Tick}/{(int)Sim.Tick} obj={h.Object} beh={h.Behaviour} method={h.Method} size={raw.Length} fwd={sent}");
            return SimulationMessageResult.Handled;
        }
        public void OnAfterClientSidePredictionReset() { }
        public void OnBeforeClientSidePredictionReset() { }
        public void OnAfterTick() { }
        public void OnAfterAllTicks(bool resimulation, int tickCount) { }
        public void OnBeforeAllTicks(bool resimulation, int tickCount) { }
        public void OnAfterSimulation() { }
        public void OnBeforeSimulation(int forwardTickCount) { }
        public void OnBeforeCopyPreviousState() { }
        public void OnConnectedToServer() { }
        public void OnDisconnectedFromServer(NetDisconnectReason reason) { }
        public OnConnectionRequestReply OnConnectionRequest(NetAddress remoteAddress, byte[] token) => OnConnectionRequestReply.Ok;
        public void OnConnectionFailed(NetAddress remoteAddress, NetConnectFailedReason reason) { }
        public void OnReliableData(PlayerRef player, ReliableId id, bool local, byte[] dataArray) { }
        public void PlayerJoined(PlayerRef player) { }
        public void PlayerLeft(PlayerRef player) { _playerObjects.Remove(player); }
        public void OnInternalConnectionAttempt(int attempt, int total, out bool shouldChange, out NetAddress newAddress) { shouldChange = false; newAddress = default; }
        public void ObjectStateAuthorityChanged(NetworkId id, bool gained) { }
        public void ObjectInputAuthorityChanged(NetworkId id, bool gained) { }
        public void ObjectIsSimulatedChanged(NetworkId id, bool simulated) { }
        public void ObjectEnterAOI(PlayerRef player, NetworkId id) { }
        public void ObjectExitAOI(PlayerRef player, NetworkId id) { }
        public void ObjectChanged(PlayerRef player, NetworkObjectMeta obj, Simulation.ObjectChangeType changeType) { }
        public void RemoteObjectCreated(NetworkObjectMeta obj)
        {
            // NetworkRunner does this after spawning a remote-created object; on the server it puts the
            // object on every connection's send schedule so the OTHER clients receive it.
            if ((obj.Flags & NetworkObjectHeaderFlags.Struct) == 0 && !obj.Id.IsReserved)
                Sim?.Replicator.OnObjectSpawnedLocal(obj.Id);
            Console.WriteLine($"[FusionHost] remote object {obj.Id} type={obj.Type} words={obj.WordCount} SA={obj.StateAuthority} IA={obj.InputAuthority} flags={obj.Flags} conns={Sim?._connections.Count}");
        }
        // A client with state authority despawned an object (e.g. the host despawning a leaver's adopted
        // avatar). Returning true means "handled" and the simulation keeps the object forever; false lets
        // Simulation.Destroy free it and forward the destroy to every other client.
        public bool RemoteObjectDestroyed(NetworkId id) { Console.WriteLine($"[FusionHost] remote destroy {id}"); return false; }
        public void UpdateRemotePrefabs() { }
        public void OnInput(SimulationInput input) { }
        public void OnInputMissing(SimulationInput input) { }
    }
}
#endif
