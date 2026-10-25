# Protocol: photon

## Health and player counts

Every listener here speaks UDP, and a UDP port cannot be probed: nothing distinguishes a bound
socket from a dead one to anything outside the process. So the server also opens one TCP port
(`Server.HealthPort`, default **8080**, `0` disables it):

| Path | Answers |
| --- | --- |
| `/health` | `{"status":"ok"}` — it is running |
| `/players` | `{"players":N,"rooms":N,"detail":[…]}` — counted from live room membership, so it cannot drift from what is actually being relayed |

An idle server answers `players: 0` rather than failing: "nobody is playing" and "not running"
are exactly what this endpoint exists to tell apart. The website's status page reads it.

## Connecting a client (no mod required)

The server speaks the stock Photon protocol, so an **unmodified game client can connect and play** —
once it reaches our Name Server the server drives Name Server → Master → Game itself (via
`AdvertisedHost`). Two ways to point a client at us:

- **Built-in LAN mode (the mod-free direct connection).** Black Ice has a native LAN mode: enable it and
  enter the server's IP/port (`127.0.0.1` / `5055`), then connect — the completely unmodified client walks
  the stack into the match.
- **`OpenPak.Redirect` (optional client mod).** Use it when you want the game's **normal online startup /
  server browser** to surface **our custom realm list** instead of Photon Cloud. It's optional — the
  redirect's only job is the startup/server-list experience; gameplay works without it.
