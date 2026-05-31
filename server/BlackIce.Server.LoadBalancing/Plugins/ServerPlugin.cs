namespace BlackIce.Server.LoadBalancing.Plugins;

/// <summary>Context handed to a plugin's actor join/leave hook.</summary>
public sealed record RoomActorContext(string RoomName, int Actor, RoomSession Session);

/// <summary>
/// A server-side plugin: optional, enable/disable-able custom logic layered on the vanilla relay. The
/// vanilla server runs with zero plugins enabled; every custom behavior (anti-cheat, game modes, …) is a
/// plugin that contributes relay interceptors, console commands, and/or join/leave hooks during
/// <see cref="Configure"/>. Implementations need a public parameterless constructor (so they can be
/// discovered) and pull whatever services they need from <see cref="PluginBuilder.Services"/>.
/// </summary>
public interface IServerPlugin
{
    /// <summary>Stable, unique id used in config (enable/disable) and the `plugins` command.</summary>
    string Name { get; }
    string Description { get; }

    /// <summary>Called once at load to register the plugin's contributions.</summary>
    void Configure(PluginBuilder builder);
}

/// <summary>
/// Collects what a plugin contributes. Contributions are tagged with the plugin so they honor its
/// enabled state at runtime — an interceptor from a disabled plugin no-ops, a hook isn't dispatched.
/// </summary>
public sealed class PluginBuilder
{
    private readonly PluginManager.Entry _entry;

    /// <summary>The server's service provider, for plugins to resolve what they need (config, registries).</summary>
    public IServiceProvider Services { get; }

    internal PluginBuilder(IServiceProvider services, PluginManager.Entry entry)
    {
        Services = services;
        _entry = entry;
    }

    /// <summary>Adds a relay interceptor. A factory (not an instance) so each room gets its own for per-room state.</summary>
    public PluginBuilder AddInterceptor(Func<IEventInterceptor> factory) { _entry.Interceptors.Add(factory); return this; }

    /// <summary>Adds a console-command provider; its [ConsoleCommand] methods are registered with the console.</summary>
    public PluginBuilder AddCommands(object provider) { _entry.Commands.Add(provider); return this; }

    /// <summary>Runs when an actor joins a room (only while the plugin is enabled).</summary>
    public PluginBuilder OnActorJoined(Action<RoomActorContext> handler) { _entry.Joined.Add(handler); return this; }

    /// <summary>Runs when an actor leaves a room (only while the plugin is enabled).</summary>
    public PluginBuilder OnActorLeft(Action<RoomActorContext> handler) { _entry.Left.Add(handler); return this; }
}
