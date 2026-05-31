using BlackIce.Server.Data;

namespace BlackIce.Server.LoadBalancing.Plugins;

/// <summary>Console commands to inspect and toggle server plugins at runtime (Admin).</summary>
public sealed class PluginCommands
{
    private readonly PluginManager _plugins;
    public PluginCommands(PluginManager plugins) => _plugins = plugins;

    [ConsoleCommand("plugins", MinLevel = PlayerLevel.Admin)]
    private string ListPlugins(CommandLine line)
    {
        var list = _plugins.List();
        return list.Count == 0
            ? "(no plugins)"
            : string.Join('\n', list.Select(p => $"{(p.Enabled ? "[on] " : "[off]")} {p.Name} — {p.Description}"));
    }

    [ConsoleCommand("plugin", Usage = "<enable|disable> <name>", MinParts = 3, MinLevel = PlayerLevel.Admin)]
    private string TogglePlugin(CommandLine line)
    {
        var verb = line.Parts[1].ToLowerInvariant();
        if (verb is not ("enable" or "disable")) return "usage: plugin <enable|disable> <name>";
        return _plugins.SetEnabled(line.Parts[2], verb == "enable")
            ? $"plugin '{line.Parts[2]}' {verb}d"
            : $"no such plugin: {line.Parts[2]}";
    }
}
