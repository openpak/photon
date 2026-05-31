namespace BlackIce.Server.LoadBalancing;

/// <summary>
/// A sink the Game role calls when an actor joins or leaves a room, so optional logic (e.g. the game-mode
/// plugin's team assignment) can react without that logic living in the core handler. Implemented by the
/// plugin manager; vanilla runs with none wired.
/// </summary>
public interface IRoomLifecycleListener
{
    void OnJoined(string roomName, int actor, RoomSession session);
    void OnLeft(string roomName, int actor, RoomSession session);
}
