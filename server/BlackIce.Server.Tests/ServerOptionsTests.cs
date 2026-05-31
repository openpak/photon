using BlackIce.Server.Core;
using Xunit;

namespace BlackIce.Server.Tests;

public class ServerOptionsTests
{
    [Fact]
    public void Defaults_are_valid_but_flag_the_placeholder_secret()
    {
        var opts = new ServerOptions();
        Assert.Empty(opts.Validate());
        Assert.True(opts.UsesDefaultSecret);
    }

    [Fact]
    public void A_changed_secret_is_no_longer_flagged()
    {
        var opts = new ServerOptions { Secret = "a-real-deployment-secret" };
        Assert.False(opts.UsesDefaultSecret);
        Assert.Empty(opts.Validate());
    }

    [Fact]
    public void Empty_secret_is_an_error()
    {
        var opts = new ServerOptions { Secret = "  " };
        Assert.Contains(opts.Validate(), e => e.Contains("Secret"));
    }

    [Fact]
    public void Duplicate_ports_are_rejected()
    {
        var opts = new ServerOptions { Ports = new ServerPorts { NameServer = 5055, MasterServer = 5055, GameServer = 5056 } };
        Assert.Contains(opts.Validate(), e => e.Contains("distinct"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(70000)]
    [InlineData(-1)]
    public void Out_of_range_ports_are_rejected(int badPort)
    {
        var opts = new ServerOptions { Ports = new ServerPorts { NameServer = badPort, MasterServer = 5055, GameServer = 5056 } };
        Assert.Contains(opts.Validate(), e => e.Contains("range"));
    }

    [Fact]
    public void Dead_timeout_must_exceed_ping_quiet()
    {
        var opts = new ServerOptions { Listener = new ListenerTimings { PingQuietSeconds = 10, DeadTimeoutSeconds = 5 } };
        Assert.Contains(opts.Validate(), e => e.Contains("DeadTimeoutSeconds"));
    }

    [Fact]
    public void Listener_timings_convert_seconds_to_timespans()
    {
        var t = new ListenerTimings { MaintenanceSeconds = 2, PingQuietSeconds = 4, DeadTimeoutSeconds = 12 };
        Assert.Equal(System.TimeSpan.FromSeconds(2), t.Maintenance);
        Assert.Equal(System.TimeSpan.FromSeconds(4), t.PingQuiet);
        Assert.Equal(System.TimeSpan.FromSeconds(12), t.DeadTimeout);
    }

    [Fact]
    public void Anticheat_defaults_are_valid_and_detection_only()
    {
        var opts = new ServerOptions();
        Assert.Empty(opts.Validate());
        Assert.False(opts.Anticheat.Enforce);
    }

    [Fact]
    public void Invalid_anticheat_options_surface_through_server_validate()
    {
        var opts = new ServerOptions { Anticheat = new AnticheatOptions { RateWindowSeconds = 0, MaxHitsPerWindow = 0 } };
        var errors = opts.Validate();
        Assert.Contains(errors, e => e.Contains("RateWindowSeconds"));
        Assert.Contains(errors, e => e.Contains("MaxHitsPerWindow"));
    }
}
