# Connection Flow

Captured live from a single solo launch via the BepInEx op-logger. Operation codes are
resolved against [`generated/photon_constants.csv`](generated/photon_constants.csv); see
[`generated/connection-timeline.sample.txt`](generated/connection-timeline.sample.txt) for
the sanitized raw sequence.

## Sequence

| # | Server | Direction | Op | Notable parameters (byte keys) |
|---|--------|-----------|----|-------------------------------|
| 1 | Name | send | `Authenticate` (230) | `220`=GameVersion `"Early Access v0.9.226_2.20.1"`, `224`=AppId (GUID), `210`=region `"us/*"` |
| 2 | Name | response | `Authenticate` (230) | `230`=Master address `…:5055`, `221`=encryption/auth token, `225`=UserId, `196`=cluster `"default"` |
| 3 | Master | send | `Authenticate` (230) | resumes session with token `221` |
| 4 | Master | event | `AppStats` (226) | server-pushed lobby stats |
| 5 | Master | response | `Authenticate` (230) | — |
| 6 | Master | send | `JoinLobby` (229) | — |
| 7 | Master | response | `JoinLobby` (229) | — |
| 8 | Master | event | `GameList` (230) | available rooms |
| 9 | Master | send | `CreateGame` (227) | requests a room; response carries the Game Server address |
| 10 | Master | response | `CreateGame` (227) | `230`=Game Server address `…:5056` |
| 11 | Game | send | `Authenticate` (230) | re-auth on the game server with token `221` |
| 12 | Game | response | `Authenticate` (230) | — |
| 13 | Game | send | `CreateGame` (227) | actually creates the room on the game server |
| 14 | Game | response | `CreateGame` (227) | `255`=room name `"Black Ice Public Game #18"`; `248`=room properties |
| 15 | Game | event | `Join` (255) | local actor joined; actor number assigned |
| 16+ | Game | send | `RaiseEvent` (253), `SetProperties` (252) | world initialization stream |

## Authentication parameters (op 230)

- `220` **AppVersion / GameVersion** — string. Acts as a compatibility gate: clients with a
  different value are matched into separate virtual app instances.
- `224` **ApplicationId** — the Photon Cloud AppId (GUID). *Credential; redacted in repo.*
- `210` **Region** — e.g. `"us/*"`.
- `221` **Secret / Token** — opaque base64 returned by the Name Server and replayed to the
  Master and Game servers. *Credential; redacted in repo.*
- `225` **UserId** — assigned identity. *Redacted in repo.*
- `230` **Address** — the next server to connect to (Master, then Game).

## Implications for reimplementation (Phase 1)

A reimplemented server must, at minimum:
1. Accept `Authenticate` (230) on a Name Server endpoint and return a Master `Address` (230)
   plus a token (221) it will later accept.
2. Accept the resumed `Authenticate` on the Master, handle `JoinLobby`/`GameList`/
   `CreateGame`, and return a Game Server `Address`.
3. Accept `Authenticate` + `CreateGame`/`JoinGame` on the Game Server, assign an actor
   number, and emit the `Join` (255) event.

Redirecting the client to that server requires changing the AppId/region/server endpoint —
either by patching the baked `PhotonServerSettings` asset or by DNS/hosts redirection of the
Name Server hostname (decision deferred to Phase 1).

## Verified against BlackIce.Server (Phase 1) — 2026-05-30

The real client reaches the in-room state through the reimplemented server. Observed,
end to end, with Photon Cloud never contacted:

```
NameServer:5058   CONNECT/VERIFYCONNECT, Init->InitResponse (F3 00 -> F3 01),
                  Diffie-Hellman key exchange, Authenticate -> Master address
MasterServer:5055 Authenticate (token), JoinLobby, GameList, CreateGame -> Game address
GameServer:5056   Authenticate (token), CreateGame -> actor + **Join (event 255)**
                  then the client streams RaiseEvent (op 253) gameplay (player spawn at a
                  Vector3 custom type, movement) — confirming it is in the room.
```

Wire details confirmed live (beyond the Phase 0 capture):
- A message = `[0xF3|0xFD][EgMessageType (|0x80 if AES-encrypted)][body]`.
- **Init** is a fixed 41-byte blob `F3 00 [protoVer:2][sdkId][clientVer:3][00][appID:32B]`; the
  server replies with the 2-byte **InitResponse** `F3 01` to unblock the client.
- Encryption is mandatory on every server (AuthMode=Auth): Oakley-768 DH then AES-256-CBC.
- Custom types use CustomTypeSlim (type byte 128..228, code = byte-128): e.g. PUN Vector3 = 86.
