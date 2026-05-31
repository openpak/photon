using System.Reflection;
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
    /// Loads every *.dll in <paramref name="directory"/> and yields the plugins they define. Each DLL is
    /// loaded into the default context (so it shares this assembly's IServerPlugin type); a failing file is
    /// logged and skipped. "Removing a plugin" = deleting its DLL here and restarting.
    /// </summary>
    public static IEnumerable<IServerPlugin> LoadFrom(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) yield break;
        foreach (var path in Directory.EnumerateFiles(directory, "*.dll"))
        {
            Assembly asm;
            try { asm = Assembly.LoadFrom(path); }
            catch (Exception ex) { Log.Exception("Plugins", $"failed to load plugin assembly {path}", ex); continue; }
            foreach (var plugin in FromAssembly(asm)) yield return plugin;
        }
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
