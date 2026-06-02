using System.Threading;
using System.Threading.Tasks;
using BlackIce.Server.LoadBalancing.Auth;
using Xunit;

namespace BlackIce.Server.Tests.Auth;

public class NullValidatorTests
{
    [Fact]
    public async Task Null_validator_reports_unavailable()
    {
        var result = await new NullSteamTicketValidator()
            .ValidateAsync(new byte[] { 1, 2, 3 }, 76561198000000000UL, CancellationToken.None);

        Assert.Equal(SteamAuthOutcome.Unavailable, result.Outcome);
        Assert.Equal(0UL, result.SteamId);
        Assert.NotNull(result.Reason);
    }
}
