using BlackIce.Photon;
using Xunit;
using PhotonOpRequest = ExitGames.Client.Photon.OperationRequest;

namespace BlackIce.Photon.Tests;

/// <summary>Message-envelope interop: the real Photon codec must parse what we emit, and vice versa.</summary>
public class MessageTests
{
    [Fact]
    public void Client_parses_our_operation_request()
    {
        var ours = MessageSerializer.SerializeRequest(new OperationRequest(230, new()
        {
            { 220, "Early Access v0.9.226_2.20.1" }, { 210, "us/*" }, { 224, "app-id" },
        }));

        var parsed = (PhotonOpRequest)Oracle.DeserializeMessage(ours);
        Assert.Equal(230, parsed.OperationCode);
        Assert.Equal("Early Access v0.9.226_2.20.1", parsed.Parameters[220]);
        Assert.Equal("us/*", parsed.Parameters[210]);
    }

    [Fact]
    public void Client_parses_our_response_with_address_and_token()
    {
        var ours = MessageSerializer.SerializeResponse(new OperationResponse(230, 0, null, new()
        {
            { 230, "127.0.0.1:5055" }, { 221, "token.sig" }, { 225, "user-1" },
        }));

        var msg = (ExitGames.Client.Photon.OperationResponse)Oracle.DeserializeMessage(ours);
        Assert.Equal(230, msg.OperationCode);
        Assert.Equal(0, msg.ReturnCode);
        Assert.Equal("127.0.0.1:5055", msg.Parameters[230]);
    }

    [Fact]
    public void Client_parses_our_join_event()
    {
        var ours = MessageSerializer.SerializeEvent(new EventData(255, new()
        {
            { 254, 1 }, { 252, new int[] { 1 } },
        }));

        var ev = (ExitGames.Client.Photon.EventData)Oracle.DeserializeMessage(ours);
        Assert.Equal(255, ev.Code);
        Assert.Equal(1, ev.Parameters[254]);
    }

    [Fact]
    public void We_parse_client_operation_request()
    {
        var clientBytes = Oracle.SerializeOperationRequest(227, new() { { 255, "Room #1" } });
        var parsed = MessageSerializer.DeserializeRequest(clientBytes);
        Assert.Equal(227, parsed.OperationCode);
        Assert.Equal("Room #1", parsed.Parameters[255]);
    }

    [Fact]
    public void Client_parses_our_server_message_event()
    {
        // ServerMessage channel (ChatCommandHandler.ServerMessageEvent): event code 199,
        // text under param 245 — exactly what the BlackIce.Motd plugin reads back as
        // photonEvent.Parameters[245]. Round-trip through the real Photon codec to prove
        // the client decodes both the code and the text faithfully.
        var ours = MessageSerializer.SerializeEvent(new EventData(199, new()
        {
            { 245, "Welcome to the server" },
        }));

        var ev = (ExitGames.Client.Photon.EventData)Oracle.DeserializeMessage(ours);
        Assert.Equal(199, ev.Code);
        Assert.Equal("Welcome to the server", ev.Parameters[245]);
    }
}
