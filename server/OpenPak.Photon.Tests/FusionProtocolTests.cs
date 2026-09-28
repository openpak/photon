using OpenPak.Photon;
using Xunit;

namespace OpenPak.Photon.Tests;

public class FusionProtocolTests
{
    // A real captured payload from a live Switch client's first RaiseEvent(code=100) after joining
    // a room: a Join Request, ProtocolVersion V1_6_0 (10), FusionSerializationVersion 2.0.8,
    // GameMode=Shared(2), PeerMode=Client(2), no requests, no unique id / player ref / encryption /
    // custom data. Decoded and cross-checked bit-for-bit (220 of 224 bits consumed, remainder is
    // byte-alignment padding) against the algorithm in FusionProtocol.cs.
    private static readonly byte[] RealCapturedJoinRequest =
    {
        0x01, 0x0A, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x08, 0x00, 0x00, 0x00,
        0x01, 0x02, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    };

    [Fact]
    public void TryDecodeJoin_parses_a_real_captured_client_request()
    {
        var join = FusionProtocol.TryDecodeJoin(RealCapturedJoinRequest);

        Assert.NotNull(join);
        Assert.Equal(10, join!.ProtocolVersion);
        Assert.Equal((2, 0, 8), (join.Major, join.Minor, join.Build));
        Assert.Equal(FusionProtocol.JoinTypeRequest, join.Type);
        Assert.Equal(2, join.GameMode);
        Assert.Equal(FusionProtocol.PeerModeClient, join.PeerMode);
        Assert.Equal(0u, join.JoinRequests);
        Assert.Null(join.UniqueId);
        Assert.Equal(0, join.PlayerRef);
        Assert.Equal("", join.CustomData);
    }

    [Fact]
    public void TryDecodeJoin_rejects_wrong_message_id()
    {
        var notJoin = (byte[])RealCapturedJoinRequest.Clone();
        notJoin[0] = 5;   // DummyTrafficSync's id, not Join's
        Assert.Null(FusionProtocol.TryDecodeJoin(notJoin));
    }

    [Fact]
    public void EncodeJoinConfirmation_round_trips_through_TryDecodeJoin()
    {
        var request = FusionProtocol.TryDecodeJoin(RealCapturedJoinRequest)!;
        var confirmationBytes = FusionProtocol.EncodeJoinConfirmation(request, playerRef: 1);

        var decoded = FusionProtocol.TryDecodeJoin(confirmationBytes);

        Assert.NotNull(decoded);
        Assert.Equal(FusionProtocol.JoinTypeConfirmation, decoded!.Type);
        // Matches a real Photon Cloud server's Confirmation, captured live: it answers as
        // PeerModeClient too (not Server) — see EncodeJoinConfirmation's doc comment.
        Assert.Equal(FusionProtocol.PeerModeClient, decoded.PeerMode);
        Assert.Equal(1, decoded.PlayerRef);
        Assert.Equal(request.GameMode, decoded.GameMode);
        Assert.Equal(request.ProtocolVersion, decoded.ProtocolVersion);
    }

    // Ground truth from a real Photon Cloud game server: captured live via a Steam cross-test
    // against real, unredirected Photon Cloud (a real "Host Session" that actually succeeded,
    // room code JWN7NE), for the exact same solo-host bootstrap our server needs to answer.
    // Decoded byte-for-byte (each consumes every bit but trailing byte-alignment padding) — see
    // earlier-outbound/HANDOFF.md for the full capture/decode trail.
    private static readonly byte[] RealCapturedConfirmation =
    {
        0x01, 0x0A, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x0D, 0x00, 0x00, 0x00,
        0x02, 0x02, 0x02, 0x02, 0x00, 0x00, 0x00, 0x11, 0x00, 0x30, 0x19, 0xD2, 0x75, 0x4F,
        0x51, 0xC6, 0xF6, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    };
    private static readonly byte[] RealCapturedStart =
    {
        0x05, 0x0A, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x0D, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    };

    [Fact]
    public void TryDecodeJoin_parses_a_real_captured_server_confirmation()
    {
        var confirmation = FusionProtocol.TryDecodeJoin(RealCapturedConfirmation);

        Assert.NotNull(confirmation);
        Assert.Equal(FusionProtocol.JoinTypeConfirmation, confirmation!.Type);
        Assert.Equal(2, confirmation.GameMode);
        Assert.Equal(FusionProtocol.PeerModeClient, confirmation.PeerMode);
        Assert.Equal(FusionProtocol.JoinRequestsNetworkConfig, confirmation.JoinRequests);
        Assert.Equal(8, confirmation.UniqueId?.Length);
        Assert.Equal(1, confirmation.PlayerRef);
        Assert.Equal("", confirmation.CustomData);
    }

    [Fact]
    public void TryDecodeStart_parses_a_real_captured_server_start()
    {
        var start = FusionProtocol.TryDecodeStart(RealCapturedStart);

        Assert.NotNull(start);
        Assert.Equal(0, start!.RemoteServerID);
        Assert.Equal(FusionProtocol.StartRequestsConnectToShared, start.StartRequests);
        Assert.Equal("", start.CustomData);
    }

    [Fact]
    public void EncodeStart_is_well_formed_and_distinct_from_join()
    {
        var startBytes = FusionProtocol.EncodeStart(protocolVersion: 10, major: 2, minor: 0, build: 8,
                                                     remoteServerId: 0, FusionProtocol.StartRequestsNone);

        Assert.NotEmpty(startBytes);
        Assert.NotEqual(1, startBytes[0]);   // not Join's message id
        // A Start message isn't a Join, so the Join decoder must reject it rather than misparse it.
        Assert.Null(FusionProtocol.TryDecodeJoin(startBytes));
    }

    [Fact]
    public void EncodeStart_matches_real_captured_server_start_field_for_field()
    {
        var startBytes = FusionProtocol.EncodeStart(protocolVersion: 10, major: 2, minor: 0, build: 13,
                                                     remoteServerId: 0, FusionProtocol.StartRequestsConnectToShared);

        Assert.Equal(RealCapturedStart, startBytes);
    }

    // The message that makes a ClientServer client connect: its ActorNr has to be the host the Start
    // named, and both addresses have to read as unset so the client falls back to the relay. Accepted
    // by a real Fusion 1.1.12 client (scratch/mo2/fusionhost, FUSIONHOST_MODE=client), which logged
    // `ReflexiveInfo: ActorNr=1, PublicAddr=[... IsValid: False ...]` and then connected.
    [Fact]
    public void EncodeReflexiveInfo_names_the_actor_and_leaves_both_addresses_unset()
    {
        var bytes = FusionProtocol.EncodeReflexiveInfo(6, 1, 1, 8, actorNr: 1, FusionProtocol.UniqueIdForActor(1));

        Assert.Equal(FusionProtocol.MsgIdReflexiveInfo, bytes[0]);
        Assert.Equal(6, bytes[1]);                                        // protocol version
        Assert.Equal(new byte[] { 1, 0, 0, 0 }, bytes[14..18]);           // ActorNr, after the 14-byte header
        Assert.All(bytes[18..66], b => Assert.Equal(0, b));               // 2 addresses x 3 ulongs
        Assert.Equal(FusionProtocol.NatTypeUdpBlocked, bytes[66]);
        Assert.Equal(80, bytes.Length);                                   // + UniqueId (1+16+64 bits) + CustomData
    }
}
