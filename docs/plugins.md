# Server-side plugins

Everything custom on top of the vanilla Photon relay is a **plugin** — optional logic that can be
enabled or disabled. The vanilla server (connect flow, relay, accounts, realms, MOTD) runs with **zero
plugins enabled**; the custom features ship as built-in plugins:

| Plugin | What it adds |
|---|---|
| `anticheat` | The server-authority validators: event-flood, movement (speed/teleport/NaN), damage, hit/headshot rate, view-ownership. |
| `gamemodes` | Server-side game modes — team assignment on join + friendly-fire/PvE damage filtering (Team-vs-Team / Co-op). |

## Enabling / disabling

Three ways, in order of precedence at runtime:

1. **Config** — list a plugin name under `Server.Plugins.Disabled` to start it disabled (it's still loaded,
   so it can be toggled later). Example: `BLACKICE_Server__Plugins__Disabled__0=anticheat`, or in
   `blackice.server.json`:
   ```jsonc
   "Plugins": { "Directory": "server-plugins", "Disabled": ["anticheat"] }
   ```
2. **Command (runtime)** — from the console: `plugins` lists them with state; `plugin disable <name>` /
   `plugin enable <name>` toggles one live (no restart). Disabling instantly makes that plugin's relay
   interceptors no-op and stops its join/leave hooks.
3. **Removing the file** — for an *external* plugin (a DLL in the `server-plugins` directory), delete the
   DLL and restart. Built-in plugins can't be file-removed but can be disabled via #1/#2.

## How it works

- The host discovers plugins on startup — the built-in ones plus any `*.dll` in `Server.Plugins.Directory`
  — and calls each plugin's `Configure`, which registers **relay interceptors**, **console commands**, and
  **actor join/leave hooks**.
- The room relay sources its interceptors from the plugin manager; with no plugins it's a pure
  pass-through. Each plugin interceptor is wrapped in a gate that forwards unchanged while the plugin is
  disabled, so enable/disable is **live** — no room's chain is rebuilt.
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
        b.AddInterceptor(() => new MyInterceptor());          // per-room relay logic
         .AddCommands(new MyCommands())                        // [ConsoleCommand] methods
         .OnActorJoined(ctx => { /* react to a join */ })
         .OnActorLeft(ctx => { /* clean up */ });
    }
}
```

Built-in plugins are compiled into `BlackIce.Server.LoadBalancing`; external plugins are DLLs that
reference it and are dropped into the `server-plugins` directory. Plugins resolve what they need
(`AnticheatOptions`, `GameModeRegistry`, `RoomRegistry`, `RealmService`, …) from `PluginBuilder.Services`.

## Verified live

A soak run showed both plugins auto-loaded and enabled; with `anticheat` **enabled** the relay produced
authority flags, and with it **disabled via config** the same cheating bot traffic produced **zero**
authority flags (pure vanilla) while `gamemodes` continued to filter friendly fire independently.
