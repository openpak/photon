using System.Text;
using OpenPak.Photon;
using Xunit;

namespace OpenPak.Photon.Tests;

/// <summary>The web-style Init the Photon C++ SDK sends on the Master hop after AuthenticateOnce.
/// The request line starts as captured from Unrailed! ("POST /?init=&amp;app"); the rest is a stand-in.</summary>
public class WebInitTests
{
    private const string App = "9d45adf2-e83f-4281-b9f5-02728f813f80";

    [Fact]
    public void Parses_the_query_and_offers_the_token()
    {
        var payload = Encoding.ASCII.GetBytes($"POST /?init=&app={App}&clientversion=4.1.12.2&token=abc%2Bdef.ghijklmnopqrstuv HTTP/1.1\r\n\r\n");
        Assert.True(WebInit.TryParse(payload, out var init));
        Assert.Equal("POST", init.Method);
        Assert.Equal("/", init.Path);
        Assert.Equal(App, init.Get("app"));
        Assert.Contains("abc+def.ghijklmnopqrstuv", init.TokenCandidates());
        Assert.Empty(init.Trailer);
    }

    [Fact]
    public void A_binary_trailer_after_the_text_is_offered_as_a_typed_string()
    {
        var head = Encoding.ASCII.GetBytes($"POST /?init=&app={App}&clientversion=4.1.12.2");
        var token = new GpBinaryWriter().WriteTyped("tok.en-value-0123456789").ToArray();
        Assert.True(WebInit.TryParse(head.Concat(token).ToArray(), out var init));
        Assert.Equal(token.Length, init.Trailer.Length);
        Assert.Contains("tok.en-value-0123456789", init.TokenCandidates());
    }

    [Fact]
    public void Describe_never_prints_a_token()
    {
        var payload = Encoding.ASCII.GetBytes($"POST /?init=&app={App}&clientversion=4.1&xInit=SECRETTOKENVALUE\r\nAuthorization: SECRETHEADER\r\n\r\n");
        Assert.True(WebInit.TryParse(payload, out var init));
        var d = init.Describe();
        Assert.Contains(App, d);
        Assert.DoesNotContain("SECRET", d);
    }

    [Fact]
    public void Binary_init_is_not_a_web_init()
        => Assert.False(WebInit.TryParse(new byte[] { 0xF3, 0x00, 0x01, 0x06 }, out _));
}
