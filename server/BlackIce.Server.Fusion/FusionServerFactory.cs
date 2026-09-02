#if FUSION_LIBS
using Fusion;
using Fusion.Sockets;

namespace BlackIce.Server.Fusion;

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
        // Route Fusion's own logs to the server log at Info; raise to Debug + TraceChannels for bring-up.
        Log.InitializeForConsole(LogLevel.Info);
    }

    public static Simulation CreateServer(int playerCount, INetSocket socket)
    {
        var cfg = new NetworkProjectConfig();
        cfg.Simulation.Topology = Topologies.Shared;
        cfg.Simulation.PlayerCount = playerCount;
        cfg.Simulation.TickRateSelection = TickRate.SharedDefault;
        cfg.Simulation.ObjectDataConsistency = SimulationConfig.DataConsistency.Full;

        var runner = new NetworkRunner();
        runner._config = cfg;
        runner._callbacks = new List<INetworkRunnerCallbacks>();

        var args = new SimulationArgs
        {
            Mode = SimulationModes.Server,
            Address = NetAddress.FromActorId(0),
            Socket = socket,
            Config = cfg,
            Callbacks = new NoopCallbacks(),
        };
        var sim = new Simulation.Server(args);
        sim.Runner = runner;
        runner._simulation = sim;
        // Skip NetworkRunner.OnRuntimeConfigReady (Unity-side spawn queue / behaviour updater) — the
        // headless server never spawns prefabs; it only relays state words between clients.
        runner.OnGameStartedInvoked = true;
        return sim;
    }

    /// <summary>Tell the simulation a peer's uniqueId -> PlayerRef mapping (the plugin's Join-time step);
    /// without it the simulation answers a relay Connect with "Waiting" and the client never joins.</summary>
    public static void RegisterPlayer(Simulation sim, int actorId, byte[] uniqueId, int playerRef)
        => sim.RegisterUniqueIdPlayerMapping(actorId, uniqueId, PlayerRef.FromIndex(playerRef));

    public static void Dispose(Simulation sim)
    {
        try { sim.ShutdownNativeSocket(); sim.Dispose(); } catch { /* best-effort teardown */ }
    }

    private sealed class NoopCallbacks : Simulation.ICallbacks
    {
        public bool IsSharedModeMasterClient => false;
        public bool CanReceivePlayerJoinLeaveCallbacks => true;
        public PlayerRef LocalPlayerRef => PlayerRef.None;
        public void OnTick() { }
        public void OnServerStart() { }
        public void OnClientStart() { }
        public void OnAfterClientSidePredictionReset() { }
        public void OnBeforeClientSidePredictionReset() { }
        public void OnAfterTick() { }
        public void OnBeforeTick() { }
        public void OnAfterAllTicks(bool resimulation, int tickCount) { }
        public void OnBeforeAllTicks(bool resimulation, int tickCount) { }
        public void OnAfterSimulation() { }
        public void OnBeforeSimulation(int forwardTickCount) { }
        public void OnBeforeCopyPreviousState() { }
        public void OnConnectedToServer() { }
        public void OnDisconnectedFromServer(NetDisconnectReason reason) { }
        public OnConnectionRequestReply OnConnectionRequest(NetAddress remoteAddress, byte[] token) => OnConnectionRequestReply.Ok;
        public void OnConnectionFailed(NetAddress remoteAddress, NetConnectFailedReason reason) { }
        public void OnRejoinRequest(string sessionId) { }
        public void OnReliableData(PlayerRef player, ReliableKey key, bool local, ReadOnlySpan<byte> dataArray) { }
        public void OnReliableDataProgress(PlayerRef player, ReliableKey key, float progress) { }
        public void PlayerJoined(PlayerRef player) { }
        public void PlayerLeft(PlayerRef player) { }
        public void OnInternalConnectionAttempt(int attempt, int total, out bool shouldChange, out NetAddress newAddress) { shouldChange = false; newAddress = default; }
        public void ObjectStateAuthorityChanged(NetworkId id, bool gained) { }
        public void ObjectInputAuthorityChanged(NetworkId id, bool gained) { }
        public void ObjectIsSimulatedChanged(NetworkId id, bool simulated) { }
        public void ObjectEnterAOI(PlayerRef player, NetworkId id) { }
        public void ObjectExitAOI(PlayerRef player, NetworkId id) { }
        public RemoteObjectCreateResult OnRemoteCreate(PlayerRef player, ref readonly NetworkObjectHeader header) => RemoteObjectCreateResult.Allowed;
        public bool OnRemoteDestroy(PlayerRef player, NetworkId id) => true;
        public void UpdateRemotePrefabs() { }
        public void OnObjectChanged(in NetworkObjectChange change) { }
        public void OnObjectWrite(in NetworkObjectChange change) { }
        public bool OnStateAuthorityRequested(PlayerRef player, NetworkObjectMeta meta, bool acquire) => true;
        public void OnInput(SimulationInput input) { }
        public void OnInputMissing(SimulationInput input) { }
    }
}
#endif
