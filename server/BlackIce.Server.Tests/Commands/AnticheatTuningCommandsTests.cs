using BlackIce.Server.Core;
using BlackIce.Server.Data;
using BlackIce.Server.LoadBalancing.Plugins;
using Xunit;

namespace BlackIce.Server.Tests.Commands;

/// <summary>
/// Covers the live anti-cheat tuning console commands: the dump, typed sets that reach the shared
/// <see cref="AnticheatOptions"/>, the enforce toggle, the Validate-driven revert on a bad set, and the
/// unknown-field path. The commands are reflected through a <see cref="CommandRegistry"/> exactly as the
/// live console drives them.
/// </summary>
public class AnticheatTuningCommandsTests
{
    private static (CommandRegistry reg, AnticheatOptions opt) Setup()
    {
        var opt = new AnticheatOptions();
        var reg = new CommandRegistry().Register(new AnticheatTuningCommands(opt));
        return (reg, opt);
    }

    [Fact]
    public void Anticheat_dumps_every_tunable()
    {
        var (reg, _) = Setup();

        Assert.True(reg.TryExecute("anticheat", PlayerLevel.Console, out var o));
        Assert.Contains("Enforce", o);
        Assert.Contains("MaxSpeedUnitsPerSecond", o);
        Assert.Contains("AdminExemptLevel", o);
    }

    [Fact]
    public void Set_updates_the_shared_options_instance()
    {
        var (reg, opt) = Setup();

        Assert.True(reg.TryExecute("anticheat set maxspeedunitspersecond 300", PlayerLevel.Console, out var o));
        Assert.Contains("set maxspeedunitspersecond = 300", o);
        Assert.Equal(300f, opt.MaxSpeedUnitsPerSecond);
    }

    [Fact]
    public void Enforce_on_toggles_enforcement()
    {
        var (reg, opt) = Setup();

        Assert.True(reg.TryExecute("anticheat enforce on", PlayerLevel.Console, out _));
        Assert.True(opt.Enforce);
    }

    [Fact]
    public void A_set_that_fails_validation_is_reverted_and_reported()
    {
        var (reg, opt) = Setup();
        var prior = opt.RateWindowSeconds;

        Assert.True(reg.TryExecute("anticheat set ratewindowseconds -1", PlayerLevel.Console, out var o));
        Assert.Contains("RateWindowSeconds", o);              // the Validate() error surfaced
        Assert.Equal(prior, opt.RateWindowSeconds);          // and the value is unchanged (reverted)
    }

    [Fact]
    public void Set_on_an_unknown_field_reports_no_such_field()
    {
        var (reg, _) = Setup();

        Assert.True(reg.TryExecute("anticheat set bogus 1", PlayerLevel.Console, out var o));
        Assert.Contains("no such field", o);
    }
}
