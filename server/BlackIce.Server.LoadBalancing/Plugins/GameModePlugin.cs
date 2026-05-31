using System.Collections.Generic;
using BlackIce.Photon;
using BlackIce.Server.Core;
using BlackIce.Server.Data;
using BlackIce.Server.LoadBalancing.Authority;

namespace BlackIce.Server.LoadBalancing.Plugins;

/// <summary>
/// Built-in plugin implementing the server-side game modes. On join it reads the realm's Mode, assigns a
/// balanced team for team modes, and broadcasts it as the standard "Team" property (client-rendered); the
/// TeamDamageInterceptor it adds drops friendly-fire / PvE-forbidden damage on the relay. Disabling it
/// reverts every room to free-for-all relay (no teams assigned, no damage filtered).
/// </summary>
public sealed class GameModePlugin : IServerPlugin
{
    public string Name => "gamemodes";
    public string Description => "Server-side game modes (Team-vs-Team / Co-op): team assignment + friendly-fire/PvE damage filtering.";

    public void Configure(PluginBuilder builder)
    {
        var modes = (GameModeRegistry?)builder.Services.GetService(typeof(GameModeRegistry)) ?? new GameModeRegistry();
        var realms = (RealmService?)builder.Services.GetService(typeof(RealmService));
        var rooms = (RoomRegistry?)builder.Services.GetService(typeof(RoomRegistry));

        builder
            .AddInterceptor(() => new TeamDamageInterceptor(modes))
            .OnActorJoined(ctx => AssignTeam(ctx, modes, realms, rooms))
            .OnActorLeft(ctx => modes.Remove(ctx.RoomName, ctx.Actor));
    }

    private static void AssignTeam(RoomActorContext ctx, GameModeRegistry modes, RealmService? realms, RoomRegistry? rooms)
    {
        var mode = GameModeRegistry.Parse(realms?.Get(ctx.RoomName)?.Mode);
        modes.SetMode(ctx.RoomName, mode);
        if (mode == GameMode.FreeForAll) return;

        int team = modes.AssignTeam(ctx.RoomName, ctx.Actor);
        var teamProp = new Dictionary<object, object> { { "Team", team } };
        rooms?.Find(ctx.RoomName)?.SetProperties(ctx.Actor, teamProp);   // persist for GetProperties
        ctx.Session.SendToAll(new EventData(PhotonCodes.Event.PropertiesChanged, new()
        {
            { PhotonCodes.Param.Properties, teamProp },
            { PhotonCodes.Param.TargetActorNr, ctx.Actor },
        }));
        Log.Info("GameMode", $"\"{ctx.RoomName}\" [{mode}] assigned actor {ctx.Actor} to team {team}");
    }
}
