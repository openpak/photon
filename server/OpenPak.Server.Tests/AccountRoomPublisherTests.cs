using System.Net;
using System.Text.Json;
using OpenPak.Server.LoadBalancing;
using Xunit;

namespace OpenPak.Server.Tests;

public class AccountRoomPublisherTests
{
    sealed class Capture : HttpMessageHandler
    {
        public HttpRequestMessage? Request;
        public string Body = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            Body = await request.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task Publishes_the_identity_verbatim_to_the_adapter()
    {
        var capture = new Capture();
        var publisher = new AccountRoomPublisher(new HttpClient(capture), "http://nx-baas:20070/", "key", "0100ED9024EB8000");

        await publisher.PublishAsync("nex:nx2.abc.def", "Q35V9E");

        Assert.Equal("http://nx-baas:20070/internal/photon-room", capture.Request!.RequestUri!.ToString());
        Assert.Equal("key", Assert.Single(capture.Request.Headers.GetValues("X-Internal-Key")));
        var body = JsonDocument.Parse(capture.Body).RootElement;
        Assert.Equal("nex:nx2.abc.def", body.GetProperty("identity").GetString());
        Assert.Equal("Q35V9E", body.GetProperty("room").GetString());
        Assert.Equal("0100ED9024EB8000", body.GetProperty("app_id").GetString());
    }

    [Theory]
    [InlineData(null, "0100ED9024EB8000")]  // unconfigured base url: the rig simply does not publish
    [InlineData("http://nx-baas:20070", null)] // no title id: a room would be filed under the wrong game
    public async Task Publishes_nothing_when_unconfigured(string? baseUrl, string? titleId)
    {
        var capture = new Capture();
        await new AccountRoomPublisher(new HttpClient(capture), baseUrl, "key", titleId).PublishAsync("bsdid:x", "Q35V9E");
        Assert.Null(capture.Request);
    }
}
