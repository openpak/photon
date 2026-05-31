# MOTD (Message of the Day) — Design

- **Date:** 2026-05-30
- **Status:** Approved (brainstorming) → ready for implementation planning
- **Sub-project of:** BlackIce.Server. First of the next batch (MOTD → remote hosting + Steam
  registration → SP3 ticket validation → Phase 2 relay).

## Context & goal

The server needs a way to show players a server-controlled message (welcome text, rules,
event/downtime notices) inside the game. Black Ice ships a native chat system (`ChatGUI`,
`ChatMessage`) we can render into, so the MOTD should look like a native system line, not a
bolted-on overlay.

This feature also intentionally bootstraps the **inbound chat-command channel** (player → server
`/command` handling) that SP3's in-game admin will build on, via the optional `/motd` command.

## Scope

**In scope**
- A per-realm MOTD with a global fallback, stored in the DB and editable live from the console.
- Display the MOTD as a native `Info`-type chat line when a player joins.
- A `/motd` chat command that re-shows the (freshly resolved) MOTD to the requesting player.
- The minimal, reusable server-side machinery the `/motd` command requires: decode inbound chat
  RPCs, recognize/suppress `/`-prefixed commands, and a server→client `ServerMessage` event.

**Out of scope (YAGNI)**
- Periodic re-broadcast / timers.
- In-game admin editing of the MOTD (console-only for now; in-game admin waits on the SP3
  security gate — networked admin must not be built on the spoofable client SteamID).
- Rich formatting beyond what the chat line already supports (plain/multiline text; sender color).
- A general command framework beyond what `/motd` needs (kept deliberately minimal, extensible).

## Decisions (from brainstorming)

1. **Storage:** DB-backed, per-realm override + global default. (`Realm.Motd ?? ServerState.Motd`.)
2. **When shown:** on join, plus on-demand via `/motd`.
3. **Delivery + display:** on-join MOTD rides a room **custom property** (`"motd"`); the client mod
   renders it through the game's native `ChatGUI` as `ChatMessage.Type.Info`.
4. **`/motd`:** handled **server-side** (decode chat event 200, suppress relay of `/`-commands,
   respond via a custom `ServerMessage` event) — chosen over a trivial client-only re-render
   specifically to build the reusable SP3 command/response channel.

## Architecture & units

Each unit has one clear purpose, a defined interface, and is independently testable.

### Data model (`BlackIce.Server.Data`)
- Add `string? Motd` to `ServerState` (global; seeded from `blackice.server.json`).
- Add `string? Motd` to `Realm` (per-realm override; `null` ⇒ inherit global).
- **Schema migration note:** the data layer uses `EnsureCreated`, which does **not** add columns
  to an existing `blackice.db`. For this sub-project, delete the dev DB so it's recreated with the
  new columns. (Adopting EF migrations via the `/ef-migration` skill is deferred until MySQL ships.)

### `MotdService` (server)
- **Purpose:** resolve the effective MOTD for a realm.
- **Interface:** `string? Resolve(Realm realm)` → `realm.Motd ?? serverState.Motd` (whitespace-only
  treated as null/no-MOTD).
- **Depends on:** the EF data layer (ServerState + the realm record).

### On-join property injection (`BlackIce.Server.LoadBalancing`, Game server)
- **Purpose:** publish the MOTD to the joining client.
- **Behavior:** where the Game server already stamps room custom properties on join (the SP2
  realm-ruleset path), add `"motd"` = `MotdService.Resolve(realm)` when non-null.
- **Depends on:** `MotdService`, the existing room-property/Join path.

### `BlackIce.Motd` plugin (client, new BepInEx plugin, `netstandard2.0`)
- **Purpose:** render server messages natively in chat.
- **Behavior:**
  - On joining a room, read `PhotonNetwork.CurrentRoom.CustomProperties["motd"]`; if present,
    call the game's chat to compose a `ChatMessage.Type.Info` line from a configurable sender
    (default `"ouroborOS"`, matching the game's own system lines). Multiline text supported.
  - Register an `OnEvent` handler for the `ServerMessage` event code; render its text the same way.
- **Depends on:** the game's `ChatGUI`/`ComposeMessage` (confirmed: `public static ChatGUI`
  instance; `ChatMessage(string sender, string message, Color color)` ctor; `ComposeMessage`),
  Photon PUN/Realtime, BepInEx/Harmony. Separate from `BlackIce.Redirect` (different concern).

