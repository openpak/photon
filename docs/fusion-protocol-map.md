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

## Fusion 1.x is a different protocol registry (2026-09-27, measured against the 1.1.12 SDK)

The map below was transcribed from the **Fusion 2.x** SDK (Outbound ships **2.0.8**, build 2.0.8.1402). Moving Out 2 runs **Fusion 1.1.8**, and the
message ids are not the same. Decompiled from the Fusion 1.1.12 SDK (`scratch/fusion/libs-1.1.12`,
`src-1.1.12`):

| id | Fusion 1.1.x | Fusion 2.x |
|---|---|---|
| 1-7 | Join, NetworkConfigSync, ReflexiveInfo, Disconnect, Start, Snapshot, HostMigration | same |
| 8 | **DummyTrafficSync** | **PlayerRefMapping** |
| 9 | — | ChangeMasterClient |
| 10 | — | DummyTrafficSync |

So `PlayerRefMapping` does not exist before Fusion 2, and a 1.x peer reads ours as a
DummyTrafficSync: fed through 1.1.12's own deserializer, our 35-byte mapping came out as *two*
DummyTrafficSync messages, the first instructing the client to send dummy traffic every 100 ms. The
peer's serialization version arrives in its own Join, so that is the gate — a peer below Fusion 2
gets no mapping.

What IS identical in 1.1.x: the `Message` header and its major-version gate, the `Join` body through
`UniqueId` (and 1.1.x has nothing after it — no PlayerRef, no EncryptionKey), the `Start` body
(`int RemoteServerID`, `uint StartRequests`), `PluginGameMode` (ClientServer=1, Shared=2) and
`ProtocolMessageVersion`. Our Join Confirmation and Start were run through the 1.1.12 deserializer
and come out with exactly the intended field values, so those two messages are correct for both
majors.

Also read from 1.1.12, and worth knowing before blaming the server: `ConfirmJoin()` has **no**
timeout (it waits forever on the join stage; the 15 s deadline belongs to
`Service_CheckScheduledRequests`), `NetworkRunner.Initialize` only ever resolves **true**
(`SetInitializationDone` sets it; the failure modes are thrown exceptions), and for
`GameMode.Host`/`Server` `HandleStartMessage` resolves the cloud operation with `ShutdownReason.Ok`
as soon as Initialize returns. `StartGame` then waits out `IsStarting` and requires the runner not to
be shut down. So nothing beyond Join Confirmation + Start is owed to a 1.x ClientServer host.

## What the client itself requires — measured, not inferred (2026-09-27)

Three questions were settled by running Photon's **own client library** (`Photon3Unity3D.dll`, the
same library the game links) against this server, and by reading the game's compiled copy of it.
The harness is `~/REPOS/Openpak/scratch/mo2/fragack` — it drives a real `PhotonPeer` against either
an in-process `UdpListener` or the production server, and decodes our Fusion messages with the SDK's
own `ProtocolSerializer`. One run takes seconds, so none of this needs a game launch.

**Our fragment acknowledgements are correct.** The retransmission of Moving Out 2's three
`NetworkConfigSync` fragments is NOT an ACK mismatch. Measured twice:
- The real client library accepted our type-16 ACKs and retransmitted nothing — locally and against
  production, plaintext and encrypted, channel 0 and 1, single message and a burst of three.
- Moving Out 2's own matcher, decompiled from its binary (`Photon3Unity3D::RemoveSentReliableCommand`
  at `0x42de0a0`, Photon **4.1.6.23**), requires exactly `reliableSequenceNumber == ackedSequence`,
  `commandChannelID == ackChannel`, and `IsFlaggedUnsequenced == (ackType == 16)`. `ExecuteCommand`
  (`0x42dc130`) derives that third argument as `commandType == 0x10`. That is what we emit.

Retransmits do appear over the internet now and then (one fragment, once, on a 200 ms-RTT run that
did not repeat) — that is ordinary UDP loss, and the server has no retransmission of its own to
recover from it. See below.

**Our Join Confirmation, Start and PlayerRefMapping are wire-correct.** Fed to the SDK's
`ProtocolSerializer.ConvertToMessages`, all three deserialize with the intended field values
(`Type=Confirmation, GameMode=ClientServer, PeerMode=Server, JoinRequests=NetworkConfig,
ProtocolVersion=V1_6_0, FusionSerializationVersion=2.1.2`, an 8-byte UniqueId, empty CustomData).
`dotnet run decode` in the harness re-checks this.

**A datagram bigger than the client's MTU is lost, silently.** Photon's `SocketUdp.ReceiveLoop`
reads into `new byte[MTU]` (default 1200), so a larger datagram is truncated and its command parser
throws — the real client logged `Exception while reading commands from incoming data:
System.ArgumentException` on a 4,035-byte response and never acknowledged it. This server had no
outgoing fragmentation at all, so any reliable message over ~1.15 KB was unreadable by every client.
`EnetPeer.WrapReliableFragmented` now splits one over the MTU into type-8 fragments with consecutive
sequence numbers (all naming the first one's sequence as the group start, which is how the client
keys the group), `UdpListener` packs at most one MTU per datagram, and an oversized datagram is
logged as a warning. Verified with the real client: the same 4 KB response now arrives reassembled.

**Still missing: the server never retransmits its own reliable commands.** The client dispatches a
channel's reliable stream strictly in order (`incomingReliableSequenceNumber + 1`), so a single lost
server datagram stalls everything we send afterwards on that channel — permanently, since nothing
resends it. That is the next transport gap, and it is the remaining transport-level explanation for a
peer that goes quiet a few seconds after joining.
