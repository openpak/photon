using BlackIce.Server.Core;

namespace BlackIce.Server.LoadBalancing.Plugins;

/// <summary>
/// Owns the loaded server plugins and their contributions. Built-in and externally-loaded plugins are
/// <see cref="Add"/>ed, then <see cref="ConfigureAll"/> collects each one's interceptors / commands /
/// hooks. The relay pulls per-room interceptors via <see cref="InterceptorsFor"/> (each gated to no-op
/// while its plugin is disabled), the Game role drives <see cref="OnJoined"/>/<see cref="OnLeft"/>, and
/// the console registers <see cref="CommandProviders"/>. Enable/disable is live (no chain rebuild): a
/// disabled plugin's interceptors forward unchanged and its hooks aren't dispatched.
/// </summary>
public sealed class PluginManager : IRoomLifecycleListener
{
    /// <summary>One loaded plugin and the contributions it registered.</summary>
    public sealed class Entry
    {
        public required IServerPlugin Plugin { get; init; }
        public bool Enabled { get; set; }
        public List<Func<IEventInterceptor>> Interceptors { get; } = new();
        public List<object> Commands { get; } = new();
        public List<Action<RoomActorContext>> Joined { get; } = new();
        public List<Action<RoomActorContext>> Left { get; } = new();
    }

    private readonly List<Entry> _entries = new();
    private bool _configured;

    /// <summary>Registers a plugin with its initial enabled state. Call before <see cref="ConfigureAll"/>.</summary>
    public void Add(IServerPlugin plugin, bool enabled)
    {
        if (_entries.Any(e => string.Equals(e.Plugin.Name, plugin.Name, StringComparison.OrdinalIgnoreCase)))
        {
            Log.Warn("Plugins", $"duplicate plugin name '{plugin.Name}' ignored");
            return;
        }
        _entries.Add(new Entry { Plugin = plugin, Enabled = enabled });
    }

    /// <summary>Calls Configure on every registered plugin once, collecting contributions. A throwing
    /// plugin is logged and force-disabled rather than taking the server down.</summary>
    public void ConfigureAll(IServiceProvider services)
    {
        if (_configured) return;
        _configured = true;
        foreach (var e in _entries)
        {
            try
            {
                e.Plugin.Configure(new PluginBuilder(services, e));
                Log.Info("Plugins", $"loaded '{e.Plugin.Name}' [{(e.Enabled ? "enabled" : "disabled")}] — {e.Plugin.Description}");
            }
            catch (Exception ex)
            {
                Log.Exception("Plugins", $"plugin '{e.Plugin.Name}' Configure threw — disabling", ex);
                e.Enabled = false;
            }
        }
    }

    /// <summary>The interceptors for one room, from all plugins, each gated to no-op while its plugin is off.</summary>
    public IReadOnlyList<IEventInterceptor> InterceptorsFor(string room)
    {
        var list = new List<IEventInterceptor>();
        foreach (var e in _entries)
            foreach (var factory in e.Interceptors)
                list.Add(new PluginGate(e, factory()));
        return list;
    }

    /// <summary>Command providers from every plugin (registered once; a disabled plugin's commands simply act on disabled logic).</summary>
    public IEnumerable<object> CommandProviders => _entries.SelectMany(e => e.Commands);

    public void OnJoined(string roomName, int actor, RoomSession session) => Dispatch(roomName, actor, session, e => e.Joined);
    public void OnLeft(string roomName, int actor, RoomSession session) => Dispatch(roomName, actor, session, e => e.Left);

    private void Dispatch(string room, int actor, RoomSession session, Func<Entry, List<Action<RoomActorContext>>> select)
    {
        var ctx = new RoomActorContext(room, actor, session);
        foreach (var e in _entries)
        {
            if (!e.Enabled) continue;
            foreach (var handler in select(e))
                try { handler(ctx); }
                catch (Exception ex) { Log.Exception("Plugins", $"plugin '{e.Plugin.Name}' hook threw", ex); }
        }
    }

    /// <summary>Enables/disables a plugin by name at runtime; false if unknown.</summary>
    public bool SetEnabled(string name, bool enabled)
    {
        var e = _entries.FirstOrDefault(x => string.Equals(x.Plugin.Name, name, StringComparison.OrdinalIgnoreCase));
        if (e is null) return false;
        e.Enabled = enabled;
        Log.Info("Plugins", $"plugin '{e.Plugin.Name}' {(enabled ? "enabled" : "disabled")}");
        return true;
    }

    public IReadOnlyList<(string Name, bool Enabled, string Description)> List() =>
        _entries.Select(e => (e.Plugin.Name, e.Enabled, e.Plugin.Description)).ToList();
}

/// <summary>Wraps a plugin's interceptor so it forwards unchanged while the plugin is disabled — the
/// mechanism behind live enable/disable without rebuilding any room's chain.</summary>
internal sealed class PluginGate : IEventInterceptor
{
    private readonly PluginManager.Entry _entry;
    private readonly IEventInterceptor _inner;

    public PluginGate(PluginManager.Entry entry, IEventInterceptor inner) { _entry = entry; _inner = inner; }

    public RelayVerdict Intercept(EventContext ctx) =>
        _entry.Enabled ? _inner.Intercept(ctx) : RelayVerdict.Forward(ctx.Event);
}
