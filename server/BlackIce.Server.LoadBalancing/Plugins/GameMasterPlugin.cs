using System.Collections.Generic;
using BlackIce.Photon;
using BlackIce.Server.Core;
using BlackIce.Server.Data;
using BlackIce.Server.LoadBalancing.Authority;

namespace BlackIce.Server.LoadBalancing.Plugins;

/// <summary>
/// Game-master manipulation: console commands that let an admin act on EXISTING live entities the master
/// client has spawned, by relaying the SAME Photon RPC/event shapes the playerbots already emit (damage,
/// XP/buff, destroy) at a chosen <c>viewId</c>. The viewId is validated against the room's authoritative
/// <see cref="RoomWorldState"/> shadow before anything is sent — an unknown view is refused rather than
/// guessed — and every send is queued onto the listener thread via <see cref="AdminActionQueue"/> (only
/// that thread may touch per-peer transport), so commands report "queued" and take effect next tick.
///
/// <para><b>Honesty note:</b> whether the master client APPLIES a server-originated RPC is UNCONFIRMED — it
/// is the open question in the live-verification roadmap (docs/protocol/live-verification.md). These commands
/// reuse the exact captured shapes the bots use so they are correct on the wire, but each carries a
/// <c>// GAP:</c> marker pinning that in-game acceptance is unverified until tested against the live game.</para>
/// </summary>
public sealed class GameMasterPlugin : IServerPlugin
{
    public string Name => "gamemaster";
    public string Description => "Game-master tools: relay damage/kill/xp/destroy RPCs at a live entity's viewId (reuses the playerbot wire shapes; in-game acceptance unverified). Admin-only.";

    public void Configure(PluginBuilder builder)
    {
        var rooms = (RoomRegistry?)builder.Services.GetService(typeof(RoomRegistry));
        var admin = (AdminActionQueue?)builder.Services.GetService(typeof(AdminActionQueue));
        var worlds = (RoomWorldStateRegistry?)builder.Services.GetService(typeof(RoomWorldStateRegistry));
        if (rooms is null || admin is null || worlds is null) return;   // missing a dependency → contribute nothing
        builder.AddCommands(new GameMasterCommands(rooms, admin, worlds));
    }
}

/// <summary>
/// The game-master console commands. Each resolves the realm (tolerating dash/case/space variants via
/// <see cref="RoomRegistry.ResolveName"/>), checks the target viewId exists in the room world-state, then
/// queues a relay of the bots' captured RPC/event shape onto the listener thread.
/// </summary>
internal sealed class GameMasterCommands
{
    private const float KillDamage = 1e9f;   // a damage value large enough to drop any entity in one TakeDamage

    private readonly RoomRegistry _rooms;
    private readonly AdminActionQueue _admin;
    private readonly RoomWorldStateRegistry _worlds;

    public GameMasterCommands(RoomRegistry rooms, AdminActionQueue admin, RoomWorldStateRegistry worlds)
    {
        _rooms = rooms;
        _admin = admin;
        _worlds = worlds;
    }

    [ConsoleCommand("damage", Usage = "<realm> <viewId> <amount>", MinParts = 4, MinLevel = PlayerLevel.Admin)]
    private string Damage(CommandLine line)
    {
        if (!Resolve(line, out var realm, out var viewId, out var error)) return error;
        if (!float.TryParse(Arg(line, 3), out var amount)) return "amount must be a number.";
        // Reuse the bots' TakeDamage shape (DamageData.BuildTakeDamageRpc == HunterBehavior.DamageRpc).
        // GAP: in-game acceptance of server-originated damage RPC unconfirmed — verify against the live master client (docs/protocol/live-verification.md).
        var ev = DamageData.BuildTakeDamageRpc(viewId, amount);
        _admin.Enqueue(() => _rooms.FindSession(realm)?.SendToAll(ev));
        return $"queued damage {amount} to {realm}#view{viewId}";
    }

    [ConsoleCommand("kill", Usage = "<realm> <viewId>", MinParts = 3, MinLevel = PlayerLevel.Admin)]
    private string Kill(CommandLine line)
    {
        if (!Resolve(line, out var realm, out var viewId, out var error)) return error;
        // A very large TakeDamage at the target — same shape as damage, lethal magnitude.
        // GAP: in-game acceptance of server-originated kill (TakeDamage) RPC unconfirmed — verify against the live master client (docs/protocol/live-verification.md).
        var ev = DamageData.BuildTakeDamageRpc(viewId, KillDamage);
        _admin.Enqueue(() => _rooms.FindSession(realm)?.SendToAll(ev));
        return $"queued kill of {realm}#view{viewId}";
    }

