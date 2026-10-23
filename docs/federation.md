# Federation: rooms in other regions

Home runs all three roles. A region node runs the Game role only and reports its rooms back,
so the lobby at home still lists them and a joiner is sent wherever the room already is. There
is one Master, at home: matchmaking stays global.

```
console (São Paulo) ─▶ home Name: GetRegions ─▶ ["eu" home, "sa" br-gru-1, ...]  (client pings each)
console (São Paulo) ─▶ home Name: Authenticate, param 210 "sa/*" ─▶ token carries "sa", Master = home
console (São Paulo) ─▶ home Master ──CreateGame──▶ "198.51.100.10:5056" (br-gru-1)
console (Lyon)      ─▶ home Master ──JoinGame────▶ "198.51.100.10:5056" (same room)
br-gru-1 Game       ──POST /internal/node-room──▶ home :8080  (who is in the room, lobby props)
```

## Regions the client sees

`GetRegions` (op 220) lists `eu` at the home Master first, always (Outbound's "Region: Automatic"
needs it), then one **real Photon token** per region with a node in rotation (`sa`, `us`, `usw`,
`jp`, `asia`, `au`, `hk`, `in`, `uae`, `za`, ...; the account core's region registry). Each
address is the node's ping responder (`photon_ping` role) or its Game host. With "Automatic" the
client pings every listed host on UDP 5055 (Photon's 13-byte ping, echoed) and authenticates with
the winner; with a fixed region it sends that. Every listener answers the ping; a node whose Game
role is not on 5055 runs a bare responder on `Federation.PingPort` (default 5055).

The Name Server reads param 210 (`"sa/*"`, `"jp"`, anything: never rejected), keeps the bare
token in the auth token, and always hands back home's Master address.

## Placing a room

`MasterServerHandler.GameAddressFor` and `NodeDirectory.Place`:

- a room with people in it is wherever it is (`NodeDirectory.AddressForRoom`), whatever the
  joiner's own region or country;
- a new room goes to the region the creator chose in game (the token's region), whatever their
  country; with no choice, to their account country's region; a region with no node walks its
  fallback chain (`sa-east -> us-east`, ...); everything else is home. An unknown token is home;
- **test nodes** are routed only to accounts on the core's test allow-list, and listed in
  `GetRegions` only to an address that authenticated as such an account in the last day;
- without `Federation.RoutingUrl`, or with no node in rotation, nothing changes: `eu` and home's
  addresses are the only ones.

The rule is `Routing.cs`, the C# twin of the account core's `pkg/routing`; both run the same
fixture (`server/OpenPak.Server.Tests/RoutingCases.json`). The creator's country, and whether their
account is a tester, are asked when the Name Server registers their identity (Switch adapter
`POST /internal/photon-identity`, then the core's `/internal/v1/routing/account` with the BAAS
user id), so they are known by the time they create a room. No answer means no country.

## Tokens

Auth tokens are Ed25519-signed (`TokenKeys`). Home signs with `Server.TokenSigningKey` (base64
32-byte seed) or, unset, a key derived from `Server.Secret`; it logs the public key at start. A
node is given only that public key (`Server.TokenPublicKey`): it checks every token home minted and
cannot mint one. A node never holds home's secret.

## Config

```jsonc
"Server": {
  "TokenSigningKey": "",   // home, optional: base64 Ed25519 seed (else derived from Secret)
  "TokenPublicKey": ""     // node: home's public key, from home's start-up log
},
"Federation": {
  "RoutingUrl": "",        // home: the account core's HTTP base, e.g. http://openpak-account:8000
  "RoutingKey": "",        // home: the core's ACCOUNT_ROUTING_KEY
  "NodeKeys": {},          // home: room-report key per node id
  "NodeId": "",            // node: set = Game role only, report rooms
  "HomeUrl": "",           // node: home's HTTP, e.g. https://photon.openpak.org
  "ReportKey": "",         // node: its entry in home's NodeKeys
  "PingPort": 5055         // node: region ping responder (0 = off)
}
```

Env: `OPENPAK_Federation__RoutingUrl` (or `OPENPAK_ROUTING_URL`), `OPENPAK_Federation__RoutingKey`
(or `OPENPAK_ROUTING_KEY`), `OPENPAK_Federation__NodeKeys__<node>`, `OPENPAK_Federation__NodeId`,
`OPENPAK_Federation__HomeUrl`, `OPENPAK_Federation__ReportKey`, `OPENPAK_Server__TokenPublicKey`.
`Federation.RegionsUrl` (the public `/api/v1/regions` list) is retired and ignored.

## Room reports

Sent on every join and leave, the whole room each time (`RoomReport`): actor numbers and the
string-keyed game properties Fusion filters sessions by. Home applies it to the same registry the
Master's lobby reads (`HealthHostedService`, `POST /internal/node-room`, header `X-Node-Key`):
only with the reporting node's own key, so one node cannot speak for another. The creator's
identity rides on the first report so home can publish the room as that player's friend
presence; a node never talks to the adapter.

## Not done, on purpose

- Room migration. A node that dies takes its rooms with it.
- Token expiry and audience binding to one node. Tokens carry no lifetime yet; a Master that
  re-mints on return from a long room would need it first.
- Country on the public Steam path: Steam players have no OpenPak account, so only their
  explicit region places their rooms.
