using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using BlackIce.Photon;
using BlackIce.Server.Core;
using BlackIce.Server.LoadBalancing;
using BlackIce.Server.LoadBalancing.Auth;
using Xunit;

namespace BlackIce.Server.Tests.Auth;

/// <summary>
/// The NameServer's Steam-ticket auth: public peers must present a ticket that validates, LAN peers keep the
/// anonymous (unverified) path, and only a Steam-verified identity yields a token with the verified flag.
/// Driven with a <see cref="FakeValidator"/> and a captured-response peer — no Steam, no socket.
/// </summary>
public class SteamTicketAuthTests
{
    private const string Secret = "test-secret";
    private const string SteamId = "76561198000000009";
    private const byte PUserId = PhotonCodes.Param.UserId, PTicket = PhotonCodes.Param.SteamTicket,
                       PSecret = PhotonCodes.Param.Secret;

    private sealed class FakeValidator : ISteamTicketValidator
    {
        private readonly SteamAuthResult _result;
        private readonly TimeSpan _delay;
        public FakeValidator(SteamAuthResult result, TimeSpan delay = default) { _result = result; _delay = delay; }
        public async Task<SteamAuthResult> ValidateAsync(byte[] ticket, ulong assertedSteamId, CancellationToken ct)
        {
            if (_delay > TimeSpan.Zero) await Task.Delay(_delay, ct);   // honors cancellation (timeout path)
            return _result;
        }
    }

    private sealed class NullHandler : IOperationHandler
    {
        public void OnConnect(PeerConnection peer) { }
        public void OnOperationRequest(PeerConnection peer, OperationRequest request) { }
        public void OnDisconnect(PeerConnection peer) { }
    }

    private static (PeerConnection peer, Func<OperationResponse?> last) Peer(IPAddress ip)
    {
        OperationResponse? captured = null;
        var p = new PeerConnection("NameServer", new IPEndPoint(ip, 5058), new NullHandler(), (_, _) => { });
        p.OnResponse = r => captured = r;
        return (p, () => captured);
    }

    // Public peer: a 203.x address forces the non-LAN branch.
    private static readonly IPAddress Public = IPAddress.Parse("203.0.113.5");

    private static OperationRequest AuthReq(string? userId, byte[]? ticket)
    {
        var p = new System.Collections.Generic.Dictionary<byte, object>();
        if (userId is not null) p[PUserId] = userId;
        if (ticket is not null) p[PTicket] = ticket;
        return new OperationRequest(PhotonCodes.Op.Authenticate, p);
    }

    private static NameServerHandler Handler(ISteamTicketValidator validator) =>
        new("127.0.0.1:5055", Secret, TestAccounts.Create(), validator, allowAnonymousLan: true);

    [Fact]
    public void Public_peer_with_a_verified_ticket_gets_a_verified_token()
    {
        var ns = Handler(new FakeValidator(SteamAuthResult.Verified(76561198000000009UL)));
        var (peer, last) = Peer(Public);

        ns.OnOperationRequest(peer, AuthReq(SteamId, new byte[] { 1, 2, 3 }));

        var resp = last();
        Assert.NotNull(resp);
        Assert.Equal(0, resp!.ReturnCode);
        Assert.True(AuthToken.Validate((string)resp.Parameters[PSecret], Secret).TryGet(out var ident));
        Assert.True(ident.Verified);
        Assert.Equal(SteamId, ident.UserId);
        Assert.True(peer.IsVerified);
    }

    [Fact]
    public void Public_peer_without_a_ticket_is_rejected()
    {
        var ns = Handler(new FakeValidator(SteamAuthResult.Verified(76561198000000009UL)));
        var (peer, last) = Peer(Public);

        ns.OnOperationRequest(peer, AuthReq(SteamId, ticket: null));

        Assert.Equal(-1, last()!.ReturnCode);
        Assert.False(peer.IsVerified);
    }

    [Fact]
    public void Public_peer_with_a_rejected_ticket_is_rejected()
    {
        var ns = Handler(new FakeValidator(SteamAuthResult.Rejected("forged")));
        var (peer, last) = Peer(Public);

        ns.OnOperationRequest(peer, AuthReq(SteamId, new byte[] { 9 }));

        Assert.Equal(-1, last()!.ReturnCode);
        Assert.False(peer.IsVerified);
    }

    [Fact]
    public void Public_peer_is_rejected_when_validation_is_unavailable()
    {
        var ns = Handler(new NullSteamTicketValidator());   // no Steam → fail closed
        var (peer, last) = Peer(Public);

        ns.OnOperationRequest(peer, AuthReq(SteamId, new byte[] { 9 }));

        Assert.Equal(-1, last()!.ReturnCode);
    }

    [Fact]
    public void Public_auth_is_refused_under_the_default_secret_even_with_a_valid_ticket()
    {
        // Security review C1: the verified flag is HMAC-signed with the secret; the shipped default is public,
        // so a verified token would be forgeable. Public auth must fail closed until a real secret is set.
        var ns = new NameServerHandler("127.0.0.1:5055", BlackIce.Server.Core.ServerOptions.DefaultSecret,
            TestAccounts.Create(), new FakeValidator(SteamAuthResult.Verified(76561198000000009UL)),
            allowAnonymousLan: true);
        var (peer, last) = Peer(Public);

        ns.OnOperationRequest(peer, AuthReq(SteamId, new byte[] { 1, 2, 3 }));

        Assert.Equal(-1, last()!.ReturnCode);
        Assert.False(peer.IsVerified);
    }

    [Fact]
    public void Lan_peer_keeps_the_anonymous_unverified_path()
    {
        var ns = Handler(new NullSteamTicketValidator());   // no ticket needed on LAN
        var (peer, last) = Peer(IPAddress.Loopback);

        ns.OnOperationRequest(peer, AuthReq(SteamId, ticket: null));

        var resp = last();
        Assert.NotNull(resp);
        Assert.Equal(0, resp!.ReturnCode);
        Assert.True(AuthToken.Validate((string)resp.Parameters[PSecret], Secret).TryGet(out var ident));
        Assert.False(ident.Verified);     // LAN identity is accepted but NOT verified
        Assert.False(peer.IsVerified);
    }
}
