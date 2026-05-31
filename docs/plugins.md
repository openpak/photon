# Server-side plugins

Everything custom on top of the vanilla Photon relay is a **plugin** — optional logic that can be
enabled or disabled. The vanilla server (connect flow, relay, accounts, realms, MOTD) runs with **zero
plugins enabled**; the custom features ship as built-in plugins:

| Plugin | What it adds |
|---|---|
| `anticheat` | The server-authority validators: event-flood, movement (speed/teleport/NaN), damage, hit/headshot rate, view-ownership. |
| `gamemodes` | Server-side game modes — team assignment on join + friendly-fire/PvE damage filtering (Team-vs-Team / Co-op). |

A plugin is either **built-in** (compiled into `BlackIce.Server.LoadBalancing`, always present, can be
disabled but not removed) or **external** (a standalone DLL dropped into the `server-plugins` directory,
loaded into its own collectible load context so it can be hot-loaded and hot-unloaded at runtime).

## Enabling / disabling / loading / unloading

Four ways to control plugins, all without recompiling the server:

1. **Config** — list a plugin name under `Server.Plugins.Disabled` to start it disabled (it's still loaded,
   so it can be toggled later). Example: `BLACKICE_Server__Plugins__Disabled__0=anticheat`, or in
   `blackice.server.json`:
   ```jsonc
   "Plugins": { "Directory": "server-plugins", "Disabled": ["anticheat"] }
   ```
2. **Command (runtime)** — from the console:
   - `plugins` — lists every plugin with its state and an `(external)` marker for DLL-loaded ones.
   - `plugin enable <name>` / `plugin disable <name>` — toggles one live (no restart). Disabling instantly
     makes that plugin's relay interceptors no-op and stops its join/leave hooks.
   - `plugin load <file>` — loads an **external** plugin DLL at runtime (into its own collectible context),
     configures it, and enables it. The path may be absolute or relative to the host's working directory.
   - `plugin unload <name>` — unloads an **external** plugin: drops its interceptors / commands / hooks and
     per-room state, then unloads its load context so the assembly is reclaimed by the GC. Built-in plugins
     can't be unloaded (disable them instead).
3. **Dropping in a file** — place an external plugin's DLL in `Server.Plugins.Directory` (default
   `server-plugins`, resolved relative to the host) and it's discovered on the next startup, or load it
   immediately with `plugin load`.
4. **Removing the file** — delete an external plugin's DLL (after `plugin unload`, or before a restart).
   Built-in plugins can't be file-removed but can be disabled via #1/#2.

## How it works

- On startup the host registers the **built-in** plugins, then loads any `*.dll` in
  `Server.Plugins.Directory` — each external DLL into its **own collectible `AssemblyLoadContext`**. It then
  calls every plugin's `Configure`, which registers **relay interceptors**, **console commands**, and
  **actor join/leave hooks**.
- The room relay does **not** bake plugin interceptors into a per-room chain. Instead it defers to the
  plugin manager's `Evaluate` on every in-room event, which runs the *currently enabled* plugins'
  interceptors and returns the first non-`Forward` verdict (or `Forward` if none object). With no plugins
  it's a pure pass-through. Evaluating live — rather than caching interceptor instances inside each room —
  is what lets a plugin be enabled, disabled, loaded, or **unloaded** at runtime with immediate effect and
  no lingering references that would pin a collectible context in memory.
- Per-(plugin, room) interceptor instances are created lazily and kept while the plugin is loaded, so
  per-room state (movement history, rate windows) survives toggling *other* plugins; they're dropped only
  when the plugin itself is unloaded.
- The Game role fires `OnJoined`/`OnLeft` to the manager, which dispatches to enabled plugins (this is how
  `gamemodes` assigns teams on join without that logic living in the core handler).

## Writing a plugin

Implement `IServerPlugin` (public, parameterless constructor) and register contributions in `Configure`:

```csharp
public sealed class MyPlugin : IServerPlugin
{
    public string Name => "myplugin";
    public string Description => "…";

    public void Configure(PluginBuilder b)
    {
        var rooms = (RoomRegistry?)b.Services.GetService(typeof(RoomRegistry));
        b.AddInterceptor(() => new MyInterceptor())           // per-room relay logic
         .AddCommands(new MyCommands())                       // [ConsoleCommand] methods
         .OnActorJoined(ctx => { /* react to a join */ })
         .OnActorLeft(ctx => { /* clean up */ });
    }
}
```

Plugins resolve what they need (`AnticheatOptions`, `GameModeRegistry`, `RoomRegistry`, `RealmService`, …)
from `PluginBuilder.Services`.

### Built-in vs external

- **Built-in:** add the class to `BlackIce.Server.LoadBalancing` (the loader discovers every
  `IServerPlugin` in that assembly). It ships in the server and can only be disabled, not removed.
- **External:** a separate project that references `BlackIce.Server.LoadBalancing` and builds to a DLL you
  drop into `server-plugins`. Reference the contract assemblies with `<Private>false</Private>` so the
  plugin compiles against them but **does not** copy them into its output — the running server already
  provides them, and the collectible load context resolves shared assemblies from the host's default
  context so the `IServerPlugin` type identity matches. **Deploy only the plugin's own DLL.** Because each
  external plugin gets its own collectible context, it can be `plugin unload`ed and the file replaced
  without restarting the server.

### Worked example

`samples/SampleServerPlugin/` is a complete, standalone external plugin (`matchstats`) that exercises all
three contribution points harmlessly — an interceptor that counts events per room and always forwards,
join/leave hooks that track occupancy, and a `matchstats` console command. It is intentionally **not** part
of the server solution; build and deploy it with:

```bash
dotnet build samples/SampleServerPlugin/SampleServerPlugin.csproj
cp samples/SampleServerPlugin/bin/Debug/net8.0/SampleServerPlugin.dll \
   server/BlackIce.Server.Host/bin/Debug/net8.0/server-plugins/
```

## Verified live

End-to-end against a running host: the external `matchstats` plugin was discovered and enabled at startup
(listed with the `(external)` marker), its `matchstats` console command worked, `plugin unload matchstats`
removed it (gone from `plugins`), and `plugin load server-plugins/SampleServerPlugin.dll` brought it back at
runtime — proving the collectible-context hot load/unload path. Separately, a soak run showed `anticheat`
producing authority flags when enabled and **zero** flags when disabled via config (pure vanilla) while
`gamemodes` continued to filter friendly fire independently.
