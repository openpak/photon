using OpenPak.Photon;
using Xunit;

namespace OpenPak.Photon.Tests;

public class FusionRelayProtocolTests
{
    // Real captured payloads from a live Steam session against real, unredirected Photon Cloud (the
    // same successful "Host Session" test that produced the Join/Start ground truth in
    // FusionProtocolTests — room code JWN7NE). Right after Start, the client tries to Connect over
    // Fusion's separate low-level transport (event code 101, "DATA") to the relay representing
    // itself; decoded field-for-field with an unsafe struct mirroring the real
    // [StructLayout(LayoutKind.Explicit)] NetCommandConnect/NetCommandAccepted layout from the
    // decompiled Fusion.Sockets SDK — see earlier-outbound/HANDOFF.md.
    private static readonly byte[] RealCapturedConnect = Convert.FromHexString("010100000000000000000000010000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000980CE9BAA728637B");

    private static readonly byte[] RealCapturedAccepted = Convert.FromHexString(
        "0102000000000000000000000100000000000000010000000100000000000000");

    [Fact]
    public void TryDecodeConnect_parses_a_real_captured_connect_and_recovers_the_unique_id()
    {
        var connect = FusionRelayProtocol.TryDecodeConnect(RealCapturedConnect);

        Assert.NotNull(connect);
        // Matches the UniqueId FusionProtocol.EncodeJoinConfirmation would have sent earlier in the
        // same session — the client is connecting back using what the server gave it.
        Assert.Equal(Convert.FromHexString("980CE9BAA728637B"), connect!.UniqueId);
    }

    [Fact]
    public void TryDecodeConnect_rejects_wrong_packet_type()
    {
        var wrongPacketType = (byte[])RealCapturedConnect.Clone();
        wrongPacketType[0] = 0;
        Assert.Null(FusionRelayProtocol.TryDecodeConnect(wrongPacketType));
    }

    [Fact]
    public void TryDecodeConnect_rejects_non_connect_command()
    {
        var notConnect = (byte[])RealCapturedConnect.Clone();
        notConnect[1] = 2;   // Accepted, not Connect
        Assert.Null(FusionRelayProtocol.TryDecodeConnect(notConnect));
    }

    [Fact]
    public void TryDecodeConnect_rejects_too_short_payload()
    {
        Assert.Null(FusionRelayProtocol.TryDecodeConnect(RealCapturedConnect[..50]));
    }

    [Fact]
    public void EncodeAccepted_matches_real_captured_server_accepted_exactly()
    {
        Assert.Equal(RealCapturedAccepted, FusionRelayProtocol.EncodeAccepted());
    }
}
