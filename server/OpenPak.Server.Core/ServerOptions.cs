namespace OpenPak.Server.Core;

/// <summary>The UDP ports the three Photon roles listen on. Defaults match the Photon LoadBalancing layout.</summary>
public sealed class ServerPorts
{
    public int NameServer { get; set; } = 5058;
    public int MasterServer { get; set; } = 5055;
    public int GameServer { get; set; } = 5056;

    /// <summary>Optional second NameServer port, same handler as <see cref="NameServer"/>. Some clients
    /// (e.g. Outbound/Fusion) try a hardcoded alternate UDP port for the Name Server independently of
    /// whatever address GetRegions hands back, regardless of region choice -- unset (null) skips binding
    /// it, which is fine for a client that doesn't do this (e.g. Black Ice).
    ///
    /// This turned out to be half of Photon's modern default UDP port triplet (27000/27001/27002),
    /// confirmed against real Photon Cloud traffic: the client does NOT use whatever port a server
    /// hands back in the Authenticate/GetRegions response's Address parameter -- it takes only the
    /// HOST from that address and always REPLACES the port with its own client-side table
    /// (Photon.Realtime's ProtocolPorts.Udp: NameServer=27000, MasterServer=27001, GameServer=27002),
    /// unless the app was built against the older 5058/5055/5056 scheme (ProtocolPorts.SetUdpDefaultOld).
    /// See MasterServerAlt/GameServerAlt below -- all three must be set together for a modern client.</summary>
    public int? NameServerAlt { get; set; } = null;

    /// <summary>Optional second MasterServer port, same handler as <see cref="MasterServer"/>. See
    /// <see cref="NameServerAlt"/>'s doc comment -- pair with NameServerAlt=27000/GameServerAlt=27002
    /// for a client using Photon's modern default UDP ports (MasterServerAlt=27001).</summary>
    public int? MasterServerAlt { get; set; } = null;

    /// <summary>Optional second GameServer port, same handler as <see cref="GameServer"/>. See
    /// <see cref="NameServerAlt"/>'s doc comment -- pair with NameServerAlt=27000/MasterServerAlt=27001
    /// for a client using Photon's modern default UDP ports (GameServerAlt=27002).</summary>
    public int? GameServerAlt { get; set; } = null;
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
    // Some Fusion clients synchronously wait on a separate P2P/NAT-punch socket and cannot service
    // Photon keepalives during that phase. Keep their authenticated/in-room state long enough for
    // the secondary handshake to complete instead of recreating them as a fresh Connected peer.
    public double DeadTimeoutSeconds { get; set; } = 120;
    // A peer that has joined a room gets this longer grace instead: the friend-invite system applet
    // suspends the game, so an in-room host stops answering keepalives entirely while it is open, and
    // no short timeout survives an applet left on screen. Losing in-room state drops the whole session
    // (the client's "connection interrupted" screen), so reap it only after this long silence.
    public double InRoomDeadTimeoutSeconds { get; set; } = 1800;

    public TimeSpan Maintenance => TimeSpan.FromSeconds(MaintenanceSeconds);
    public TimeSpan PingQuiet => TimeSpan.FromSeconds(PingQuietSeconds);
    public TimeSpan DeadTimeout => TimeSpan.FromSeconds(DeadTimeoutSeconds);
    public TimeSpan InRoomDeadTimeout => TimeSpan.FromSeconds(InRoomDeadTimeoutSeconds);
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

    /// <summary>
    /// TCP port for the health endpoint (<c>/health</c>, <c>/players</c>). The game listeners are
    /// UDP and cannot be probed from outside, so this is the only way anything else can tell a
    /// running server from a silent one. 0 disables it.
    /// </summary>
    public int HealthPort { get; set; } = 8080;
    public ListenerTimings Listener { get; set; } = new();

    /// <summary>Server-authority / anti-cheat validator tunables (detection-only by default).</summary>
    public AnticheatOptions Anticheat { get; set; } = new();

    /// <summary>Playerbot auto-spawn / game-action soak settings (off by default).</summary>
    public BotOptions Bots { get; set; } = new();

    /// <summary>Server plugin loading: external directory + which plugins start disabled.</summary>
    public PluginOptions Plugins { get; set; } = new();

    /// <summary>Team-deathmatch / arena match settings (score cap + auto-reset); off by default.</summary>
    public ArenaOptions Arena { get; set; } = new();

    /// <summary>True when <see cref="Secret"/> is still the shipped placeholder — insecure for anything public.</summary>
    public bool UsesDefaultSecret => Secret == DefaultSecret;

    /// <summary>Returns human-readable validation errors; an empty list means the options are usable.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(Secret))
            errors.Add("Server.Secret must not be empty.");

        var namedPorts = new List<(string name, int port)>
        {
            (nameof(Ports.NameServer), Ports.NameServer),
            (nameof(Ports.MasterServer), Ports.MasterServer),
            (nameof(Ports.GameServer), Ports.GameServer),
        };
        if (Ports.NameServerAlt is { } alt) namedPorts.Add((nameof(Ports.NameServerAlt), alt));
        if (Ports.MasterServerAlt is { } masterAlt) namedPorts.Add((nameof(Ports.MasterServerAlt), masterAlt));
        if (Ports.GameServerAlt is { } gameAlt) namedPorts.Add((nameof(Ports.GameServerAlt), gameAlt));

        foreach (var (name, port) in namedPorts)
            if (port is < 1 or > 65535)
                errors.Add($"Server.Ports.{name} ({port}) is out of the 1..65535 range.");

        var ports = namedPorts.Select(p => p.port).ToArray();
        if (ports.Distinct().Count() != ports.Length)
            errors.Add("Server.Ports.NameServer/MasterServer/GameServer (and any *Alt ports set) must all be distinct.");

        if (Listener.MaintenanceSeconds <= 0)
            errors.Add("Server.Listener.MaintenanceSeconds must be greater than 0.");
        if (Listener.PingQuietSeconds <= 0)
            errors.Add("Server.Listener.PingQuietSeconds must be greater than 0.");
        if (Listener.DeadTimeoutSeconds <= Listener.PingQuietSeconds)
            errors.Add("Server.Listener.DeadTimeoutSeconds must be greater than PingQuietSeconds.");
        if (Listener.InRoomDeadTimeoutSeconds < Listener.DeadTimeoutSeconds)
            errors.Add("Server.Listener.InRoomDeadTimeoutSeconds must be >= DeadTimeoutSeconds (in-room peers get a longer, not shorter, grace).");

        errors.AddRange(Anticheat.Validate());
        errors.AddRange(Bots.Validate());
        errors.AddRange(Arena.Validate());
        return errors;
    }
}
