#if FUSION_LIBS
using System.Collections.Concurrent;
using System.Diagnostics;
using OpenPak.Server.Core;
using OpenPak.Server.LoadBalancing;
using Fusion;
using Fusion.Sockets;

namespace OpenPak.Server.Fusion;

/// <summary>
/// <see cref="IFusionSessionHost"/> backed by Fusion's own <c>Simulation.Server</c> (Shared topology),
/// one per room. Each room's simulation talks to its clients through an in-process
/// <see cref="RelaySocket"/> whose datagrams are Photon event-code-101 payloads: inbound packets are
/// what clients raised, outbound packets are RaiseEvent'd back to the addressed actor.
///
/// This is the server role Photon Cloud's Fusion plugin plays. All members run on the single Game
/// listener thread (Fusion simulation is not thread-safe), driven by <see cref="Tick"/>.
/// </summary>
public sealed class FusionSharedServer : IFusionSessionHost
{
    private readonly ConcurrentDictionary<string, RoomSim> _rooms = new(StringComparer.Ordinal);
    private readonly Action<string, int, byte[]> _send101;
    private readonly int _playerCount;

    /// <param name="send101">Delivers one outbound relay packet as Photon event 101 (server sender,
    /// actor 0) to a specific actor in a room.</param>
    /// <param name="playerCount">Max players per room simulation.</param>
    public FusionSharedServer(Action<string, int, byte[]> send101, int playerCount = 8)
    {
        _send101 = send101;
        _playerCount = Math.Clamp(playerCount, 2, 200);
        FusionServerFactory.EnsureLogInitialized();
    }

    public void PlayerJoined(string room, int actor, byte[] uniqueId)
        => _rooms.GetOrAdd(room, r => new RoomSim(r, _playerCount, _send101)).AddPlayer(actor, uniqueId);

    public void PlayerLeft(string room, int actor)
    {
        if (_rooms.TryGetValue(room, out var sim) && sim.RemovePlayer(actor) && _rooms.TryRemove(room, out _))
            sim.Dispose();
    }

    public bool FeedRelay(string room, int actor, byte[] payload)
    {
        if (!_rooms.TryGetValue(room, out var sim)) return false;
        sim.Feed(actor, payload);
        return true;
    }

    public void Tick()
    {
        foreach (var sim in _rooms.Values) sim.Tick();
    }

    private sealed class RoomSim : IDisposable
    {
        private readonly string _room;
        private readonly Action<string, int, byte[]> _send101;
        private readonly RelaySocket _socket;
        private readonly Simulation _sim;
        private readonly HashSet<int> _actors = new();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private double _last;
        private double _lastLog;
        private int _dumpEvery;
        private UdpListener? _owner;   // the listener loop that owns this sim; ticks run only from it

        public RoomSim(string room, int playerCount, Action<string, int, byte[]> send101)
        {
            _room = room;
            _send101 = send101;
            _socket = new RelaySocket((actor, data) => { _emitted++; _send101(_room, actor, data); });
            _sim = FusionServerFactory.CreateServer(playerCount, _socket);
        }

        public void AddPlayer(int actor, byte[] uniqueId)
        {
            _owner = UdpListener.Current;
            if (_actors.Add(actor))
                FusionServerFactory.RegisterPlayer(_sim, actor, uniqueId, actor);
            ElectMaster();
        }

        public bool RemovePlayer(int actor)
        {
            if (_actors.Remove(actor))
            {
                FusionServerFactory.DisconnectPlayer(_sim, actor);
                Console.WriteLine($"[FusionHost] room \"{_room}\" player {actor} left -> sim disconnect");
            }
            ElectMaster();
            return _actors.Count == 0;
        }

        // Master client (RuntimeConfig.MasterClient) = lowest actor. NB: the RuntimeConfig struct only
        // exists after the sim's first Update, so for a solo host this takes effect when the SECOND
        // actor joins — that is the behaviour of the first build in which the host saw the joiner
        // (room DCTKKE). Making the host master from tick 1 (plus SceneInfo/PhysicsInfo authority to
        // MasterClient) made the host stop sending its 134-RPC join sync (room KMK3EE). Do not "fix"
        // this without re-testing that.
        private void ElectMaster()
        {
            if (_actors.Count > 0) FusionServerFactory.SetMasterClient(_sim, _actors.Min());
        }

        private long _fed, _emitted;
        public void Feed(int actor, byte[] payload) { _fed++; _socket.Enqueue(actor, payload); }

        public void Tick()
        {
            // Fusion's simulation is single-threaded; only its owning listener LOOP may drive it. The loop
            // hops pool threads between receives (async), so the check is on the loop, not the thread.
            if (_owner is null || !ReferenceEquals(_owner, UdpListener.Current)) return;
            double now = _clock.Elapsed.TotalSeconds;
            double dt = now - _last;
            _last = now;
            if (dt > 0) _sim.Update(dt);
            if (now - _lastLog >= 1.0)
            {
                _lastLog = now;
                Console.WriteLine($"[FusionHost] room \"{_room}\" tick={_sim.Tick} players={_actors.Count} fed={_fed} emitted={_emitted}");
                if (_actors.Count >= 2 && ++_dumpEvery % 5 == 0)   // diagnostics: per-object delivery state every 5 s
                    Console.WriteLine("[FusionHost] objects: " + FusionServerFactory.Dump(_sim));
            }
        }

        public void Dispose() => FusionServerFactory.Dispose(_sim);
    }
}
#endif
