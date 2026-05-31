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
}
