using System.ComponentModel.DataAnnotations;

namespace BlackIce.Server.Data;

/// <summary>A persistent realm definition: a named room plus its native-knob ruleset.</summary>
public class Realm
{
    [Key] public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public bool Pvp { get; set; }
    public int HackDifficultyIncrease { get; set; }
    public string Password { get; set; } = "";       // "" = open
    public int MaxPlayers { get; set; } = 8;
    public bool IsVisible { get; set; } = true;
    public bool IsEnabled { get; set; } = true;
    public string ExtraJson { get; set; } = "{}";     // future server-enforced rules (stored, not enforced)
    /// <summary>Per-realm MOTD override. Null/empty = inherit the global MOTD.</summary>
    public string? Motd { get; set; }
}
