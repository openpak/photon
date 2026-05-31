using BlackIce.Photon;

namespace BlackIce.Server.LoadBalancing;

/// <summary>What the relay should do with an inbound event after the interceptor chain runs.</summary>
public enum RelayAction { Forward, Drop, Rewrite, Originate }

/// <summary>
/// An interceptor's decision. <see cref="Forward"/> relays the event unchanged; <see cref="Drop"/>
/// swallows it; <see cref="Rewrite"/> relays a replacement; <see cref="Originate"/> relays the event
/// plus extra server-authored events (used by authority corrections and playerbots in later phases).
/// </summary>
public sealed class RelayVerdict
{
    public RelayAction Action { get; }
    /// <summary>The event to relay (null for <see cref="RelayAction.Drop"/>).</summary>
    public EventData? Event { get; }
    /// <summary>Extra events to relay after <see cref="Event"/>. Empty unless Originate.</summary>
    public IReadOnlyList<EventData> Originated { get; }

    private RelayVerdict(RelayAction action, EventData? ev, IReadOnlyList<EventData> originated)
    {
        Action = action; Event = ev; Originated = originated;
    }

    private static readonly EventData[] None = System.Array.Empty<EventData>();

    public static RelayVerdict Forward(EventData ev) => new(RelayAction.Forward, ev, None);
    public static RelayVerdict Drop() => new(RelayAction.Drop, null, None);
    public static RelayVerdict Rewrite(EventData replacement) => new(RelayAction.Rewrite, replacement, None);
    public static RelayVerdict Originate(EventData ev, IReadOnlyList<EventData> extras) =>
        new(RelayAction.Originate, ev, extras);
}
