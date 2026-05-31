namespace BlackIce.Server.Data;

/// <summary>Message-of-the-Day commands (global and per-realm). Always registered so they appear in
/// help; if no MOTD service was wired they report that rather than acting.</summary>
public sealed class MotdCommands
{
    private readonly MotdService? _motd;
    public MotdCommands(MotdService? motd) => _motd = motd;

    [ConsoleCommand("motd", Usage = "[text]", MinLevel = PlayerLevel.Admin)]
    private string Motd(CommandLine line)
    {
        if (_motd is null) return "MOTD service unavailable.";
        if (line.Rest.Length == 0) return _motd.GetGlobal() is { } m ? m : "(no MOTD set)";
        _motd.SetGlobal(line.Rest);
        return $"global MOTD set: {line.Rest}";
    }

    [ConsoleCommand("realmmotd", Usage = "<realm> <text>", MinParts = 3, MinLevel = PlayerLevel.Admin)]
    private string RealmMotd(CommandLine line)
    {
        if (_motd is null) return "MOTD service unavailable.";
        var realmName = line.Parts[1];
        // text = everything after the realm token. Slice from Rest ("<realm> <text>") rather than
        // content-searching the raw line, which would mis-match if the realm name occurs earlier (e.g.
        // a realm whose name is a substring of "realmmotd"). MinParts=3 normally guarantees a space in
        // Rest; the >= 0 check is belt-and-braces against a future change to MinParts.
        var sp = line.Rest.IndexOf(' ');
        var text = sp >= 0 ? line.Rest[(sp + 1)..].Trim() : "";
        return _motd.SetRealm(realmName, text).IsOk ? $"{realmName} MOTD set: {text}" : $"no such realm: {realmName}";
    }
}
