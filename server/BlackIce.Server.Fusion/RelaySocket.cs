#if FUSION_LIBS
using System.Runtime.InteropServices;
using Fusion.Sockets;

namespace BlackIce.Server.Fusion;

/// <summary>
/// In-process <see cref="INetSocket"/> that stands in for Fusion's real relay transport. A Fusion
/// <c>NetSocketRelay</c> addresses peers by actor id (<see cref="NetAddress.FromActorId"/>); here each
/// datagram is the raw payload of a Photon event-code-101 message. Inbound packets (what a client
/// raised) are pushed via <see cref="Enqueue"/>; outbound packets (what the simulation sends) are
/// handed to the <c>emit</c> callback keyed by target actor. Single-threaded, matching the simulation.
/// </summary>
internal sealed unsafe class RelaySocket : INetSocket
{
    private readonly Queue<(int from, byte[] data)> _inbound = new();
    private readonly Action<int, byte[]> _emit;   // (targetActor, payload)

    /// <summary>Unused hook kept for symmetry with the room sim; set to the actor being serviced.</summary>
    public int CurrentTarget { get; set; } = -1;

    public RelaySocket(Action<int, byte[]> emit) => _emit = emit;

    public void Enqueue(int fromActor, byte[] payload) => _inbound.Enqueue((fromActor, payload));

    public bool SupportsMultiThreading => false;
    public void Initialize(NetConfig config) { }
    public NetSocket Create(NetConfig config) => new NetSocket { Handle = 1 };
    public NetAddress Bind(NetSocket socket, NetConfig config) => NetAddress.FromActorId(0);
    public bool CanFragment(NetAddress address) => true;
    public bool Poll(NetSocket socket, long timeout) => _inbound.Count > 0;

    public int Receive(NetSocket socket, NetAddress* address, byte* buffer, int bufferLength)
    {
        if (_inbound.Count == 0) return 0;
        var (from, data) = _inbound.Dequeue();
        int n = Math.Min(bufferLength, data.Length);
        Marshal.Copy(data, 0, (IntPtr)buffer, n);
        *address = NetAddress.FromActorId(from);
        return n;
    }

    public int Send(NetSocket socket, NetAddress* address, byte* buffer, int bufferLength, bool reliable = false)
    {
        if (!address->IsRelayAddr) return -1;
        var data = new byte[bufferLength];
        Marshal.Copy((IntPtr)buffer, data, 0, bufferLength);
        _emit(address->ActorId, data);
        return bufferLength;
    }

    public void Destroy(NetSocket socket) { }
    public void DeleteEncryptionKey(NetAddress address) { }
    public void SetupEncryption(byte[] key, byte[] encryptedKey) { }
}
#endif
