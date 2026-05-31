namespace BlackIce.Server.Data;

/// <summary>Realm inspection commands (the persisted realm/ruleset definitions). Editing realm rulesets
/// at runtime is intentionally out of scope here; realms are seeded from config and managed in the DB.</summary>
public sealed class RealmCommands
{
    private readonly RealmService _realms;
    public RealmCommands(RealmService realms) => _realms = realms;

    [ConsoleCommand("realms", MinLevel = PlayerLevel.Mod)]
    private string Realms(CommandLine line)
    {
        var all = _realms.All();
        if (all.Count == 0) return "(no realms)";
        return string.Join('\n', all.Select(r =>
            $"{r.Name} \"{r.DisplayName}\" mode={r.Mode} pvp={r.Pvp} max={r.MaxPlayers} hack=+{r.HackDifficultyIncrease} " +
            $"{(r.IsEnabled ? "enabled" : "DISABLED")}{(r.IsVisible ? "" : " hidden")}{(r.Password.Length > 0 ? " locked" : "")}"));
    }

    [ConsoleCommand("realm", Usage = "<name>", MinParts = 2, MinLevel = PlayerLevel.Mod)]
    private string Realm(CommandLine line)
    {
        var r = _realms.Get(line.Parts[1]);
        return r is null
            ? $"no such realm: {line.Parts[1]}"
            : $"{r.Name} \"{r.DisplayName}\" mode={r.Mode} pvp={r.Pvp} maxPlayers={r.MaxPlayers} hackDifficulty=+{r.HackDifficultyIncrease} " +
              $"enabled={r.IsEnabled} visible={r.IsVisible} locked={r.Password.Length > 0} motd={(r.Motd is null ? "<none>" : $"\"{r.Motd}\"")}";
    }

    private static readonly string[] KnownModes = { "FreeForAll", "TeamVsTeam", "Coop" };

    [ConsoleCommand("setmode", Usage = "<realm> <FreeForAll|TeamVsTeam|Coop>", MinParts = 3, MinLevel = PlayerLevel.Admin)]
    private string SetMode(CommandLine line)
    {
        var canonical = KnownModes.FirstOrDefault(m => string.Equals(m, line.Parts[2], StringComparison.OrdinalIgnoreCase));
        if (canonical is null) return $"unknown mode '{line.Parts[2]}' (use {string.Join('|', KnownModes)})";
        var r = _realms.Get(line.Parts[1]);
        if (r is null) return $"no such realm: {line.Parts[1]}";
        r.Mode = canonical;
        _realms.Upsert(r);
        return $"{r.Name} mode -> {canonical} (applies to new joins)";
    }

    [ConsoleCommand("realmcreate", Usage = "<name>", MinParts = 2, MinLevel = PlayerLevel.Admin)]
    private string Create(CommandLine line)
    {
        var name = line.Rest;
        if (_realms.Get(name) is not null) return $"realm already exists: {name}";
        _realms.Upsert(new Realm { Name = name, DisplayName = name });
        return $"created realm \"{name}\" (FreeForAll, enabled)";
    }

    [ConsoleCommand("realmdelete", Usage = "<name>", MinParts = 2, MinLevel = PlayerLevel.Admin)]
    private string Delete(CommandLine line)
        => _realms.Delete(line.Rest).IsOk ? $"deleted realm \"{line.Rest}\"" : $"no such realm: {line.Rest}";

    [ConsoleCommand("realmenable", Usage = "<name>", MinParts = 2, MinLevel = PlayerLevel.Admin)]
    private string Enable(CommandLine line) => SetEnabled(line.Rest, true);

    [ConsoleCommand("realmdisable", Usage = "<name>", MinParts = 2, MinLevel = PlayerLevel.Admin)]
    private string Disable(CommandLine line) => SetEnabled(line.Rest, false);

    private string SetEnabled(string name, bool on)
    {
        var r = _realms.Get(name);
        if (r is null) return $"no such realm: {name}";
        r.IsEnabled = on;
        _realms.Upsert(r);
        return $"{r.Name} {(on ? "enabled" : "disabled")}";
    }
}
