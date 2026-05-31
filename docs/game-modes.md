# Server-side custom game modes

Each realm has a server-enforced **`Mode`** that changes the rules of play **without modifying the
client**. It works by leaning on two things the stock client already does: it renders the standard
**`Team`** player property, and it routes damage as RPCs through the server relay. The server assigns
teams and drops the damage the mode forbids, so a free-for-all room becomes Team-vs-Team or Co-op with
no client mod.

## Modes

| `Realm.Mode` | Teams | Player-vs-player damage | Use |
|---|---|---|---|
| `FreeForAll` (default) | none | unrestricted (client-side `Pvp` flag still applies) | the stock behavior |
| `TeamVsTeam` | 2, balanced | **same-team dropped** (friendly fire off); cross-team allowed | team deathmatch |
| `Coop` | tracked as one side | **all player-vs-player dropped** (PvE only) | co-op survival |

Set it on a realm (config seed, the DB, or — read-only today — the `realm <name>` console command):

```jsonc
{ "Name": "Black Ice — Team Battle", "DisplayName": "Team Battle", "Pvp": true, "MaxPlayers": 8, "Mode": "TeamVsTeam" }
```

The shipped seed realms include a `Coop` Co-op realm and a `TeamVsTeam` "Team Battle" realm.

## How it works

1. **Team assignment (on join).** When an actor joins a team-mode realm, `GameServerHandler` asks
   `GameModeRegistry` for a balanced team, stores it as the actor's **`Team`** player property, and
   broadcasts a `PropertiesChanged` (event 253) — exactly the message the client uses to show teams, so
   players see their teammates with no client change.
2. **Damage filtering (on the relay).** `TeamDamageInterceptor` runs in the room's interceptor chain.
   For a damage RPC it finds the target player from the RPC's viewID block (`viewID / 1000`), asks
   `GameModeRegistry.BlocksDamage(room, attacker, target)`, and **drops** the event when the mode forbids
   it (same-team in TvT, any player target in Co-op). A dropped event never reaches the victim, so the
   damage is never applied. Damage to enemies/world objects is never blocked.

Both run on the single Game-listener thread; team state is freed when an actor leaves.

## Verified live

A soak run (`Bots.AutoSpawnPerRealm=4`, `EmitGameActions=true`) showed bots auto-assigned to balanced
teams and the relay dropping the disallowed damage: in the `TeamVsTeam` realm only **same-team** hits
were dropped (cross-team passed); in the `Coop` realm **all** player-vs-player hits were dropped.

## Notes / extending

- Damage detection trusts the **server's** team map, not the client-sent DamagePacket faction (which is
  untrusted) — see `docs/protocol/live-verification.md` for the packet-field caveats.
- `GameMode` is an enum + a small registry; adding a mode (e.g. teams-of-N, objective rules) is a new
  enum value plus its policy in `GameModeRegistry.BlocksDamage` / team assignment.
- Ownership note: the relay's view-ownership check applies to **position/instantiation** only, not RPCs —
  a damage RPC legitimately targets another player's view, so it is not an ownership violation.