    [ConsoleCommand("xp", Usage = "<realm> <viewId> <amount>", MinParts = 4, MinLevel = PlayerLevel.Admin)]
    private string Xp(CommandLine line)
    {
        if (!Resolve(line, out var realm, out var viewId, out var error)) return error;
        if (!int.TryParse(Arg(line, 3), out var amount)) return "amount must be a whole number.";
        // Reuse the bots' progression RPC: AddBuffRPC with the same arg layout HunterBehavior.LevelUpRpc emits
        // ({ buffId, magnitude, duration, ..., scale, ... }); the operator amount drives the magnitude slot.
        // GAP: in-game acceptance of server-originated xp/AddBuff RPC unconfirmed — verify against the live master client (docs/protocol/live-verification.md).
        var ev = Rpc(viewId, "AddBuffRPC", new object[] { 1, amount, 30f, 0, 1.5f, 0 });
        _admin.Enqueue(() => _rooms.FindSession(realm)?.SendToAll(ev));
        return $"queued xp/buff {amount} to {realm}#view{viewId}";
    }

    [ConsoleCommand("destroy", Usage = "<realm> <viewId>", MinParts = 3, MinLevel = PlayerLevel.Admin)]
    private string Destroy(CommandLine line)
    {
        if (!Resolve(line, out var realm, out var viewId, out var error)) return error;
        // PUN Destroy (204): payload carries the viewId at key 0, matching the live-capture shape the
        // WorldStateObserver reads ({0=viewId}). This is the despawn event PUN emits for a networked object.
        // GAP: in-game acceptance of server-originated destroy (204) event unconfirmed — verify against the live master client (docs/protocol/live-verification.md).
        var ev = new EventData(PhotonCodes.PunEvent.Destroy, new()
        {
            { PhotonCodes.Param.Data, new Dictionary<object, object> { { (byte)0, viewId } } },
        });
        _admin.Enqueue(() => _rooms.FindSession(realm)?.SendToAll(ev));
        return $"queued destroy of {realm}#view{viewId}";
    }

    // --- helpers ---------------------------------------------------------------------------------

    /// <summary>
    /// Resolves the realm (arg 1) and viewId (arg 2) shared by every command: realm must exist and the
    /// viewId must be a number the room world-state has observed. On any failure returns false with a
    /// human-readable <paramref name="error"/>; on success returns true with the canonical realm + viewId.
    /// </summary>
    private bool Resolve(CommandLine line, out string realm, out int viewId, out string error)
    {
        realm = ""; viewId = 0; error = "";
        var typed = Arg(line, 1);
        var resolved = _rooms.ResolveName(typed);
        if (resolved is null) { error = $"no such realm: {typed}"; return false; }
        realm = resolved;
        if (!int.TryParse(Arg(line, 2), out viewId)) { error = "viewId must be a number."; return false; }
        if (_worlds.Find(realm)?.Knows(viewId) != true)
        {
            error = $"view not found: {realm}#view{viewId} (the server has not observed that entity)";
            return false;
        }
        return true;
    }

    /// <summary>Builds a PUN RPC (event 200) the same way the playerbots do — viewId/method/args under the
    /// RPC payload hashtable. Mirrors <c>HunterBehavior.Rpc</c> / <c>GameActions.Rpc</c>.</summary>
    private static EventData Rpc(int viewId, string method, object[] args) =>
        new(PhotonCodes.PunEvent.Rpc, new()
        {
            { PhotonCodes.Param.Code, PhotonCodes.PunEvent.Rpc },
            { PhotonCodes.Param.Data, new Dictionary<object, object>
                {
                    { PhotonCodes.RpcKey.ViewId, viewId },
                    { PhotonCodes.RpcKey.MethodName, method },
                    { PhotonCodes.RpcKey.Args, args },
                } },
        });

    private static string Arg(CommandLine line, int index) => index < line.Parts.Count ? line.Parts[index] : "";
}
