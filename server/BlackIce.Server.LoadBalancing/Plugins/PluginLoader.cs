using System.Reflection;
using System.Runtime.Loader;
using BlackIce.Server.Core;

namespace BlackIce.Server.LoadBalancing.Plugins;

/// <summary>Discovers <see cref="IServerPlugin"/> implementations — the built-in ones compiled into the
/// server, and any in external DLLs dropped into the server-plugins directory.</summary>
public static class PluginLoader
{
    /// <summary>Instantiates every concrete, parameterless <see cref="IServerPlugin"/> in the given assemblies.</summary>
    public static IEnumerable<IServerPlugin> Discover(params Assembly[] assemblies)
    {
        foreach (var asm in assemblies)
            foreach (var plugin in FromAssembly(asm))
                yield return plugin;
    }

    /// <summary>Built-in plugins (those shipped in this assembly).</summary>
    public static IEnumerable<IServerPlugin> BuiltIn() => FromAssembly(typeof(PluginLoader).Assembly);

    /// <summary>
    /// Loads one external plugin DLL into its own <b>collectible</b> load context (so it can be hot-unloaded)
    /// and yields the plugins it defines, paired with that context. Shared assemblies (this server's, the
    /// framework) resolve against the default context so the plugin's IServerPlugin is the host's type. A
    /// failing file is logged and skipped; a DLL defining no plugins has its context unloaded immediately.
    /// </summary>
    public static IEnumerable<(IServerPlugin Plugin, AssemblyLoadContext Context)> LoadExternal(string path)
    {
        AssemblyLoadContext ctx;
        IServerPlugin[] found;
        try
        {
            ctx = new PluginLoadContext(path);
            var asm = ctx.LoadFromAssemblyPath(Path.GetFullPath(path));
            found = FromAssembly(asm).ToArray();
        }
        catch (Exception ex)
        {
            Log.Exception("Plugins", $"failed to load plugin assembly {path}", ex);
            yield break;
        }

        if (found.Length == 0) { ctx.Unload(); yield break; }
        foreach (var plugin in found) yield return (plugin, ctx);
    }

    private static IEnumerable<IServerPlugin> FromAssembly(Assembly asm)
    {
        Type[] types;
        try { types = asm.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t is not null).ToArray()!; }

        foreach (var type in types)
        {
            if (type is null || type.IsAbstract || !typeof(IServerPlugin).IsAssignableFrom(type)) continue;
            if (type.GetConstructor(Type.EmptyTypes) is null) continue;
            IServerPlugin? plugin = null;
            try { plugin = (IServerPlugin)Activator.CreateInstance(type)!; }
            catch (Exception ex) { Log.Exception("Plugins", $"failed to instantiate plugin {type.FullName}", ex); }
            if (plugin is not null) yield return plugin;
        }
    }
}

/// <summary>
/// Collectible load context for one external plugin DLL. Assemblies the host already provides (this
/// server, the framework) resolve to null here so they fall back to the default context — keeping a
/// single shared <see cref="IServerPlugin"/> type identity; only the plugin's own private dependencies
/// load into this context. Being collectible lets <see cref="PluginManager.Unload"/> reclaim it.
/// </summary>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    public PluginLoadContext(string pluginPath) : base(name: $"plugin:{Path.GetFileNameWithoutExtension(pluginPath)}", isCollectible: true)
        => _resolver = new AssemblyDependencyResolver(Path.GetFullPath(pluginPath));

    protected override Assembly? Load(AssemblyName name)
    {
        // Share anything already loaded by the host (its assemblies + the framework) so contract types match.
        if (Default.Assemblies.Any(a => a.GetName().Name == name.Name)) return null;
        var path = _resolver.ResolveAssemblyToPath(name);
        return path is null ? null : LoadFromAssemblyPath(path);
    }
}
