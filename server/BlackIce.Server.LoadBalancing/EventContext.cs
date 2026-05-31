using BlackIce.Photon;

namespace BlackIce.Server.LoadBalancing;

/// <summary>
/// The decoded inbound event handed to interceptors: which room it is in, which actor sent it, and
/// the event itself. Phase 2b adds richer classification (RPC method, decoded payloads); 2a keeps it
/// to the raw event so the pass-through relay needs no payload understanding.
/// </summary>
public sealed class EventContext
{
    public string RoomName { get; }
    public int SenderActor { get; }
    public EventData Event { get; }
    public bool Unreliable { get; }

    public EventContext(string roomName, int senderActor, EventData ev, bool unreliable = false)
    {
        RoomName = roomName; SenderActor = senderActor; Event = ev; Unreliable = unreliable;
    }
}
