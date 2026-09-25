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

    // One server, several games: a room is filed under the title whose Photon AppId the peer
    // authenticated with, and only falls back to the default when the AppId is unknown.
    [Theory]
    [InlineData("a956fef2-5ccf-4c52-9df1-adb2b1b6953b", "0100C4C00E73E000")] // Moving Out
    [InlineData("A956FEF2-5CCF-4C52-9DF1-ADB2B1B6953B", "0100C4C00E73E000")] // clients send either case
    [InlineData("cffbe809-5036-43d8-84a1-7bf16c924721", "0100ED9024EB8000")] // Outbound
    [InlineData("00000000-0000-0000-0000-000000000000", "0100ED9024EB8000")] // unmapped -> default
    [InlineData(null, "0100ED9024EB8000")]                                   // no AppId  -> default
    public async Task Files_a_room_under_the_title_its_app_id_names(string? appId, string expected)
    {
        var capture = new Capture();
        var titles = PhotonTitles.Parse("cffbe809-5036-43d8-84a1-7bf16c924721=0100ED9024EB8000," +
                                        "a956fef2-5ccf-4c52-9df1-adb2b1b6953b=0100C4C00E73E000");
        var publisher = new AccountRoomPublisher(new HttpClient(capture), "http://nx-baas:20070", "key",
                                                 "0100ED9024EB8000", titles);

        await publisher.PublishAsync("bsdid:x", "Q35V9E", appId);

        var body = JsonDocument.Parse(capture.Body).RootElement;
        Assert.Equal(expected, body.GetProperty("app_id").GetString());
    }
}
