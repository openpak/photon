using System.Runtime.Loader;
using BlackIce.Photon;
using BlackIce.Server.Core;

namespace BlackIce.Server.LoadBalancing.Plugins;

/// <summary>
/// Owns the loaded server plugins and their contributions. Built-in and externally-loaded plugins are
/// added (built-ins compiled in; externals from a directory, each in its own collectible load context),
/// then <see cref="ConfigureAll"/> collects each one's interceptors / commands / hooks. The relay calls
/// <see cref="Evaluate"/> per event, so enable / disable / load / unload take effect immediately with no
/// chain rebuild and no lingering references — the key to hot-unloading an external plugin. The Game
/// role drives <see cref="OnJoined"/>/<see cref="OnLeft"/>, and the console registers
/// <see cref="CommandProviders"/>.
/// </summary>
public sealed class PluginManager : IRoomLifecycleListener
{
    /// <summary>One loaded plugin and the contributions it registered.</summary>
    public sealed class Entry
    {
        public required IServerPlugin Plugin { get; init; }
        public bool Enabled { get; set; }
        public bool Configured { get; set; }
        public AssemblyLoadContext? Context { get; init; }   // null for built-ins; set (collectible) for external
        public List<Func<IEventInterceptor>> Interceptors { get; } = new();
        public List<object> Commands { get; } = new();
        public List<Action<RoomActorContext>> Joined { get; } = new();
        public List<Action<RoomActorContext>> Left { get; } = new();
    }

    private readonly object _gate = new();
    private readonly List<Entry> _entries = new();
    private int _generation;   // bumped whenever the active set changes, invalidating the per-room chain cache

    // Per-(plugin, room) interceptor instances — persistent so per-room state (movement history, rate
    // windows) survives toggling OTHER plugins; dropped only when a plugin is unloaded.
    private readonly Dictionary<(Entry, string), IEventInterceptor[]> _instances = new();
    private readonly Dictionary<string, (int Gen, IEventInterceptor[] Chain)> _activeCache = new();

    /// <summary>Registers a built-in plugin (no load context).</summary>
    public void Add(IServerPlugin plugin, bool enabled) => Add(plugin, enabled, context: null);

    private void Add(IServerPlugin plugin, bool enabled, AssemblyLoadContext? context)
    {
        lock (_gate)
        {
            if (_entries.Any(e => string.Equals(e.Plugin.Name, plugin.Name, StringComparison.OrdinalIgnoreCase)))
            {
                Log.Warn("Plugins", $"duplicate plugin name '{plugin.Name}' ignored");
                return;
            }
            _entries.Add(new Entry { Plugin = plugin, Enabled = enabled, Context = context });
            _generation++;
        }
    }

