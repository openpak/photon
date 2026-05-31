namespace BlackIce.Server.Data;

/// <summary>
/// Parses and executes a single server-console admin line, returning text output. Commands are
/// declared as <see cref="ConsoleCommandAttribute"/>-annotated methods and dispatched through a
/// <see cref="CommandRegistry"/>, so adding a command is a one-method change and the help text stays
/// in sync automatically.
/// </summary>
public sealed class ConsoleCommandProcessor
{
    private readonly AccountService _accounts;
    private readonly MotdService? _motd;
    private readonly CommandRegistry _registry = new();

    public ConsoleCommandProcessor(AccountService accounts, MotdService? motd = null)
    {
        _accounts = accounts;
        _motd = motd;
        _registry.Register(this);
    }

    public string Execute(string? line)
        => _registry.TryExecute(line, out var output) ? output : $"unknown command '{Word(line)}'. type 'help'.";

    private static string Word(string? line)
    {
        var t = (line ?? "").Trim();
        var sp = t.IndexOf(' ');
        return (sp < 0 ? t : t[..sp]).ToLowerInvariant();
    }

    [ConsoleCommand("promote", Aliases = new[] { "demote" }, Usage = "<steamId> <0-3>", MinParts = 3)]
    private string Promote(CommandLine line)
    {
        if (!int.TryParse(line.Parts[2], out var lvl) || lvl is < 0 or > 3) return "level must be 0-3.";
        return _accounts.SetLevel(line.Parts[1], (PlayerLevel)lvl).IsOk
            ? $"{line.Parts[1]} -> {(PlayerLevel)lvl}" : $"no such account: {line.Parts[1]}";
    }

    [ConsoleCommand("ban", Usage = "<steamId>", MinParts = 2)]
    private string Ban(CommandLine line)
        => _accounts.SetBanned(line.Parts[1], true).IsOk ? $"banned {line.Parts[1]}" : $"no such account: {line.Parts[1]}";

    [ConsoleCommand("unban", Usage = "<steamId>", MinParts = 2)]
    private string Unban(CommandLine line)
        => _accounts.SetBanned(line.Parts[1], false).IsOk ? $"unbanned {line.Parts[1]}" : $"no such account: {line.Parts[1]}";

    [ConsoleCommand("list")]
    private string List(CommandLine line)
        => string.Join('\n', _accounts.All().Select(a =>
            $"{a.SteamId} {a.DisplayName} {a.Level}{(a.IsBanned ? " [BANNED]" : "")}"));

    [ConsoleCommand("code")]
    private string Code(CommandLine line) => $"bootstrap code: {_accounts.EnsureBootstrapCode()}";

    [ConsoleCommand("motd", Usage = "[text]")]
    private string Motd(CommandLine line)
    {
        if (_motd is null) return "MOTD service unavailable.";
        if (line.Rest.Length == 0) return _motd.GetGlobal() is { } m ? m : "(no MOTD set)";
        _motd.SetGlobal(line.Rest);
        return $"global MOTD set: {line.Rest}";
    }

    [ConsoleCommand("realmmotd", Usage = "<realm> <text>", MinParts = 3)]
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

    [ConsoleCommand("help")]
    private string ShowHelp(CommandLine line) => _registry.HelpText;
}
