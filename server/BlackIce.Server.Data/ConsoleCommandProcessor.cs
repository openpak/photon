namespace BlackIce.Server.Data;

/// <summary>
/// Convenience facade over a <see cref="CommandRegistry"/> wired with the data-layer command providers
/// (accounts + MOTD), dispatching at Console level. The host builds a richer registry (adding runtime
/// server/room commands) directly; this type keeps the data-layer command surface independently usable
/// and testable.
/// </summary>
public sealed class ConsoleCommandProcessor
{
    private readonly CommandRegistry _registry = new();

    public ConsoleCommandProcessor(AccountService accounts, MotdService? motd = null)
    {
        _registry.Register(new AccountCommands(accounts));
        _registry.Register(new MotdCommands(motd));
    }

    /// <summary>Executes one line at Console level (the local server console has full authority).</summary>
    public string Execute(string? line)
        => _registry.TryExecute(line, PlayerLevel.Console, out var output)
            ? output
            : $"unknown command '{Word(line)}'. type 'help'.";

    private static string Word(string? line)
    {
        var t = (line ?? "").Trim();
        var sp = t.IndexOf(' ');
        return (sp < 0 ? t : t[..sp]).ToLowerInvariant();
    }
}
