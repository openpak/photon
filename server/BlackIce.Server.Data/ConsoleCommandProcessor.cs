namespace BlackIce.Server.Data;

/// <summary>Parses and executes a single server-console admin line, returning text output.</summary>
public sealed class ConsoleCommandProcessor
{
    private readonly AccountService _accounts;
    private readonly MotdService? _motd;

    public ConsoleCommandProcessor(AccountService accounts, MotdService? motd = null)
    {
        _accounts = accounts;
        _motd = motd;
    }

    public string Execute(string line)
    {
        var trimmed = line.Trim();
        var sp = trimmed.IndexOf(' ');
        var cmd = (sp < 0 ? trimmed : trimmed[..sp]).ToLowerInvariant();
        var rest = sp < 0 ? "" : trimmed[(sp + 1)..].Trim();
        var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "";

        switch (cmd)
        {
            case "promote" or "demote" when parts.Length == 3 && int.TryParse(parts[2], out var lvl) && lvl is >= 0 and <= 3:
                return _accounts.SetLevel(parts[1], (PlayerLevel)lvl)
                    ? $"{parts[1]} -> {(PlayerLevel)lvl}" : $"no such account: {parts[1]}";
            case "ban" when parts.Length == 2:
                return _accounts.SetBanned(parts[1], true) ? $"banned {parts[1]}" : $"no such account: {parts[1]}";
            case "unban" when parts.Length == 2:
                return _accounts.SetBanned(parts[1], false) ? $"unbanned {parts[1]}" : $"no such account: {parts[1]}";
            case "list":
                return string.Join('\n', _accounts.All().Select(a =>
                    $"{a.SteamId} {a.DisplayName} {a.Level}{(a.IsBanned ? " [BANNED]" : "")}"));
            case "code":
                return $"bootstrap code: {_accounts.EnsureBootstrapCode()}";
            case "motd" when _motd is not null && rest.Length == 0:
                return _motd.GetGlobal() is { } m ? m : "(no MOTD set)";
            case "motd" when _motd is not null:
                _motd.SetGlobal(rest);
                return $"global MOTD set: {rest}";
            case "realmmotd" when _motd is not null && parts.Length >= 3:
                var realmName = parts[1];
                // text = everything after the realm token. Slice from `rest`
                // ("<realm> <text>") rather than content-searching the raw line,
                // which would mis-match if the realm name occurs earlier (e.g.
                // a realm whose name is a substring of "realmmotd"). The
                // parts.Length >= 3 guard guarantees a space exists in `rest`.
                var text = rest[(rest.IndexOf(' ') + 1)..].Trim();
                return _motd.SetRealm(realmName, text)
                    ? $"{realmName} MOTD set: {text}" : $"no such realm: {realmName}";
            case "help":
                return Help;
            default:
                return $"unknown command '{cmd}'. type 'help'.";
        }
    }

    public const string Help =
        "commands: promote <steamId> <0-3> | demote <steamId> <0-3> | ban <steamId> | unban <steamId> | " +
        "list | code | motd [text] | realmmotd <realm> <text> | help";
}