### Inbound chat-command handler (`BlackIce.Server.LoadBalancing`)
- **Purpose:** turn `/`-prefixed chat into server commands instead of relayed chat.
- **Behavior:** when a chat RaiseEvent (PUN event code 200) arrives, decode it enough to extract
  the RPC method (`ReceiveChatMessage`) and the `string text` argument. If `text` begins with `/`:
  - do **not** relay the event to other players (suppress);
  - dispatch to a command handler. For `/motd`: resolve the MOTD for the player's realm and send it
    back via a `ServerMessage` event to that actor.
  - Unknown `/commands`: reply with a short "unknown command" `ServerMessage` (no relay).
- **Depends on:** the GpBinary codec (Hashtable/object[] decode), the event router, `MotdService`,
  the `ServerMessage` sender.
- **Risk / verification item:** PUN event-200 payload is a Hashtable keyed by byte codes
  (viewID, method-name *or* hashed method id, parameter `object[]`). Whether the method arrives as
  the string `"ReceiveChatMessage"` or a hashed shortcut must be pinned by an interop test against
  the real Photon DLL. This is shared Phase-2/SP3 work.

### `ServerMessage` event sender (`BlackIce.Server.LoadBalancing`)
- **Purpose:** a server→client channel for transient server text, decoupled from PUN RPCs (no
  viewID/actor-impersonation needed).
- **Interface:** `SendServerMessage(actor, text)` → RaiseEvent of our chosen `ServerMessage` code,
  targeted at one actor (or broadcast), payload = the text. Reusable for all SP3 command responses.

### Console commands (`ConsoleCommandProcessor`, SP1)
- `motd <text>` — set the global MOTD (persist to `ServerState`).
- `realmmotd <realm> <text>` — set a realm override (persist to `Realm`).
- `motd` / `realmmotd <realm>` with no text — show the current value.
- Effective immediately for subsequent joins and `/motd` responses.

## Data flow

**On join:** client joins → Game server resolves realm → `MotdService.Resolve` → set room prop
`"motd"` → client receives room props → `BlackIce.Motd` renders an Info chat line.

**`/motd`:** player types `/motd` → game sends `ReceiveChatMessage` RPC (event 200) → server decodes,
sees leading `/`, suppresses relay → command handler resolves MOTD → `SendServerMessage(player, motd)`
→ `BlackIce.Motd` `OnEvent` renders the Info line. Other players see nothing.

## Error handling & edge cases
- No MOTD set (both null/empty): no room prop, nothing rendered; `/motd` replies "No MOTD set."
- MOTD changed mid-session via console: new joins and `/motd` reflect it immediately; already-shown
  lines are not retroactively changed.
- Chat event that isn't `ReceiveChatMessage`, or a non-`/` chat line: relayed normally (unchanged).
- `ChatGUI` not yet initialized when the join fires: the plugin retries/defers until the chat system
  exists (poll or hook the chat-ready point) before composing.
- Malformed/oversized event-200 payload: decode defensively (bounded reads); on decode failure,
  fall back to relaying unchanged rather than dropping the player's chat.

## Security notes
- The MOTD is admin-authored (console) and read-only to players — low risk.
- `/motd` is an **unprivileged** command (anyone may request the public MOTD); it grants nothing.
- **Framework constraint for SP3:** the command channel must NOT grant privileged actions based on
  the client-asserted SteamID/UserId (spoofable — see SECURITY.md). Privileged commands wait on
  Steam ticket server-side validation. `/motd` stays unprivileged so it ships now safely.

## Testing strategy (xUnit, project TDD norm)
- `MotdService.Resolve`: realm override wins; global fallback; null/whitespace ⇒ none.
- Command parsing: `/motd` recognized; `/`-prefixed commands suppressed from relay; non-command chat
  relayed; unknown command replies.
- `ServerMessage` encode round-trips.
- Event-200 decode verified against the real `Photon3Unity3D.dll` oracle (extract method + text arg).
- **Live verification:** launch game → join → MOTD Info line appears; `/motd` → reappears (and other
  clients don't see the command); console `motd <text>` → rejoin → updated text shows.

## Incremental delivery
- **Increment A — MOTD on join:** data model + `MotdService` + room-prop injection + `BlackIce.Motd`
  display + console commands. No event-200 decode needed. Independently shippable and live-verifiable.
- **Increment B — `/motd` command:** inbound chat-command handler (event-200 decode + suppression) +
  `ServerMessage` event + plugin `OnEvent` rendering. Builds the reusable SP3 command channel.

## Open risks
1. **PUN event-200 method serialization** (string vs hashed shortcut) — pin via oracle interop test
   in Increment B; this is the main unknown and is shared with Phase 2/SP3.
2. **`EnsureCreated` column add** — handled by recreating the dev DB (documented above).
