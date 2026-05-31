namespace BlackIce.Server.LoadBalancing.Bots;

/// <summary>A generated synthetic-player identity. All fields are free-form (the client does not
/// validate them) and map to the player custom properties a real client sets after joining.</summary>
public sealed record BotIdentity(string Name, int ModelIndex, float[][] ModelColors, int Level, int Team);
