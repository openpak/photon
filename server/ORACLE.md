# Photon Serialization Oracle API (Phase 1)

The real `Photon3Unity3D.dll` is referenced **test-only** in `BlackIce.Photon.Tests` to
produce reference bytes. Never shipped (GPL).

Namespace: `ExitGames.Client.Photon`. Protocol: **GpBinaryV18** (`Protocol18 : IProtocol`,
`ProtocolType => "GpBinaryV18"`). Construct with `new Protocol18()`.

## Entry points
- Scalars/collections (type byte + value): `byte[] IProtocol.Serialize(object obj)` — base
  convenience that calls `Serialize(StreamBuffer, obj, setType: true)`.
- Operation request: `void SerializeOperationRequest(StreamBuffer, byte operationCode, Dictionary<byte,object> parameters, bool setType)`.
- Event: `void SerializeEventData(StreamBuffer, EventData, bool setType)`.
- Response: `void SerializeOperationResponse(StreamBuffer, OperationResponse, bool setType)`.
- Deserialize value: `object IProtocol.Deserialize(byte[] serializedData)`.

## StreamBuffer
`new StreamBuffer(64)`; `byte[] ToArray()`; `int Length`; `byte ReadByte()`;
`StreamBuffer(byte[] buf)` to read.

## Open byte-level questions (reconcile against oracle in Tasks 3–5)
- v1.8 string length prefix width (int16 vs varint) — Task 3.
- `Dictionary<byte,object>` header layout — Task 4.
- Whether full wire messages carry a `0xF3` (243) magic + message-type byte prefix before the
  operation code (see `DeserializeMessage`) — Task 5. Our `MessageSerializer` must match
  whatever `SerializeOperationRequest` emits.

## Crypto
DH/encryption code lives in `ExitGames.Client.Photon.Encryption` — referenced for the Task 7
spike.

## Task 7 spike result — encryption IS required (Name Server path)
- `LoadBalancingClient` calls `EstablishEncryption()` whenever connecting to the **Name Server**
  (PhotonRealtime ~line 2017), regardless of AuthMode. So the full-Name-Server path requires
  implementing Photon's DH key exchange + encrypted operations.
- Algorithm is recoverable (managed): `DiffieHellmanCryptoProvider` (Photon3Unity3D ~11383)
  uses `OakleyGroups.OakleyPrime768` (RFC 2409 Group 1) + `BigInteger.ModPow`; the managed
  class can serve as the implementation reference AND test oracle.
- `InitEncryption` is sent below the `SendOperation` layer, which is why the Phase 0 op-log
  didn't show it.
- Alternatives to full DH impl: (a) implement it (matches stock-client goal); (b) bypass via
  the BepInEx mod (UseNameServer=false + AuthMode=AuthOnce to skip EstablishEncryption),
  trading away the "no client changes beyond redirect" property.

## Integration finding — Black Ice has native LAN mode
- ConnectCoroutine (Assembly-CSharp ~114175) reads PlayerPrefs: `"LAN"` ("LAN" vs "Cloud"),
  `"LAN IP"` (default 127.0.0.1), `"LAN Port"` (default 5055). LAN mode => UseNameServer=false,
  Server=LAN IP, Port=LAN Port, connects straight to MASTER (no Name Server).
- So the game already provides the realmlist redirect; the BepInEx redirect mod is optional.
- Connect is triggered by a MENU action (Play/Connect), NOT pure startup — autonomous headless
  integration can't click it. Registry: HKCU\Software\SuperDuperGameCompany\Black Ice.
- AuthMode defaults to Auth, so encryption (DH) is still established even on the LAN/Master path.

## Platform SP1 verified — 2026-05-30
Live: client mod reads SteamID from registry (HKCU\Software\Valve\Steam\ActiveProcess\ActiveUser
-> SteamID64 = 0x0110000100000000 + accountId) and sends it as the Photon UserId; the server
auto-creates a persistent Account (+Profile) at level Player keyed by that SteamID (SQLite
blackice.db). Bootstrap code printed on first start; console promote/ban/list working.
Note: Steamworks.SteamUser.GetSteamID() throws "not initialized" inside the BepInEx plugin
context — registry read is the reliable path.

## Platform SP2 verified — 2026-05-30
Live: 3 config realms (Co-op / PvP / Hardcore) seeded into SQLite on first run, advertised in
the lobby GameList (event 230, param 222) with per-realm props, persisted across restarts. Client
connected Name→Master→Game, requested the lobby (op 229), received the realm list without
serialization error, and reached the Game server. Joining applies the realm ruleset; unknown
realm → return -4, wrong password → -5.
