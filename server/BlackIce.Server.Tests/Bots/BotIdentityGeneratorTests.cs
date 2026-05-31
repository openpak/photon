using BlackIce.Server.LoadBalancing.Bots;
using Xunit;

namespace BlackIce.Server.Tests.Bots;

public class BotIdentityGeneratorTests
{
    [Fact]
    public void Generates_a_nonempty_name_and_in_range_model()
    {
        var id = new BotIdentityGenerator(seed: 42).Next();
        Assert.False(string.IsNullOrWhiteSpace(id.Name));
        Assert.InRange(id.ModelIndex, 0, 31);
        Assert.Equal(4, id.ModelColors.Length);   // main/secondary/tertiary/quaternary RGBA
    }

    [Fact]
    public void Successive_identities_differ()
    {
        var g = new BotIdentityGenerator(seed: 7);
        var a = g.Next(); var b = g.Next();
        Assert.NotEqual(a.Name, b.Name);
    }
}
