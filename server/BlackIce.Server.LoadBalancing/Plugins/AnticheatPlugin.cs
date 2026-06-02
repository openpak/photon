using System;
using BlackIce.Server.Core;
using BlackIce.Server.Data;
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
        var rooms = (RoomRegistry?)builder.Services.GetService(typeof(RoomRegistry));
        var accounts = (AccountService?)builder.Services.GetService(typeof(AccountService));

        // A movement-enforcement exemption for verified admins (so the fly/speed plugin works for them only).
        // Honored ONLY for a Steam-verified connection — never for an asserted/anonymous identity, whose
        // (spoofable) SteamID's claimed level must not be trusted. Null deps (tests) → no exemption.
        Func<string, int, bool> isAdminExempt = (room, actor) =>
        {
            if (rooms is null || accounts is null) return false;
            var peer = rooms.FindSession(room)?.PeerOf(actor);
            if (peer is null || !peer.IsVerified || peer.SteamId is null) return false;
            return accounts.Find(peer.SteamId)?.Level >= opt.AdminExemptLevel;
        };

        builder
            .AddInterceptor(() => new EventRateInterceptor(opt))
            .AddInterceptor(() => new MovementValidationInterceptor(opt.MaxSpeedUnitsPerSecond, opt.MaxTeleportDistance, opt.Enforce, isAdminExempt))
            .AddInterceptor(() => new DamageValidationInterceptor(opt.MaxDamagePerHit, opt.Enforce))
            .AddInterceptor(() => new HitRateInterceptor(opt))
            .AddInterceptor(() => new ViewOwnershipInterceptor(opt.Enforce));
    }
}
