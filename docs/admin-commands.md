# Admin & debug commands

Commands are typed at the **server console** (stdin). They are dispatched through a
permission-gated `CommandRegistry`: each command declares a minimum `PlayerLevel`, and the local
console runs at the highest tier (**Console**) so it may run all of them. The same registry is the
seam for a future remote-admin endpoint, where an authenticated account's level would be passed
instead. Type `help` for the listing filtered to your tier.

Account levels (lowest→highest): **Player (0) · Mod (1) · Admin (2) · Console (3)**.

Commands that send packets to clients are **queued onto the game listener thread** (only that thread
may touch per-peer transport) and take effect on the next maintenance tick — they reply "queued".

## Moderation & accounts

| Command | Min level | What it does |
|---|---|---|
| `promote\|demote <steamId> <0-3>` | Admin | Set an account's permission tier |
| `ban <steamId>` / `unban <steamId>` | Mod | Toggle an account's ban |
| `list` | Mod | List all accounts (level, ban flag) |
| `whois <steamId>` | Mod | Account detail (level, ban, created/last-seen, playtime) |
| `kick <room> <actor> [reason]` | Mod | Remove an actor from a room and notify it (soft kick) |
| `code` | Console | Show the one-time bootstrap admin code |

## Message of the Day

| Command | Min level | What it does |
|---|---|---|
| `motd [text]` | Admin | Get (no arg) or set the global MOTD |
| `realmmotd <realm> <text>` | Admin | Set a realm's MOTD override |

## Live inspection / debug

| Command | Min level | What it does |
|---|---|---|
| `rooms` | Mod | List rooms with current player counts |
| `room <name>` | Mod | A room's actors + game properties |
| `getprop <room>` | Mod | Dump a room's game properties |
| `stats` | Mod | Room/player totals, uptime, log level |
| `loglevel <trace\|debug\|info\|warn\|error>` | Admin | Change the live log verbosity |

## Direct client manipulation (sends packets)

| Command | Min level | What it does |
|---|---|---|
| `say <room> <text>` | Mod | Broadcast a ServerMessage (event 199) to a room |
| `tell <room> <actor> <text>` | Mod | Send a ServerMessage to one actor |
| `setprop <room> <key> <value>` | Admin | Set a shared game property and broadcast the change (event 253) |
| `bot <realm>` | Admin | Spawn a playerbot into a realm |

`setprop` parses `<value>` as a bool (`true`/`false`), then an integer, otherwise a string — matching
the loosely-typed Photon property table.
