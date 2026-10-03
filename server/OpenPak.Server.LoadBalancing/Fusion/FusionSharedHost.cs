using System.Diagnostics;
using OpenPak.Photon;
using OpenPak.Photon.Transport;
using OpenPak.Server.Core;

namespace OpenPak.Server.LoadBalancing.Fusion;

/// <summary>
/// <see cref="IFusionSessionHost"/> for Fusion 2 Shared-mode rooms: one <see cref="FusionSharedRoom"/>
/// per room, in place of the cloud plugin (docs/protocol/08-fusion-shared-simulation.md). ClientServer
/// rooms get none: their peers never connect to actor 0 and their relay traffic is carried peer to peer
/// by <see cref="GameServerHandler"/>.
///
/// Threading: joins, leaves and relay packets may arrive from any Game listener loop; they are queued
/// and applied by <see cref="Tick"/> on the loop that created the room (<see cref="UdpListener.Current"/>
/// at its first join), so each room is single-threaded. Building a room is cheap, so there is no
/// off-thread construction.
/// </summary>
public sealed class FusionSharedHost : IFusionSessionHost
{
    /// <summary>The largest Fusion datagram that may go out as an unreliable Photon event: the client
    /// reads one UDP datagram into a 1200-byte buffer, and event 101 costs 39 bytes around the datagram
    /// (12 Photon header, 16 unreliable command, 11 event framing). Larger ones are sent reliably, which
    /// the Photon layer fragments (docs/protocol/08 §19).</summary>
    public const int MaxUnreliableDatagramBytes = EnetPeer.Mtu - PhotonHeader.Size - NCommand.UnreliableHeaderSize - 11;
    private const string Cat = "FusionHost";

    private readonly Action<string, int, byte[], bool> _send;
    private readonly FusionRoomOptions _options;
    private readonly Func<double> _clock;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _rooms = new(StringComparer.Ordinal);

    private sealed class Entry(FusionSharedRoom room, UdpListener? owner)
    {
        public readonly FusionSharedRoom Room = room;
        public readonly UdpListener? Owner = owner;
        /// <summary>Work for the room's own loop, in arrival order. Guarded by the host's gate.</summary>
        public List<Action<FusionSharedRoom>> Pending = new();
    }

    /// <param name="send">Delivers one relay datagram as event 101 (sender actor 0) to an actor of a room;
    /// the flag asks for reliable delivery (fragment pieces, and anything over <see cref="MaxUnreliableDatagramBytes"/>).</param>
    /// <param name="clock">Seconds on a monotonic clock; a stopwatch by default (tests pass their own).</param>
    public FusionSharedHost(Action<string, int, byte[], bool> send, FusionRoomOptions? options = null, Func<double>? clock = null)
    {
        _send = send;
        _options = options ?? new FusionRoomOptions();
        if (clock is null)
        {
            var stopwatch = Stopwatch.StartNew();
            clock = () => stopwatch.Elapsed.TotalSeconds;
        }
        _clock = clock;
    }

    public void PlayerJoined(string room, int actor, byte[] uniqueId, bool clientServer = false)
    {
        if (clientServer)
        {
            Log.Debug(Cat, $"\"{room}\" actor {actor} is ClientServer: relayed peer to peer, no hosted simulation");
            return;
        }
        var id = uniqueId.ToArray();
        lock (_gate)
        {
            if (!_rooms.TryGetValue(room, out var entry))
            {
                var created = new FusionSharedRoom(room, (target, datagram, reliable) =>
                    _send(room, target, datagram, reliable || datagram.Length > MaxUnreliableDatagramBytes), _clock(), _options);
                _rooms[room] = entry = new Entry(created, UdpListener.Current);
                Log.Debug(Cat, $"\"{room}\" Shared simulation created");
            }
            entry.Pending.Add(r => r.Register(actor, id));
        }
    }

    public void PlayerLeft(string room, int actor)
    {
        lock (_gate)
            if (_rooms.TryGetValue(room, out var entry)) entry.Pending.Add(r => r.Unregister(actor));
    }

    public bool FeedRelay(string room, int actor, byte[] payload)
    {
        var arrival = _clock();
        lock (_gate)
        {
            if (!_rooms.TryGetValue(room, out var entry)) return false;
            entry.Pending.Add(r => r.Receive(actor, payload, arrival));
            return true;
        }
    }

    public void Tick()
    {
        var current = UdpListener.Current;
        List<Entry> mine;
        lock (_gate) mine = _rooms.Values.Where(e => ReferenceEquals(e.Owner, current)).ToList();

        foreach (var entry in mine)
        {
            List<Action<FusionSharedRoom>> work;
            lock (_gate) (work, entry.Pending) = (entry.Pending, new());
            try
            {
                foreach (var action in work) action(entry.Room);
                entry.Room.Tick(_clock());
            }
            catch (Exception ex)
            {
                Log.Exception(Cat, $"\"{entry.Room.Name}\" tick failed", ex);
            }

            lock (_gate)
                if (entry.Room.IsEmpty && entry.Pending.Count == 0 && _rooms.Remove(entry.Room.Name))
                    Log.Debug(Cat, $"\"{entry.Room.Name}\" Shared simulation closed: no players");
        }
    }
}
