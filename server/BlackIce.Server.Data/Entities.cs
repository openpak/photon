using System.ComponentModel.DataAnnotations;

namespace BlackIce.Server.Data;

/// <summary>A persistent player identity, keyed by SteamID, auto-created on first connect.</summary>
public class Account
{
    [Key] public string SteamId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public PlayerLevel Level { get; set; } = PlayerLevel.Player;
    public bool IsBanned { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;
    public Profile Profile { get; set; } = new();
}

/// <summary>Per-account profile data (placeholders now, room to grow).</summary>
public class Profile
{
    [Key] public string SteamId { get; set; } = "";
    public long PlaytimeSeconds { get; set; }
    public string Notes { get; set; } = "";
}

/// <summary>Single-row server state, e.g. the one-time bootstrap code.</summary>
public class ServerState
{
    [Key] public int Id { get; set; } = 1;
    public string? BootstrapCode { get; set; }
    public bool BootstrapClaimed { get; set; }
    /// <summary>Global Message of the Day shown to players on join. Null/empty = none.</summary>
    public string? Motd { get; set; }
}
