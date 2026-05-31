# Server-side plugins

Everything custom on top of the vanilla Photon relay is a **plugin** — optional logic that can be
enabled or disabled. The vanilla server (connect flow, relay, accounts, realms, MOTD) runs with **zero
plugins enabled**; the custom features ship as built-in plugins:

| Plugin | What it adds |
|---|---|
| `anticheat` | The server-authority validators: event-flood, movement (speed/teleport/NaN), damage, hit/headshot rate, view-ownership. |
| `gamemodes` | Server-side game modes — team assignment on join + friendly-fire/PvE damage filtering (Team-vs-Team / Co-op). |
| `mutators` | Global gameplay mutators by rewriting damage RPCs: a damage multiplier and force-crit. Inert (×1, no crits) until tuned. |
| `spawnguard` | Spawn protection — drops incoming damage to a player for a grace window after they join. Off until armed. |
| `thorns` | Damage reflection — returns a % of player-dealt damage to the attacker via a server-originated `TakeDamage` RPC. Off until set. |
| `killfeed` | Server-authoritative killstreak feed — models HP from relayed damage and announces kills/streaks in vanilla chat. Off by default. |
| `arena` | Team-deathmatch match for Team-vs-Team realms — scores `killfeed` kills per team, first team to the score cap wins, then resets to replay. Off by default. |

The last four are **server-only gameplay customizations** — they change the rules using nothing but the
relay (rewriting, dropping, or originating events), so the vanilla client just renders the result with no
mod. They ship **inert/off**, so the server stays vanilla until an admin tunes them live (see *Gameplay
mutator commands* below). They demonstrate the three relay levers a plugin has — `mutators` **rewrites**,
`spawnguard` **drops**, `thorns`/`killfeed` **originate** — and compose safely with the validators above.

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

### Gameplay mutator commands

The server-only gameplay plugins ship inert and are tuned live from the console (Admin). Each command with
no argument prints the current state:

| Command | Effect |
|---|---|
| `mutators` · `mutator damage <mult>` · `mutator crits <on\|off>` · `mutator reset` | Global damage multiplier and force-crit, applied by rewriting damage RPCs. `mutator reset` returns to vanilla (×1, no crits). |
| `spawnguard [seconds <n> \| off]` | Arms spawn protection: incoming damage to a player is dropped for `<n>` seconds after they join. `0`/`off` disables. |
| `thorns [percent <0-1000> \| off]` | Reflects `<n>`% of player-dealt damage back at the attacker (a server-originated `TakeDamage` on the attacker's avatar view). |
| `killfeed [on\|off\|hp <n>]` | Toggles the killstreak feed and sets the assumed max-HP the kill model uses (it sums relayed damage per victim and credits the attacker that crosses it). |
| `arena [on\|off\|scorecap <n>\|reset]` | Toggles the team match, sets the winning score, or force-resets all active matches. Scores only in Team-vs-Team realms and needs `killfeed` on (its kill source). |

Because these are interceptor/announcement rules layered on the relay, they take effect immediately and on
every room, with no client changes and no restart.

#### Arena match loop (TvT)

With a Team-vs-Team realm, `killfeed` on, and `arena on`, the continuous relay becomes a replayable match:
each cross-team kill `killfeed` models is published on an in-process `KillBus`; `arena` credits the killer's
team a point and broadcasts the running score; the first team to `arena scorecap <n>` wins (the other side
loses); and — when `Server.Arena.ResetOnWin` is set (default) — the match resets (team scores cleared, kill
tallies wiped via the bus, "new round" announced) so it loops like an arcade arena. Configure defaults under
`Server.Arena` (`Enabled`, `ScoreCap`, `ResetOnWin`); tune live with the `arena`/`killfeed` commands. Soak
bots, which hold a team but no peer membership, count as combatants in team modes, so a bot soak in a TvT
realm exercises the whole loop. (Score/round announcements use the vanilla chat RPC and are always written to
the server log under `[Arena]`.)

## How it works

- On startup the host registers the **built-in** plugins, then loads any `*.dll` in
  `Server.Plugins.Directory` — each external DLL into its **own collectible `AssemblyLoadContext`**. It then
  calls every plugin's `Configure`, which registers **relay interceptors**, **console commands**, and
  **actor join/leave hooks**.
- The room relay does **not** bake plugin interceptors into a per-room chain. Instead it defers to the
  plugin manager's `Evaluate` on every in-room event. Evaluating live — rather than caching interceptor
  instances inside each room — is what lets a plugin be enabled, disabled, loaded, or **unloaded** at
  runtime with immediate effect and no lingering references that would pin a collectible context in memory.
- `Evaluate` **composes** the active plugins' verdicts rather than stopping at the first opinion: a
  `Rewrite` updates the working event and the chain *continues*, so a later interceptor (e.g. the anti-cheat
  / game-mode validators) sees the rewritten event and can still veto it; an `Originate` additionally
  accumulates server-authored extra events; and a single `Drop` short-circuits and discards everything (a
  validator veto always wins). A throwing interceptor is caught and skipped — one buggy plugin can never
  make the relay skip the validators that run after it. This is why a `mutators` damage rewrite still passes
  through `anticheat`, and why `thorns`/`killfeed` reflection/announcements vanish if the hit that triggered
  them is dropped by friendly-fire or spawn-protection rules.
- Chain order is **deterministic**, set by each plugin's `Order` (low to high), not by discovery order: by
  convention rewriters run first (`mutators`, `Order = -100`), then the validators (anti-cheat / game modes,
  `Order = 0`), then the observe-and-originate reactors (`thorns` / `killfeed`, `Order = 100`). This is what
  guarantees the validators see a rewrite and that a Drop discards a reaction. External plugins keep the
  default (`0`) unless they need a specific slot.
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
