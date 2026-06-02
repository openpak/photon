using System.Text;
using BlackIce.Server.Data;
using BlackIce.Server.LoadBalancing.Authority;

namespace BlackIce.Server.LoadBalancing.Plugins;

/// <summary>
/// Built-in plugin contributing <b>read-only</b> developer/diagnostic console commands for operators:
/// dumping a realm's authoritative world-state shadow, filtering entities by kind, listing the verified
/// roster of a room, fuzzy-finding realm names, and a one-line process/runtime summary. Everything here
/// only reads thread-safe singletons (<see cref="RoomRegistry"/> + <see cref="RoomWorldStateRegistry"/>),
/// so the commands run inline on the console thread — no <c>AdminActionQueue</c> needed and nothing is sent
/// to clients. Deliberately avoids the DbContext-backed services (accounts/realms/motd), which are not safe
/// to touch off their own thread. On by default; it adds inspection only, never changes relay behavior.
/// </summary>
public sealed class DevCommandsPlugin : IServerPlugin
{
    public string Name => "dev";
    public string Description => "Read-only developer/diagnostic console commands: dump world-state entities, list rosters, find realms, and report runtime info.";

    public void Configure(PluginBuilder builder)
    {
        // Resolve the shared singletons; fall back to fresh instances so the plugin still loads in a minimal
        // host (the commands then just report "no such realm" until traffic populates the real registries).
        var rooms = (RoomRegistry?)builder.Services.GetService(typeof(RoomRegistry)) ?? new RoomRegistry();
        var worlds = (RoomWorldStateRegistry?)builder.Services.GetService(typeof(RoomWorldStateRegistry)) ?? new RoomWorldStateRegistry();

        builder.AddCommands(new DevCommands(rooms, worlds));
    }
}

/// <summary>
/// Read-only diagnostic command provider (all <see cref="PlayerLevel.Mod"/>+). Runs inline on the console
/// thread: every command takes a thread-safe snapshot of the room/world registries and formats it, never
/// sending packets or mutating state.
/// </summary>
internal sealed class DevCommands
{
    /// <summary>Cap on how many entities a single dump/entities listing prints, so a busy realm doesn't
    /// flood the console; the overflow is summarized with a "(+N more)" note.</summary>
    private const int MaxEntities = 50;

    private readonly RoomRegistry _rooms;
    private readonly RoomWorldStateRegistry _worlds;
    private readonly DateTime _startedUtc = DateTime.UtcNow;

    public DevCommands(RoomRegistry rooms, RoomWorldStateRegistry worlds)
    {
        _rooms = rooms;
        _worlds = worlds;
    }

    [ConsoleCommand("dump", Usage = "<realm>", MinParts = 2, MinLevel = PlayerLevel.Mod)]
    private string Dump(CommandLine line)
    {
        var typed = AfterArg(line, 0);   // realm name may contain spaces
        var realm = _rooms.ResolveName(typed);
        if (realm is null) return $"no such realm: {typed}";

        var alive = _worlds.For(realm).Alive();
        if (alive.Count == 0) return $"\"{realm}\": no alive entities tracked";
        return FormatEntities(realm, alive);
    }

    [ConsoleCommand("entities", Usage = "<realm> [kindSubstring]", MinParts = 2, MinLevel = PlayerLevel.Mod)]
    private string Entities(CommandLine line)
    {
        // The realm name can contain spaces, so we can't simply split on whitespace for the optional filter.
        // The console game's realm name is a fixed phrase; resolve the longest leading span that names a realm,
        // and treat any trailing token as the kind filter. In practice operators pass either "<realm>" or
        // "<realm> <kind>", so try the whole tail as a realm first, then peel the last token off as the filter.
        var tail = AfterArg(line, 0);
        var realm = _rooms.ResolveName(tail);
        string? kindFilter = null;
        if (realm is null)
        {
            int lastSpace = tail.LastIndexOf(' ');
            if (lastSpace > 0)
            {
                realm = _rooms.ResolveName(tail[..lastSpace].Trim());
                kindFilter = tail[(lastSpace + 1)..].Trim();
            }
        }
        if (realm is null) return $"no such realm: {tail}";

        var alive = _worlds.For(realm).Alive();
        var matches = kindFilter is { Length: > 0 }
            ? alive.Where(e => e.Kind is not null && e.Kind.Contains(kindFilter, StringComparison.OrdinalIgnoreCase)).ToList()
            : alive.ToList();

        var filterNote = kindFilter is { Length: > 0 } ? $" matching \"{kindFilter}\"" : "";
        if (matches.Count == 0) return $"\"{realm}\": no alive entities{filterNote}";
        return $"\"{realm}\": {matches.Count} entit{(matches.Count == 1 ? "y" : "ies")}{filterNote}\n" + FormatEntities(realm, matches, includeHeader: false);
    }

