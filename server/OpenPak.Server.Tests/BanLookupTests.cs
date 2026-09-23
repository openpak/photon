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
        var nodes = new NodeDirectory(new HttpClient(handler), null, "http://adapter", "k");

        Assert.Equal(banned, await nodes.IsBannedAsync("bsdid:0011"));
        Assert.Equal("http://adapter/internal/photon-identity", handler.Seen!.RequestUri!.ToString());
        Assert.Equal("k", handler.Seen.Headers.GetValues("X-Internal-Key").Single());
    }

    [Fact]
    public async Task NoAdapterBansNobody()
    {
        var nodes = new NodeDirectory(new HttpClient(new Answer(HttpStatusCode.Forbidden)), null, null, null);
        Assert.False(await nodes.IsBannedAsync("bsdid:0011"));
    }
}
