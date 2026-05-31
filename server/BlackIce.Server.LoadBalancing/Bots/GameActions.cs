using System.Buffers.Binary;
using System.Collections.Generic;
using BlackIce.Photon;

namespace BlackIce.Server.LoadBalancing.Bots;

/// <summary>One scripted bot action: a human label, whether it's a deliberate cheat, and the event(s)
/// it relays. Bursts (e.g. a flood) carry many events.</summary>
public readonly record struct BotAction(string Label, bool Cheat, IReadOnlyList<EventData> Events);

/// <summary>
/// Builds a rotating script of networked game actions for a playerbot to exercise the server's relay
/// and authority/anti-cheat surface — a mix of legitimate traffic and deliberate cheats. RPC names,
/// event codes and the DamagePacket layout are taken from the project's protocol recon
/// (docs/protocol). Damage is written big-endian to match the server's PunRpcInfo decoder; the
/// DamagePacket "combined" bitfield (byte 39, big-endian LSB) carries Crit=bit0 / WeakPoint=bit1 —
/// WeakPoint being Black Ice's "headshot" equivalent.
/// </summary>
public static class GameActions
{
    private const byte EnemyDamageRpcView = 5;     // a scene object (block 0): a shared enemy/world target

    /// <summary>The per-bot action script. Cycled one entry per tick; over a full pass it covers the surface.</summary>
    public static IReadOnlyList<BotAction> Script(PlayerBot bot)
    {
        int ownView = bot.ViewId;
        int foreignView = (bot.Actor + 1) * 1000 + 5;   // a viewID in ANOTHER actor's block (ownership spoof)

        return new List<BotAction>
        {
            // --- legitimate gameplay ---------------------------------------------------------------
            One("chat", false, Rpc(ownView, "ReceiveChatMessage", $"bot {bot.Actor}: gg")),
            One("damage-enemy", false, Rpc(EnemyDamageRpcView, "TakeDamage", EnemyDamageRpcView, DamagePacket(25f))),
            One("change-color", false, Rpc(ownView, "ChangeColor", Color(0.2f, 0.6f, 1f, 1f))),
            One("equip/refresh-model", false, Rpc(ownView, "RefreshModel", ownView)),
            One("add-buff", false, Rpc(ownView, "AddBuffRPC", 1, 1, 30f, 0, 1.5f, 0)),
            One("temp-hp", false, Rpc(ownView, "AddTempHP", 25, 10)),
            One("hack-setup", false, Rpc(EnemyDamageRpcView, "SetupHack", 100, Vec3(5, 0, 5), 30f, 0, 0, 50f)),
            One("hack-finish", false, Rpc(EnemyDamageRpcView, "FinishEndingHack", 100, 1, 0)),
            One("npc-spawn", false, Instantiate("Enemy", foreignBlockSafeEnemyView(bot))),
            One("loot-lock", false, Rpc(EnemyDamageRpcView, "GetLock")),
            One("position", false, Position(ownView, 1f, 0f, 1f)),

            // --- deliberate cheats (flagged by the authority interceptors) -------------------------
            One("CHEAT over-max-damage", true, Rpc(EnemyDamageRpcView, "TakeDamage", EnemyDamageRpcView, DamagePacket(999_999f))),
            One("CHEAT nan-damage", true, Rpc(EnemyDamageRpcView, "TakeDamage", EnemyDamageRpcView, DamagePacket(float.NaN))),
            One("CHEAT view-spoof", true, Rpc(foreignView, "TakeDamage", foreignView, DamagePacket(50f))),
            One("CHEAT teleport", true, Position(ownView, 99_999f, 0f, 99_999f)),
            One("CHEAT nan-position", true, Position(ownView, float.NaN, 0f, 0f)),
            One("CHEAT instant-loot", true, Rpc(EnemyDamageRpcView, "SetItemRPC", "Legendary_Rifle_Godroll")),
            One("CHEAT xp-farm", true, Rpc(ownView, "AddXPRPC", int.MaxValue)),
            Burst("CHEAT headshot-flood", true, 12, _ => Rpc(EnemyDamageRpcView, "TakeDamage", EnemyDamageRpcView, DamagePacket(40f, weakPoint: true))),
            Burst("CHEAT hit-rate-flood", true, 40, _ => Rpc(EnemyDamageRpcView, "TakeDamage", EnemyDamageRpcView, DamagePacket(10f))),
            Burst("CHEAT event-flood", true, 220, i => Rpc(ownView, "ReceiveChatMessage", $"spam {i}")),
        };
    }

