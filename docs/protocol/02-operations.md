# Operations, Events, Parameters, Errors

The protocol vocabulary, extracted verbatim from `PhotonRealtime.dll` into
[`generated/photon_constants.csv`](generated/photon_constants.csv) (132 constants across 4
groups). The full ParameterCode (69) and ErrorCode (29) tables live in the CSV; the two
small, central groups are reproduced here. The **Live** column marks codes observed in the
Phase 0 op-log.

## OperationCode (client → server requests)

| Code | Name | Live | Notes |
|---|---|---|---|
| 217 | GetGameList | | explicit room list |
| 218 | ServerSettings | | |
| 219 | WebRpc | | server-side web hooks |
| 220 | GetRegions | | region ping list |
| 221 | GetLobbyStats | | |
| 222 | FindFriends | | |
| 225 | JoinRandomGame | | matchmaking |
| 226 | JoinGame | | join existing room |
| 227 | CreateGame | ✓ | create room (Master returns Game Server addr; Game creates room) |
| 228 | LeaveLobby | | |
| 229 | JoinLobby | ✓ | enter matchmaking lobby |
| 230 | Authenticate | ✓ | used on all three servers |
| 231 | AuthenticateOnce | | binary-token variant |
| 248 | ChangeGroups | | interest groups |
| 250 | ExchangeKeysForEncryption | | Diffie-Hellman key setup |
| 251 | GetProperties | | |
| 252 | SetProperties | ✓ | room/actor property updates (heavy use) |
| 253 | RaiseEvent | ✓ | the game-event carrier (72× in solo session) |
| 254 | Leave | | leave room |
| 255 | Join | ✓ | join room on game server |

## EventCode (server → client / broadcast)

| Code | Name | Live | Notes |
|---|---|---|---|
| 210 | AzureNodeInfo | | |
| 223 | AuthEvent | | |
| 224 | LobbyStats | | |
| 226 | AppStats | ✓ | lobby population stats |
| 227 | Match | | |
| 228 | QueueState | | |
| 229 | GameListUpdate | | incremental room list |
| 230 | GameList | ✓ | full room list |
| 250 | CacheSliceChanged | | |
| 251 | ErrorInfo | | server-pushed error |
| 253 | PropertiesChanged / SetProperties | ✓ | property change broadcast |
| 254 | Leave | | actor left room |
| 255 | Join | ✓ | actor joined room |

> Codes ≥ 200 are Photon LoadBalancing reserved. **Game-specific events** raised via
> `RaiseEvent` (op 253) use *small* event codes (typically 1–199) carried in parameter `244`
> (`ParameterCode.Code`); those are catalogued from gameplay capture and the RPC layer — see
> [03-rpc-catalog.md](03-rpc-catalog.md).

## Parameter & Error codes

See [`generated/photon_constants.csv`](generated/photon_constants.csv):
- **ParameterCode** (69 entries) — byte keys inside operation/event parameter dictionaries.
  Central ones observed: `220` AppVersion, `224` ApplicationId, `210` Region, `221`
  Secret/Token, `225` UserId, `230` Address, `255` RoomName/ActorNr, `248` GameProperties,
  `249` PlayerProperties, `244` Code (event code), `245` Data (event payload).
- **ErrorCode** (29 entries) — operation `returnCode` values; `0` = OK (all observed
  responses returned 0).