    /// <summary>Loads external plugin DLLs from a directory, each into its own collectible context.</summary>
    public void LoadDirectory(string? directory, IReadOnlyCollection<string> disabled)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;
        foreach (var path in Directory.EnumerateFiles(directory, "*.dll"))
            foreach (var (plugin, ctx) in PluginLoader.LoadExternal(path))
                Add(plugin, enabled: !disabled.Contains(plugin.Name, StringComparer.OrdinalIgnoreCase), context: ctx);
    }

    /// <summary>Configures any not-yet-configured plugins, collecting contributions. Safe to call again
    /// after a runtime load. A throwing plugin is logged and force-disabled.</summary>
    public void ConfigureAll(IServiceProvider services)
    {
        Entry[] pending;
        lock (_gate) pending = _entries.Where(e => !e.Configured).ToArray();
        foreach (var e in pending)
        {
            try
            {
                e.Plugin.Configure(new PluginBuilder(services, e));
                e.Configured = true;
                Log.Info("Plugins", $"loaded '{e.Plugin.Name}' [{(e.Enabled ? "enabled" : "disabled")}]{(e.Context is null ? "" : " (external)")} — {e.Plugin.Description}");
            }
            catch (Exception ex)
            {
                Log.Exception("Plugins", $"plugin '{e.Plugin.Name}' Configure threw — disabling", ex);
                e.Enabled = false;
                e.Configured = true;
            }
        }
        lock (_gate) _generation++;
    }

    /// <summary>
    /// Runs the active plugins' interceptors for the event's room and composes their verdicts: a
    /// <see cref="RelayAction.Rewrite"/> updates the working event and the chain continues (so later
    /// interceptors — e.g. the anti-cheat/game-mode validators — see the rewritten event and can still
    /// veto it), an <see cref="RelayAction.Originate"/> additionally accumulates its server-authored
    /// extras, and a single <see cref="RelayAction.Drop"/> short-circuits and discards everything
    /// (a validator veto always wins). A throwing interceptor is caught and skipped (treated as Forward)
    /// so one buggy plugin can never bypass the validators that run after it.
    /// </summary>
    public RelayVerdict Evaluate(EventContext ctx)
    {
        IEventInterceptor[] chain;
        lock (_gate)
        {
            if (!_activeCache.TryGetValue(ctx.RoomName, out var cached) || cached.Gen != _generation)
            {
                var list = new List<IEventInterceptor>();
                // Deterministic order independent of discovery: by the plugin's Order (rewriters < validators
                // < reactors), then name as a stable tie-break. This is what guarantees the validators see a
                // mutator's rewrite and that a Drop always wins.
                var active = _entries
                    .Where(e => e.Enabled && e.Interceptors.Count > 0)
                    .OrderBy(e => e.Plugin.Order)
                    .ThenBy(e => e.Plugin.Name, StringComparer.OrdinalIgnoreCase);
                foreach (var e in active)
                {
                    if (!_instances.TryGetValue((e, ctx.RoomName), out var ints))
                        _instances[(e, ctx.RoomName)] = ints = e.Interceptors.Select(f => f()).ToArray();
                    list.AddRange(ints);
                }
                chain = list.ToArray();
                _activeCache[ctx.RoomName] = (_generation, chain);
            }
            else chain = cached.Chain;
        }

        // Run outside the lock (single-threaded relay; toggles are rare).
        var current = ctx.Event;
        List<EventData>? extras = null;
        foreach (var interceptor in chain)
        {
            RelayVerdict verdict;
            try { verdict = interceptor.Intercept(new EventContext(ctx.RoomName, ctx.SenderActor, current, ctx.Unreliable)); }
            catch (Exception ex)
            {
                Log.Exception("Plugins", $"interceptor {interceptor.GetType().Name} threw on event {ctx.Event.Code} — skipped", ex);
                continue;
            }

            switch (verdict.Action)
            {
                case RelayAction.Drop:
                    return RelayVerdict.Drop();   // a veto wins outright — discard any accumulated rewrite/originate
                case RelayAction.Rewrite:
                    if (verdict.Event is not null) current = verdict.Event;
                    break;
                case RelayAction.Originate:
                    if (verdict.Event is not null) current = verdict.Event;
                    if (verdict.Originated.Count > 0) (extras ??= new()).AddRange(verdict.Originated);
                    break;
                // Forward: no opinion — keep the working event and continue.
            }
        }

        if (extras is { Count: > 0 }) return RelayVerdict.Originate(current, extras);
        return ReferenceEquals(current, ctx.Event) ? RelayVerdict.Forward(ctx.Event) : RelayVerdict.Rewrite(current);
    }

    public IEnumerable<object> CommandProviders { get { lock (_gate) return _entries.SelectMany(e => e.Commands).ToArray(); } }

    /// <summary>Raised when a runtime <see cref="LoadFile"/> brings in plugins that contribute console
    /// commands, so the live console registry can register them. Built-in commands are registered directly
    /// at startup via <see cref="CommandProviders"/>; this covers only the hot-load path.</summary>
    public event Action<IReadOnlyList<object>>? CommandsRegistered;

    /// <summary>Raised when <see cref="Unload"/> removes a plugin that contributed console commands, so the
    /// live console registry can drop them — otherwise its delegates keep the (now unloaded) command
    /// provider, and its load context, alive.</summary>
    public event Action<IReadOnlyList<object>>? CommandsUnregistered;

    public void OnJoined(string roomName, int actor, RoomSession session) => Dispatch(roomName, actor, session, e => e.Joined);
    public void OnLeft(string roomName, int actor, RoomSession session) => Dispatch(roomName, actor, session, e => e.Left);

    private void Dispatch(string room, int actor, RoomSession session, Func<Entry, List<Action<RoomActorContext>>> select)
    {
        Entry[] snapshot;
        lock (_gate) snapshot = _entries.Where(e => e.Enabled).ToArray();
        var ctx = new RoomActorContext(room, actor, session);
        foreach (var e in snapshot)
            foreach (var handler in select(e))
                try { handler(ctx); }
                catch (Exception ex) { Log.Exception("Plugins", $"plugin '{e.Plugin.Name}' hook threw", ex); }
    }

    /// <summary>Enables/disables a plugin by name; false if unknown. Takes effect on the next event.</summary>
    public bool SetEnabled(string name, bool enabled)
    {
        lock (_gate)
        {
            var e = _entries.FirstOrDefault(x => string.Equals(x.Plugin.Name, name, StringComparison.OrdinalIgnoreCase));
            if (e is null) return false;
            e.Enabled = enabled;
            _generation++;
        }
        Log.Info("Plugins", $"plugin '{name}' {(enabled ? "enabled" : "disabled")}");
        return true;
    }

    /// <summary>
    /// Loads an external plugin DLL at runtime (into its own collectible context), configures it, and
    /// enables it. Returns the names loaded; empty on failure or if the path defines no plugins.
    /// </summary>
    public IReadOnlyList<string> LoadFile(string path, IServiceProvider services)
    {
        var names = new List<string>();
        foreach (var (plugin, ctx) in PluginLoader.LoadExternal(path))
        {
            Add(plugin, enabled: true, context: ctx);
            names.Add(plugin.Name);
        }
        if (names.Count == 0) return names;

        ConfigureAll(services);   // populates each new entry's Commands during Configure
        List<object> providers;
        lock (_gate)
            providers = _entries.Where(e => names.Contains(e.Plugin.Name, StringComparer.OrdinalIgnoreCase))
                                .SelectMany(e => e.Commands).ToList();
        if (providers.Count > 0) CommandsRegistered?.Invoke(providers);
        return names;
    }

    /// <summary>
    /// Unloads an external plugin: drops its contributions + per-room instances, then unloads its
    /// collectible context (the assembly is reclaimed once the GC runs). Built-in plugins can't be
    /// unloaded (disable them instead). Returns false if unknown or built-in.
    /// </summary>
    public bool Unload(string name)
    {
        AssemblyLoadContext? ctx;
        object[] removedCommands;
        lock (_gate)
        {
            var e = _entries.FirstOrDefault(x => string.Equals(x.Plugin.Name, name, StringComparison.OrdinalIgnoreCase));
            if (e is null || e.Context is null) return false;   // unknown or built-in
            ctx = e.Context;
            removedCommands = e.Commands.ToArray();
            _entries.Remove(e);
            foreach (var key in _instances.Keys.Where(k => k.Item1 == e).ToArray()) _instances.Remove(key);
            _activeCache.Clear();
            _generation++;
        }
        // Drop the plugin's console commands BEFORE unloading its context — the registry's delegates close
        // over the command provider (in the plugin's assembly), so leaving them would pin the context.
        if (removedCommands.Length > 0) CommandsUnregistered?.Invoke(removedCommands);
        try { ctx.Unload(); }
        catch (Exception ex) { Log.Exception("Plugins", $"failed to unload context for '{name}'", ex); }
        GC.Collect();
        GC.WaitForPendingFinalizers();
        Log.Info("Plugins", $"plugin '{name}' unloaded");
        return true;
    }

    public IReadOnlyList<(string Name, bool Enabled, bool External, string Description)> List()
    {
        lock (_gate)
            return _entries.Select(e => (e.Plugin.Name, e.Enabled, e.Context is not null, e.Plugin.Description)).ToList();
    }
}
