# Federation: rooms in other countries

Home runs all three roles. A region node runs the Game role only and reports its rooms back,
so the lobby at home still lists them and a joiner is sent wherever the room already is.

```
console (Sydney) ──▶ home Name ──▶ home Master ──CreateGame──▶ "203.0.113.9:22002" (au-sydney)
console (Lyon)   ──▶ home Name ──▶ home Master ──JoinGame────▶ "203.0.113.9:22002" (same room)
au-sydney Game   ──POST /internal/node-room──▶ home :8080     (who is in the room, lobby props)
```

## Placing a room

`MasterServerHandler.GameAddressFor`:

- a room with people in it is wherever it is (`NodeDirectory.AddressForRoom`), whatever the
  joiner's own country;
- a new room goes to the node for the creator's country, or home when there is none;
- without `Federation.RegionsUrl` nothing changes and home's address is the only one.

The creator's country is asked of the Switch adapter (`POST /internal/photon-identity`) when
the Name Server registers their identity, so it is already known by the time they create a
room. No answer means home.

## Config

```jsonc
"Federation": {
  "RegionsUrl": "https://openpak.org/api/v1/regions",   // home: where the node list comes from
  "NodeId": "",       // node: set = Game role only, report rooms
  "HomeUrl": ""       // node: home's HTTP, e.g. https://photon.openpak.org
}
```

Env: `OPENPAK_Federation__RegionsUrl`, `OPENPAK_Federation__NodeId`, `OPENPAK_Federation__HomeUrl`.
A node also needs home's `Server.Secret`: the tokens home minted must validate on the node.

## Room reports

Sent on every join and leave, the whole room each time (`RoomReport`): actor numbers and the
string-keyed game properties Fusion filters sessions by. Home applies it to the same registry the
Master's lobby reads (`HealthHostedService`, `POST /internal/node-room`, `X-Photon-Secret`). The
creator's identity rides on the first report so home can publish the room as that player's
friend presence; a node never talks to the adapter.

## Not done, on purpose

- Nearest-by-ping. First node for the country wins.
- Room migration. A node that dies takes its rooms with it.
- Per-node secrets. One shared `Server.Secret`; see `node/README.md` in the OpenPak tree.
