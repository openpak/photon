using System.Net;
using OpenPak.Server.LoadBalancing;
using Xunit;

namespace OpenPak.Server.Tests;

/// <summary>An OpenPak account banned in the admin is refused at the Name Server: the Switch adapter
/// answers /internal/photon-identity with 403 for it (website/docs/ban-lookup.md).</summary>
public class BanLookupTests
{
    private sealed class Answer(HttpStatusCode status) : HttpMessageHandler
    {
        public HttpRequestMessage? Seen;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen = request;
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, true)]
    [InlineData(HttpStatusCode.OK, false)]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.BadGateway, false)]
    public async Task OnlyAForbiddenAnswerIsABan(HttpStatusCode status, bool banned)
    {
        var handler = new Answer(status);
        var nodes = new NodeDirectory(new HttpClient(handler), null, null, "http://adapter", "k");

        Assert.Equal(banned, await nodes.IsBannedAsync("bsdid:0011"));
        Assert.Equal("http://adapter/internal/photon-identity", handler.Seen!.RequestUri!.ToString());
        Assert.Equal("k", handler.Seen.Headers.GetValues("X-Internal-Key").Single());
    }

    [Fact]
    public async Task NoAdapterBansNobody()
    {
        var nodes = new NodeDirectory(new HttpClient(new Answer(HttpStatusCode.Forbidden)), null, null, null, null);
        Assert.False(await nodes.IsBannedAsync("bsdid:0011"));
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, IdentityState.Linked)]
    [InlineData(HttpStatusCode.Forbidden, IdentityState.Banned)]
    [InlineData(HttpStatusCode.BadRequest, IdentityState.Unlinked)]
    [InlineData(HttpStatusCode.NotFound, IdentityState.Unlinked)]
    [InlineData(HttpStatusCode.BadGateway, IdentityState.Unknown)]
    public async Task LinkAtFirstUse_UnknownIdentityIsUnlinked_OutageIsNot(HttpStatusCode status, IdentityState want)
    {
        var nodes = new NodeDirectory(new HttpClient(new Answer(status)), null, null, "http://adapter", "k");
        Assert.Equal(want, await nodes.LookupAsync("bsdid:0011"));
        Assert.True(nodes.RequiresLink);
    }

    [Fact]
    public async Task NoAdapterNeverRequiresALink()
    {
        var nodes = new NodeDirectory(new HttpClient(new Answer(HttpStatusCode.BadRequest)), null, null, null, null);
        Assert.False(nodes.RequiresLink);
        Assert.Equal(IdentityState.Unknown, await nodes.LookupAsync("bsdid:0011"));
    }

    private sealed class Core(bool? answer) : ILinkCheck
    {
        public int Calls;
        public Task<bool?> IsLinkedAsync(string baasUserId, CancellationToken ct) { Calls++; return Task.FromResult(answer); }
    }

    private sealed class Body(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
    }

    [Theory]
    [InlineData(true, IdentityState.Linked)]
    [InlineData(false, IdentityState.Unlinked)]
    [InlineData(null, IdentityState.Linked)]   // the core cannot be asked: not a lockout
    public async Task KnownToTheAdapterButNotLinkedInTheCoreIsUnlinked(bool? core, IdentityState want)
    {
        var nodes = new NodeDirectory(new HttpClient(new Body("{\"baas_user_id\":\"abc\"}")), null, null, "http://adapter", "k")
        { CoreLinks = new Core(core) };
        Assert.Equal(want, await nodes.LookupAsync("bsdid:0011"));
    }

    [Fact]
    public async Task ALinkedAnswerIsCachedAndErasable()
    {
        var core = new Core(true);
        var nodes = new NodeDirectory(new HttpClient(new Body("{\"baas_user_id\":\"abc\"}")), null, null, "http://adapter", "k") { CoreLinks = core };
        await nodes.LookupAsync("x"); await nodes.LookupAsync("x");
        Assert.Equal(1, core.Calls);
        Assert.Equal(1, nodes.ForgetLinked("abc"));
        await nodes.LookupAsync("x");
        Assert.Equal(2, core.Calls);
    }

    [Fact]
    public void ParsesAnActiveLinkAndRefusesAPendingOne()
    {
        static byte[] Frame(byte[] msg) => new byte[] { 0, 0, 0, 0, (byte)msg.Length }.Concat(msg).ToArray();
        var acct = System.Text.Encoding.UTF8.GetBytes("acct-1");
        var active = new byte[] { 0x12, (byte)acct.Length }.Concat(acct).Concat(new byte[] { 0x18, 2 }).ToArray();
        var pending = new byte[] { 0x12, (byte)acct.Length }.Concat(acct).Concat(new byte[] { 0x18, 1 }).ToArray();
        Assert.True(CoreLinks.ParseLinked(Frame(active)));
        Assert.False(CoreLinks.ParseLinked(Frame(pending)));
        Assert.False(CoreLinks.ParseLinked(Array.Empty<byte>()));
    }
}
