using BlackIce.Server.Data;

namespace BlackIce.Server.LoadBalancing.Plugins;

/// <summary>Console commands to inspect, toggle, and hot-load/unload server plugins at runtime (Admin).</summary>
public sealed class PluginCommands
{
    private readonly PluginManager _plugins;
    private readonly IServiceProvider _services;
    public PluginCommands(PluginManager plugins, IServiceProvider services)
    {
        _plugins = plugins;
        _services = services;
    }

    [ConsoleCommand("plugins", MinLevel = PlayerLevel.Admin)]
    private string ListPlugins(CommandLine line)
    {
        var list = _plugins.List();
        return list.Count == 0
            ? "(no plugins)"
            : string.Join('\n', list.Select(p =>
                $"{(p.Enabled ? "[on] " : "[off]")} {p.Name}{(p.External ? " (external)" : "")} — {p.Description}"));
    }

    [ConsoleCommand("plugin", Usage = "<enable|disable|load|unload> <name|file>", MinParts = 3, MinLevel = PlayerLevel.Admin)]
    private string Plugin(CommandLine line)
    {
        var verb = line.Parts[1].ToLowerInvariant();
        // The argument may be a filesystem path with spaces (load), so take everything after the verb.
        var arg = string.Join(' ', line.Parts.Skip(2));
        switch (verb)
        {
            case "enable":
            case "disable":
                return _plugins.SetEnabled(arg, verb == "enable")
                    ? $"plugin '{arg}' {verb}d"
                    : $"no such plugin: {arg}";
            case "load":
                var loaded = _plugins.LoadFile(arg, _services);
                return loaded.Count == 0
                    ? $"loaded no plugins from '{arg}' (missing, no IServerPlugin, or load error — see log)"
                    : $"loaded + enabled: {string.Join(", ", loaded)}";
            case "unload":
                return _plugins.Unload(arg)
                    ? $"plugin '{arg}' unloaded"
                    : $"can't unload '{arg}' (unknown or built-in — built-ins can only be disabled)";
            default:
                return "usage: plugin <enable|disable|load|unload> <name|file>";
        }
    }
}
