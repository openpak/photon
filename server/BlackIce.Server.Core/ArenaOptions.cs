namespace BlackIce.Server.Core;

/// <summary>
/// Team-deathmatch / arena match settings for the <c>arena</c> plugin. In a Team-vs-Team realm the plugin
/// turns the continuous relay into a scored match: each server-modelled kill credits the killer's team a
/// point, the first team to <see cref="ScoreCap"/> wins, and (when <see cref="ResetOnWin"/> is set) the
/// match resets so play loops like an arcade arena. Off by default — vanilla until enabled.
/// </summary>
public sealed class ArenaOptions
{
    /// <summary>Master switch. When false the plugin is inert even if loaded/enabled.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Points (kills) a team needs to win the match.</summary>
    public int ScoreCap { get; set; } = 25;

    /// <summary>When true, the match auto-resets (scores cleared, new round announced) after a win; when
    /// false it stays ended until an admin runs <c>arena reset</c>.</summary>
    public bool ResetOnWin { get; set; } = true;

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (ScoreCap < 1) errors.Add("Server.Arena.ScoreCap must be >= 1.");
        return errors;
    }
}
