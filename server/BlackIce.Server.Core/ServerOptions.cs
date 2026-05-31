namespace BlackIce.Server.Core;

/// <summary>The UDP ports the three Photon roles listen on. Defaults match the Photon LoadBalancing layout.</summary>
public sealed class ServerPorts
{
    public int NameServer { get; set; } = 5058;
    public int MasterServer { get; set; } = 5055;
    public int GameServer { get; set; } = 5056;
}

/// <summary>
/// Keepalive / dead-peer cleanup cadence for a <see cref="UdpListener"/>, expressed in seconds so it
/// binds cleanly from JSON. The defaults (1s maintenance, ping after 3s quiet, evict after 10s) match
/// Photon's behavior and the values previously hard-coded in the listener.
/// </summary>
public sealed class ListenerTimings
{
    public double MaintenanceSeconds { get; set; } = 1;
    public double PingQuietSeconds { get; set; } = 3;
    public double DeadTimeoutSeconds { get; set; } = 10;

    public TimeSpan Maintenance => TimeSpan.FromSeconds(MaintenanceSeconds);
    public TimeSpan PingQuiet => TimeSpan.FromSeconds(PingQuietSeconds);
    public TimeSpan DeadTimeout => TimeSpan.FromSeconds(DeadTimeoutSeconds);
}

/// <summary>
/// Server-runtime knobs that used to live as scattered constants in Program.cs and UdpListener: the
/// token-signing secret, the per-role ports, and the listener cadence. Gathered here so they are
/// configurable per deployment and validated once at startup. <see cref="Validate"/> returns a list
/// of hard errors (empty = OK); soft advice (e.g. running on the default secret) is surfaced via
/// <see cref="UsesDefaultSecret"/> so the host can warn without refusing to start.
/// </summary>
public sealed class ServerOptions
{
    /// <summary>The placeholder secret a fresh config ships with; running on it should be flagged.</summary>
    public const string DefaultSecret = "change-me-platform-secret";

    /// <summary>HMAC key the Name/Master/Game roles sign and validate auth tokens with. Must be set per deployment.</summary>
    public string Secret { get; set; } = DefaultSecret;

    public ServerPorts Ports { get; set; } = new();
    public ListenerTimings Listener { get; set; } = new();

    /// <summary>True when <see cref="Secret"/> is still the shipped placeholder — insecure for anything public.</summary>
    public bool UsesDefaultSecret => Secret == DefaultSecret;

    /// <summary>Returns human-readable validation errors; an empty list means the options are usable.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(Secret))
            errors.Add("Server.Secret must not be empty.");

        foreach (var (name, port) in new[]
                 {
                     (nameof(Ports.NameServer), Ports.NameServer),
                     (nameof(Ports.MasterServer), Ports.MasterServer),
                     (nameof(Ports.GameServer), Ports.GameServer),
                 })
            if (port is < 1 or > 65535)
                errors.Add($"Server.Ports.{name} ({port}) is out of the 1..65535 range.");

        var ports = new[] { Ports.NameServer, Ports.MasterServer, Ports.GameServer };
        if (ports.Distinct().Count() != ports.Length)
            errors.Add("Server.Ports.NameServer/MasterServer/GameServer must all be distinct.");

        if (Listener.MaintenanceSeconds <= 0)
            errors.Add("Server.Listener.MaintenanceSeconds must be greater than 0.");
        if (Listener.PingQuietSeconds <= 0)
            errors.Add("Server.Listener.PingQuietSeconds must be greater than 0.");
        if (Listener.DeadTimeoutSeconds <= Listener.PingQuietSeconds)
            errors.Add("Server.Listener.DeadTimeoutSeconds must be greater than PingQuietSeconds.");

        return errors;
    }
}