    private static BotAction One(string label, bool cheat, EventData ev) => new(label, cheat, new[] { ev });

    private static BotAction Burst(string label, bool cheat, int count, System.Func<int, EventData> make)
    {
        var evs = new EventData[count];
        for (int i = 0; i < count; i++) evs[i] = make(i);
        return new BotAction(label, cheat, evs);
    }

    // viewID in the bot's OWN block (legit spawn target), distinct from its avatar's +1 slot.
    private static int foreignBlockSafeEnemyView(PlayerBot bot) => bot.Actor * 1000 + 2;

    // --- event builders --------------------------------------------------------------------------

    private static EventData Rpc(int viewId, string method, params object[] args) =>
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

    private static EventData Instantiate(string prefab, int viewId) =>
        new(PhotonCodes.PunEvent.Instantiation, new()
        {
            { PhotonCodes.Param.Data, new Dictionary<object, object>
                {
                    { PhotonCodes.InstantiationKey.PrefabName, prefab },
                    { PhotonCodes.InstantiationKey.ServerTime, System.Environment.TickCount },
                    { PhotonCodes.InstantiationKey.ViewId, viewId },
                } },
        });

    private static EventData Position(int viewId, float x, float y, float z)
    {
        var view = new object[] { viewId, false, null!, Vec3(x, y, z), Quat(), 0f, 200f, 0f, 0f };
        var batch = new object[] { System.Environment.TickCount, null!, view };
        return new EventData(PhotonCodes.PunEvent.SendSerialize, new() { { PhotonCodes.Param.Data, batch } });
    }

    /// <summary>41-byte DamagePacket. Damage is a big-endian float (the server's PunRpcInfo convention);
    /// the combined bitfield (Crit=bit0, WeakPoint=bit1) sits in byte 39 (big-endian LSB).</summary>
    private static PhotonCustomData DamagePacket(float damage, bool crit = false, bool weakPoint = false)
    {
        var b = new byte[41];
        BinaryPrimitives.WriteSingleBigEndian(b.AsSpan(0), damage);
        b[39] = (byte)((crit ? 0x01 : 0) | (weakPoint ? 0x02 : 0));
        return new PhotonCustomData(PhotonCodes.CustomType.DamagePacket, b);
    }

    private static PhotonCustomData Vec3(float x, float y, float z)
    {
        var b = new byte[12];
        BinaryPrimitives.WriteSingleBigEndian(b.AsSpan(0), x);
        BinaryPrimitives.WriteSingleBigEndian(b.AsSpan(4), y);
        BinaryPrimitives.WriteSingleBigEndian(b.AsSpan(8), z);
        return new PhotonCustomData(PhotonCodes.CustomType.Vector3, b);
    }

    private static PhotonCustomData Quat()
    {
        var b = new byte[16];
        BinaryPrimitives.WriteSingleBigEndian(b.AsSpan(12), 1f);   // identity (x=y=z=0, w=1)
        return new PhotonCustomData(PhotonCodes.CustomType.Quaternion, b);
    }

    private static PhotonCustomData Color(float r, float g, float b, float a)
    {
        var bytes = new byte[16];
        BinaryPrimitives.WriteSingleBigEndian(bytes.AsSpan(0), r);
        BinaryPrimitives.WriteSingleBigEndian(bytes.AsSpan(4), g);
        BinaryPrimitives.WriteSingleBigEndian(bytes.AsSpan(8), b);
        BinaryPrimitives.WriteSingleBigEndian(bytes.AsSpan(12), a);
        return new PhotonCustomData(PhotonCodes.CustomType.Color, bytes);
    }
}
