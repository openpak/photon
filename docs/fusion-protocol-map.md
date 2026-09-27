# Fusion's session protocol, mapped from the SDK (2026-09-27)

Decompiled from `Fusion.Sockets.dll` and `Fusion.Runtime.dll` with `ilspycmd`
(`DOTNET_ROLL_FORWARD=LatestMajor`; the tool targets .NET 6 and this box has 8/9/10). Working copies:
`~/REPOS/Openpak/scratch/fusion/src/Fusion.Sockets.decompiled.cs` (8,851 lines — the wire protocol)
and `Fusion.Runtime.decompiled.cs` (55,399 — the client state machine). These are the SDK's own
types, so this file is a transcription, not a reconstruction.

## Transport

```
EventCodes:  PROTOCOL = 100,  DATA = 101,  DUMMY = 102
PhotonConstants: SERVER_ACTOR_ID = 0,  FUSION_DATA_EVENT_PARAMETER_CODE = 245
```

Everything below rides Photon event **100**; event **101** is Fusion's own `NetPeerGroup` transport.

## Message registry (`ProtocolSerializer`)

| id | message | body, in serialization order |
|---|---|---|
| 1 | `Join` | `byte Type`, `byte GameMode`, `byte PeerMode`, `uint JoinRequests`, then ≥6 `byte[] UniqueId`, ≥7 `int PlayerRef`, ≥9 `byte[] EncryptionKey`, `byte[] EncryptionKeySecret` |
| 2 | `NetworkConfigSync` | `byte SyncType`, `string NetworkConfig` |
| 3 | `ReflexiveInfo` | `int ActorNr`, `NetAddress PublicAddr`, `NetAddress PrivateAddr`, `NATType`, `byte[] UniqueId` |
| 4 | `Disconnect` | `DisconnectReason`, plus a field from ≥2 |
| 5 | `Start` | `int RemoteServerID`, `uint StartRequests` |
| 6 | `Snapshot` | `int Tick`, `uint NetworkID`, `SnapshotType`, `int TotalSize`, `byte[] Data` |
| 7 | `HostMigration` | `byte PeerMode` |
| 8 | `PlayerRefMapping` | `int ActorId`, `int PlayerRef`, `byte[] UniqueId` |
| 9 | `ChangeMasterClient` | `int NewMasterClientCandidate` |
| 10 | `DummyTrafficSync` | — |

### The header every message carries (`Message.Serialize`)

```
byte ProtocolVersion                    // then clamped: value = Math.Min(10, value)
if (value >= 2) { int Major; int Minor; int Build }     // FusionSerializationVersion
if (FusionSerializationVersion.Major == current major)  // else the body is NOT read at all
{
    SerializeProtected(stream)
    if (value >= 3) string CustomData
}
```

The major-version gate matters: echo the request's `Major/Minor/Build` or the peer silently skips
the body of everything you send.

## Enums

```
ProtocolMessageVersion: Invalid=0, V1_0_0=1, V1_1_0=2, V1_2_0=3, V1_2_1=4, V1_2_2=5,
                        V1_2_3=6, V1_3_0=7, V1_4_0=8, V1_5_0=9, V1_6_0=10 (LATEST)
Topologies      (Fusion.Runtime): ClientServer = 1, Shared = 2
PluginGameMode  (wire):           Invalid = 0, ClientServer = 1, Shared = 2
PeerMode:        None = 0, Server = 1, Client = 2
JoinMessageType: Request = 1, Confirmation = 2
JoinRequests:    None = 0, NetworkConfig = 2, ReflexiveInfo = 4, DisableNATPunch = 8
StartRequests:   None = 0, ConnectToShared = 2, WaitForReflexiveInfo = 4
```

## What the client does, and what it needs from us

From `Fusion.Runtime`, the join sequence:

```csharp
switch (_runner.GameMode) {
case GameMode.Shared:            peerMode = Client; joinMode = Shared;       break;
case GameMode.Server: case GameMode.Host:
                                 peerMode = Server; joinMode = ClientServer; break;
case GameMode.Client:            peerMode = Client; joinMode = ClientServer; break;
}
SendMessage(0, new Join(Request, joinMode, peerMode, 0));
// waits up to 30 s for a Confirmation ("Join Confirmation timeout. Shutdown.")
if (joinResponse.EncryptionKey != null) EncryptionToken = { Key, KeyEncrypted };
if (joinResponse.JoinRequests.HasFlag(NetworkConfig)) SendNetworkSyncMessage(...);
if (joinResponse.JoinRequests.HasFlag(ReflexiveInfo)) schedule ReflexiveInfo;
CurrentProtocolMessageVersion = joinResponse.ProtocolVersion;
UniqueId = joinResponse.UniqueId;  PlayerRef = joinResponse.PlayerRef;
```

`HandleJoinMessage` only resolves the waiting task — it validates nothing. So a Confirmation that
arrives at all is accepted; a client still asking for Join has not *received* one.

Then `HandleStartMessage` (asserts sender == 0):

```csharp
if (!await ConfirmJoin()) → "Received Start Message, but never a Join Confirmation. Shutdown."
if (!await _runner.Initialize(...)) → "Runner failed to Initialize. Shutdown."
switch (_runner.GameMode) {
case GameMode.Shared:
    if (start.StartRequests.HasFlag(ConnectToShared)) Connect(Relay, NetAddress.FromActorId(0));
    break;
case GameMode.Server: case GameMode.Host: case GameMode.Client:
    if (ScheduledRequests.ReflexiveInfo) { QueryReflexiveInfo(); SendReflexiveInfo(...); }
    if (_runner.IsClient) { RemoteReflexiveInfo = new(start.RemoteServerID, ...);
                            IsNATPunchthroughEnabled &= start.StartRequests.HasFlag(WaitForReflexiveInfo); }
    _runner.SetupEncryption(EncryptionToken);
    break;
}
```

Consequences for a self-hosted server:

- **A ClientServer peer never connects to actor 0.** It does not bring up the event-101 relay, so
  `fed=0`/`emitted=0` on such a room is correct, not a symptom. Only Shared peers relay through us,
  and only when told `ConnectToShared`.
- **`ConnectToShared` is meaningless outside Shared** — the client only reads it in the Shared branch.
- `SetupEncryption` tolerates a null token (it logs "no token, ignoring"), so omitting
  `EncryptionKey` from the Confirmation is safe.
- A **Host/Server** peer needs nothing else from us after Start; the runner is up at that point.

## Where our implementation still differs

`FusionProtocol.EncodeJoinConfirmation` hardcodes `PeerMode.Client` in the Confirmation. A
ClientServer host announces itself as `PeerMode.Server`, and echoing its own mode back is what the
plugin would do. Nothing in the client's `HandleJoinMessage` reads it, so this is correctness rather
than a known failure — recorded because it is the only field we invent rather than echo.

## The open question (2026-09-27)

Moving Out 2 sends its `Join` **four times** and its 2,814-byte `NetworkConfigSync` four times, then
ENet disconnects it ~4 s after entering the room. Four identical sends inside one second is the shape
of **reliable retransmission**, not of a client retrying a 30-second operation — so the next
measurement is the ENet sequence numbers and our ACKs for this peer (`UdpListener` logs commands with
channel and sequence at Trace level). Fragment reassembly and per-command ACKs are both implemented
(`EnetPeer.HandleCommand`), so the question is whether the ACK we emit matches what the client is
waiting for, not whether we emit one.
