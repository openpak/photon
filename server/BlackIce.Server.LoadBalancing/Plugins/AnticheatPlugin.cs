using BlackIce.Server.Core;
using BlackIce.Server.LoadBalancing.Authority;

namespace BlackIce.Server.LoadBalancing.Plugins;

/// <summary>
/// Built-in plugin packaging the server-authority validators. Disabling it (config or `plugins disable
/// anticheat`) removes all of them from the relay, leaving a pure pass-through — vanilla behavior. The
/// thresholds come from <see cref="AnticheatOptions"/> resolved from the host services.
/// </summary>
public sealed class AnticheatPlugin : IServerPlugin
{
    public string Name => "anticheat";
    public string Description => "Server-authority validators: event-flood, movement, damage, hit/headshot rate, view-ownership.";

    public void Configure(PluginBuilder builder)
    {
        var opt = (AnticheatOptions?)builder.Services.GetService(typeof(AnticheatOptions)) ?? new AnticheatOptions();
        builder
            .AddInterceptor(() => new EventRateInterceptor(opt))
            .AddInterceptor(() => new MovementValidationInterceptor(opt.MaxSpeedUnitsPerSecond, opt.MaxTeleportDistance, opt.Enforce))
            .AddInterceptor(() => new DamageValidationInterceptor(opt.MaxDamagePerHit, opt.Enforce))
            .AddInterceptor(() => new HitRateInterceptor(opt))
            .AddInterceptor(() => new ViewOwnershipInterceptor(opt.Enforce));
    }
}
