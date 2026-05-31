using System.Collections.Generic;
using BlackIce.Photon;
using BlackIce.Server.LoadBalancing;
using Xunit;

namespace BlackIce.Server.Tests;

public class RelayVerdictTests
{
    [Fact]
    public void Forward_carries_the_original_event_and_no_extras()
    {
        var ev = new EventData(200, new() { { 245, "x" } });
        var v = RelayVerdict.Forward(ev);
        Assert.Equal(RelayAction.Forward, v.Action);
        Assert.Same(ev, v.Event);
        Assert.Empty(v.Originated);
    }

    [Fact]
    public void Drop_carries_no_event()
    {
        var v = RelayVerdict.Drop();
        Assert.Equal(RelayAction.Drop, v.Action);
        Assert.Null(v.Event);
    }

    [Fact]
    public void Rewrite_replaces_the_forwarded_event()
    {
        var replacement = new EventData(200, new() { { 245, "clamped" } });
        var v = RelayVerdict.Rewrite(replacement);
        Assert.Equal(RelayAction.Rewrite, v.Action);
        Assert.Same(replacement, v.Event);
    }

    [Fact]
    public void Originate_forwards_original_plus_extra_events()
    {
        var ev = new EventData(200, new());
        var extra = new EventData(202, new());
        var v = RelayVerdict.Originate(ev, new[] { extra });
        Assert.Equal(RelayAction.Originate, v.Action);
        Assert.Same(ev, v.Event);
        Assert.Single(v.Originated);
        Assert.Same(extra, v.Originated[0]);
    }
}