    [ConsoleCommand("roster", Usage = "<realm>", MinParts = 2, MinLevel = PlayerLevel.Mod)]
    private string Roster(CommandLine line)
    {
        var typed = AfterArg(line, 0);   // realm name may contain spaces
        var realm = _rooms.ResolveName(typed);
        if (realm is null) return $"no such realm: {typed}";

        var session = _rooms.FindSession(realm);
        var actors = session?.Actors() ?? System.Array.Empty<int>();
        if (actors.Count == 0) return $"\"{realm}\": no actors connected";

        var sb = new StringBuilder();
        sb.Append($"\"{realm}\": {actors.Count} actor(s)");
        foreach (var actor in actors.OrderBy(a => a))
        {
            var peer = session?.PeerOf(actor);
            var steamId = peer?.SteamId ?? "?";
            var verified = peer?.IsVerified ?? false;
            sb.Append($"\nactor {actor} steamId={steamId} verified={verified}");
        }
        return sb.ToString();
    }

    [ConsoleCommand("find", Usage = "<text>", MinParts = 2, MinLevel = PlayerLevel.Mod)]
    private string Find(CommandLine line)
    {
        var text = AfterArg(line, 0);   // search text may contain spaces
        var matches = _rooms.RoomNames
            .Where(n => n.Contains(text, StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n)
            .ToList();
        if (matches.Count == 0) return $"no realms matching \"{text}\"";
        return $"{matches.Count} realm(s) matching \"{text}\":\n" + string.Join('\n', matches);
    }

    [ConsoleCommand("sysinfo", MinLevel = PlayerLevel.Mod)]
    private string SysInfo(CommandLine line)
    {
        var up = DateTime.UtcNow - _startedUtc;
        var heapMb = GC.GetTotalMemory(forceFullCollection: false) / (1024.0 * 1024.0);
        var version = typeof(DevCommands).Assembly.GetName().Version;
        return $"uptime={up:d\\.hh\\:mm\\:ss} heap={heapMb:F1}MB cpus={Environment.ProcessorCount} version={version}";
    }

    // --- helpers ---------------------------------------------------------------------------------

    /// <summary>Formats up to <see cref="MaxEntities"/> world-state entities as one line each, with a
    /// "(+N more)" overflow note. <paramref name="includeHeader"/> prefixes the realm + count line.</summary>
    private static string FormatEntities(string realm, IReadOnlyList<RoomWorldState.Entity> entities, bool includeHeader = true)
    {
        var sb = new StringBuilder();
        if (includeHeader) sb.Append($"\"{realm}\": {entities.Count} alive entit{(entities.Count == 1 ? "y" : "ies")}");

        int shown = 0;
        foreach (var e in entities.OrderBy(e => e.ViewId))
        {
            if (shown >= MaxEntities) break;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append($"view {e.ViewId} {e.Kind ?? "?"} alive={e.Alive} pos=({e.X},{e.Y},{e.Z}) [hasPos={e.HasPosition}]");
            shown++;
        }
        int overflow = entities.Count - shown;
        if (overflow > 0) sb.Append($"\n(+{overflow} more)");
        return sb.ToString();
    }

    /// <summary>Everything after the token at <paramref name="index"/> (so trailing text — a realm name or
    /// search phrase — can contain spaces). Local copy of the same helper used by ServerCommands, kept here
    /// so this plugin carries no shared dependency.</summary>
    private static string AfterArg(CommandLine line, int index)
    {
        var s = line.Raw;
        int pos = 0;
        for (int i = 0; i <= index; i++)
        {
            pos = s.IndexOf(' ', pos);
            if (pos < 0) return "";
            pos++;
        }
        return s[pos..].Trim();
    }
}
