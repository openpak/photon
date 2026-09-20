using OpenPak.Photon;
using OpenPak.Server.Data;
using OpenPak.Server.LoadBalancing;
using Xunit;

namespace OpenPak.Server.Tests;

public class IdentityTests
{
    const byte PUserId = 225;
    const string Secret = "test-secret";

    [Fact]
    public void NameServer_creates_account_and_returns_token_for_steamid()
    {
        var accounts = TestAccounts.Create();
        var ns = new NameServerHandler("127.0.0.1:5055", Secret, accounts);

        var resp = ns.Authenticate(new OperationRequest(230, new() { { PUserId, "76561198000000009" } }));
        Assert.Equal(0, resp.ReturnCode);
        Assert.NotNull(accounts.Find("76561198000000009"));   // account auto-created
        Assert.Equal(PlayerLevel.Player, accounts.Find("76561198000000009")!.Level);
        Assert.True(resp.Parameters.ContainsKey(221));         // token minted
    }

    [Fact]
    public void NameServer_refuses_banned_account()
    {
        const string bannedSteamId = "76561198000000010";   // valid SteamID64 format
        var accounts = TestAccounts.Create();
        accounts.ResolveOrCreate(bannedSteamId, "x");
        accounts.SetBanned(bannedSteamId, true);
        var ns = new NameServerHandler("127.0.0.1:5055", Secret, accounts);

        var resp = ns.Authenticate(new OperationRequest(230, new() { { PUserId, bannedSteamId } }));
        Assert.NotEqual(0, resp.ReturnCode);
    }

    [Fact]
    public void NameServer_ignores_malformed_spoofed_identity()
    {
        var accounts = TestAccounts.Create();
        var ns = new NameServerHandler("127.0.0.1:5055", Secret, accounts);

        // A junk (non-SteamID) UserId must not become the account key; it falls back to a
        // throwaway id, so no account named "not-a-steamid" is ever created.
        ns.Authenticate(new OperationRequest(230, new() { { PUserId, "not-a-steamid" } }));
        Assert.Null(accounts.Find("not-a-steamid"));
    }

    // Real nx2 payloads as prod minted them on 2026-09-20: "<pid>.<username>.<expiry>", base64url.
    // The two tobagin tokens are the same account logging in twice, ~5.6 h apart.
    const string TobaginLogin1 = "nex:nx2.MTgyMjExODc4Mi50b2JhZ2luLjE3OTI0NzkyMzM.c2lnbmF0dXJl";
    const string TobaginLogin2 = "nex:nx2.MTgyMjExODc4Mi50b2JhZ2luLjE3OTI0OTk1MjA.c2lnbmF0dXJl";
    const string LeiaLogin = "nex:nx2.MTg0NTUyMzIwMy5MZWlhLjE3OTI0OTk4NzM.c2lnbmF0dXJl";

    [Fact]
    public void Photon_user_id_survives_a_relogin()
    {
        var identities = new AccountIdentityRegistry();

        // The credential is minted per session and carries an expiry, so the two differ. The UserId
        // must not: the game keys a returning player's character on it, and a world that already
        // knows a player cannot match them if their id rotated under them.
        Assert.NotEqual(TobaginLogin1, TobaginLogin2);
        Assert.Equal(identities.Register(TobaginLogin1), identities.Register(TobaginLogin2));

        // ...and it still tells two accounts apart.
        Assert.NotEqual(identities.Register(TobaginLogin1), identities.Register(LeiaLogin));
    }

    [Fact]
    public void Registered_identity_is_kept_verbatim_for_the_account_adapter()
    {
        var identities = new AccountIdentityRegistry();

        // Only nx-baas can verify the credential's HMAC, so the full token — not the stable key we
        // hash — is what must come back out.
        var userId = identities.Register(TobaginLogin2);
        Assert.Equal(TobaginLogin2, identities.Find(userId));
    }

    [Fact]
    public void Unparseable_identity_still_yields_a_user_id()
    {
        var identities = new AccountIdentityRegistry();

        Assert.StartsWith("openpak:", identities.Register("bsdid:574741535d0eef77"));
        Assert.StartsWith("openpak:", identities.Register("nex:nx2.!!not-base64!!.sig"));
        Assert.StartsWith("openpak:", identities.Register("nex:nx2.bm8tZG90cy1oZXJl.sig"));
    }

    [Fact]
    public void Master_refuses_banned_account_via_token()
    {
        var accounts = TestAccounts.Create();
        accounts.ResolveOrCreate("banned-2", "x");
        accounts.SetBanned("banned-2", true);
        var master = new MasterServerHandler("127.0.0.1:5056", Secret, new RoomRegistry(), accounts: accounts);

        var token = AuthToken.Mint("banned-2", Secret);
        var resp = master.Authenticate(new OperationRequest(230, new() { { 221, token } }));
        Assert.Equal(-3, resp.ReturnCode);
    }
}
