namespace BlackIce.Server.LoadBalancing.Bots;

/// <summary>One step of bot decision-making, producing where the bot now is. Movement first; combat later.</summary>
public readonly record struct BotPositionUpdate(float X, float Y, float Z);

public interface IBotBehavior
{
    BotPositionUpdate Tick();
}
