namespace BlackIce.Server.Data;

/// <summary>
/// Account moderation/administration commands (the SteamID-keyed identity + permission store).
/// Each carries the minimum <see cref="PlayerLevel"/> a remote caller needs; the local server console
/// runs at Console level and so may run all of them.
/// </summary>
public sealed class AccountCommands
{
    private readonly AccountService _accounts;
    public AccountCommands(AccountService accounts) => _accounts = accounts;

    [ConsoleCommand("promote", Aliases = new[] { "demote" }, Usage = "<steamId> <0-3>", MinParts = 3, MinLevel = PlayerLevel.Admin)]
    private string Promote(CommandLine line)
    {
        if (!int.TryParse(line.Parts[2], out var lvl) || lvl is < 0 or > 3) return "level must be 0-3.";
        return _accounts.SetLevel(line.Parts[1], (PlayerLevel)lvl).IsOk
            ? $"{line.Parts[1]} -> {(PlayerLevel)lvl}" : $"no such account: {line.Parts[1]}";
    }

    [ConsoleCommand("ban", Usage = "<steamId>", MinParts = 2, MinLevel = PlayerLevel.Mod)]
    private string Ban(CommandLine line)
        => _accounts.SetBanned(line.Parts[1], true).IsOk ? $"banned {line.Parts[1]}" : $"no such account: {line.Parts[1]}";

    [ConsoleCommand("unban", Usage = "<steamId>", MinParts = 2, MinLevel = PlayerLevel.Mod)]
    private string Unban(CommandLine line)
        => _accounts.SetBanned(line.Parts[1], false).IsOk ? $"unbanned {line.Parts[1]}" : $"no such account: {line.Parts[1]}";

    [ConsoleCommand("list", MinLevel = PlayerLevel.Mod)]
    private string List(CommandLine line)
        => string.Join('\n', _accounts.All().Select(a =>
            $"{a.SteamId} {a.DisplayName} {a.Level}{(a.IsBanned ? " [BANNED]" : "")}"));

    [ConsoleCommand("whois", Usage = "<steamId>", MinParts = 2, MinLevel = PlayerLevel.Mod)]
    private string Whois(CommandLine line)
    {
        var a = _accounts.Find(line.Parts[1]);
        return a is null
            ? $"no such account: {line.Parts[1]}"
            : $"{a.SteamId} \"{a.DisplayName}\" level={a.Level} banned={a.IsBanned} " +
              $"created={a.CreatedUtc:u} lastSeen={a.LastSeenUtc:u} playtime={a.Profile.PlaytimeSeconds}s";
    }

    [ConsoleCommand("code", MinLevel = PlayerLevel.Console)]
    private string Code(CommandLine line) => $"bootstrap code: {_accounts.EnsureBootstrapCode()}";
}
