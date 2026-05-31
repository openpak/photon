using System.Reflection;

namespace BlackIce.Server.Data;

/// <summary>
/// Marks a method as a console/admin command. The method must take a single <see cref="CommandLine"/>
/// and return the string to print. Declaring commands this way (instead of a central switch) means a
/// new command is added by writing one annotated method — the registry discovers it, enforces the
/// minimum argument count, and contributes it to the auto-generated help. Mirrors TrinityCore's
/// declarative ChatCommandTable; <see cref="MinLevel"/> is the seam for a future permissioned remote
/// console (the local server console always runs at the highest level).
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class ConsoleCommandAttribute : Attribute
{
    public string Name { get; }
    public string[] Aliases { get; init; } = Array.Empty<string>();
    public string Usage { get; init; } = "";
    /// <summary>Minimum whitespace-separated tokens (including the command word) required to run.</summary>
    public int MinParts { get; init; } = 1;
    public PlayerLevel MinLevel { get; init; } = PlayerLevel.Console;

    public ConsoleCommandAttribute(string name) => Name = name;
}

/// <summary>A parsed command line handed to a command method.</summary>
/// <param name="Raw">The full trimmed line.</param>
/// <param name="Command">The command word (token 0), lower-cased.</param>
/// <param name="Rest">Everything after the command word, trimmed.</param>
/// <param name="Parts">All whitespace-separated tokens, including the command word.</param>
public readonly record struct CommandLine(string Raw, string Command, string Rest, IReadOnlyList<string> Parts);

/// <summary>
/// Reflects the <see cref="ConsoleCommandAttribute"/>-annotated methods of one or more targets into a
/// dispatch table, then routes a raw line to the matching method (validating its minimum arg count)
/// and exposes an auto-generated help listing.
/// </summary>
public sealed class CommandRegistry
{
    private sealed record Entry(Func<CommandLine, string> Invoke, int MinParts, string Usage, PlayerLevel MinLevel);

    private sealed record HelpEntry(string Names, string Usage, PlayerLevel MinLevel);

    private readonly Dictionary<string, Entry> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<HelpEntry> _help = new();

    /// <summary>Discovers and registers every annotated method on <paramref name="target"/>.</summary>
    public CommandRegistry Register(object target)
    {
        foreach (var method in target.GetType()
                     .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            var attr = method.GetCustomAttribute<ConsoleCommandAttribute>();
            if (attr is null) continue;

            var entry = new Entry(line => method.Invoke(target, new object[] { line }) as string ?? "",
                                  attr.MinParts, attr.Usage, attr.MinLevel);
            _byName[attr.Name] = entry;
            foreach (var alias in attr.Aliases) _byName[alias] = entry;

            var names = attr.Aliases.Length == 0 ? attr.Name : $"{attr.Name}|{string.Join("|", attr.Aliases)}";
            _help.Add(new HelpEntry(names, attr.Usage, attr.MinLevel));
        }
        return this;
    }

    /// <summary>
    /// Runs the command on <paramref name="raw"/> as a caller of permission tier <paramref name="caller"/>.
    /// Returns false (with <paramref name="output"/> unset) only when the command word is unknown, so the
    /// caller can render its own "unknown command" hint. Unknown-but-"help" yields the help listing; a
    /// command above the caller's tier is refused; too few arguments yields the usage string.
    /// </summary>
    public bool TryExecute(string? raw, PlayerLevel caller, out string output)
    {
        var trimmed = (raw ?? "").Trim();
        var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) { output = ""; return true; }

        var cmd = parts[0];
        if (string.Equals(cmd, "help", StringComparison.OrdinalIgnoreCase)) { output = HelpFor(caller); return true; }
        if (!_byName.TryGetValue(cmd, out var entry)) { output = ""; return false; }
        if (caller < entry.MinLevel) { output = $"'{cmd}' requires {entry.MinLevel}+ (you are {caller})"; return true; }
        if (parts.Length < entry.MinParts) { output = $"usage: {cmd} {entry.Usage}".TrimEnd(); return true; }

        var sp = trimmed.IndexOf(' ');
        var rest = sp < 0 ? "" : trimmed[(sp + 1)..].Trim();
        output = entry.Invoke(new CommandLine(trimmed, cmd.ToLowerInvariant(), rest, parts));
        return true;
    }

    /// <summary>Every registered command + usage, for the (highest-tier) help listing.</summary>
    public string HelpText => HelpFor(PlayerLevel.Console);

    /// <summary>Help listing limited to the commands a caller of <paramref name="caller"/> may run.</summary>
    public string HelpFor(PlayerLevel caller) =>
        "commands: " + string.Join(" | ", _help
            .Where(h => caller >= h.MinLevel)
            .Select(h => string.IsNullOrEmpty(h.Usage) ? h.Names : $"{h.Names} {h.Usage}"));
}
