namespace BlackIce.Server.Core;

/// <summary>
/// Server plugin loading settings. Built-in plugins (anti-cheat, game modes) and any external DLLs in
/// <see cref="Directory"/> are discovered on startup; names listed in <see cref="Disabled"/> start
/// disabled (still loaded, so they can be toggled at runtime with the `plugin enable` command).
/// </summary>
public sealed class PluginOptions
{
    /// <summary>Directory scanned for external plugin DLLs (relative to the executable if not rooted).</summary>
    public string Directory { get; set; } = "server-plugins";

    /// <summary>Plugin names that start disabled.</summary>
    public List<string> Disabled { get; set; } = new();
}
