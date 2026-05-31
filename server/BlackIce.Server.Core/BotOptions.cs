namespace BlackIce.Server.Core;

/// <summary>
/// Playerbot soak/anti-cheat-exercise settings. With <see cref="AutoSpawnPerRealm"/> &gt; 0 the host
/// spawns that many synthetic players into each realm on startup; with <see cref="EmitGameActions"/>
/// on they also drive a rotating script of legitimate and cheating gameplay traffic through the relay,
/// exercising the authority/anti-cheat validators. Both default off — bots are opt-in.
/// </summary>
public sealed class BotOptions
{
    public int AutoSpawnPerRealm { get; set; } = 0;
    public bool EmitGameActions { get; set; } = false;

    /// <summary>When true, auto-spawned bots are added to a realm's advertised player count in the lobby
    /// server browser, so a stocked realm looks populated to connecting players. Off by default — some
    /// operators prefer the browser to show only real players. (The server console's own <c>rooms</c>
    /// listing always shows the real bot count regardless of this flag.)</summary>
    public bool CountInLobby { get; set; } = false;

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (AutoSpawnPerRealm < 0) errors.Add("Server.Bots.AutoSpawnPerRealm must be >= 0.");
        return errors;
    }
}
