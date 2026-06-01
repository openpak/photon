using System.Collections.Generic;
using BlackIce.Photon;

namespace BlackIce.Server.LoadBalancing.Bots;

/// <summary>
/// A server-originated synthetic player. Owns a reserved actor number and viewID and emits the same
/// event sequence a real client sends after joining, fanned out to real players via the room session
/// (the bot is not a session member, so RelayFrom reaches every real actor). Mod-free: the unmodified
/// client renders it as a normal player. See docs/superpowers/specs Phase 2 Background for the recon.
/// </summary>
public sealed class PlayerBot
{
    public int Actor { get; }
    public int ViewId { get; }
    public BotIdentity Identity { get; }

    public PlayerBot(int actor, BotIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        // ViewId = actor * 1000 + 1 must not overflow int (PUN's MAX_VIEW_IDS block scheme).
        if (actor <= 0 || actor > (int.MaxValue - 1) / 1000)
            throw new ArgumentOutOfRangeException(nameof(actor), actor, "bot actor number out of viewID range");
        Actor = actor; Identity = identity; ViewId = actor * 1000 + 1;
    }

    /// <summary>Emits join → identity properties → avatar instantiate → model-refresh, to the room's real
    /// players. The avatar is instantiated AT <paramref name="x"/>,<paramref name="y"/>,<paramref name="z"/>
    /// (carried in the 202's position slot) so the client renders each bot at its own spot — without a
    /// position the client spawns every bot at world origin, which looks like a single stacked bot.</summary>
    public void Spawn(RoomSession session, float x = 0f, float y = 0f, float z = 0f)
    {
        // 1) Join (255): announce the new actor.
        session.RelayFrom(Actor, new EventData(PhotonCodes.Event.Join, new() { { PhotonCodes.Param.ActorNr, Actor } }));

        // 2) Identity as a properties-changed (253): the client reads appearance from these.
        var props = new Dictionary<object, object>
        {
            { "PlayerLevel", Identity.Level },
            { "Cheater", false },
            { "ViralAchievement", false },
            { "PlayerModelIndex", Identity.ModelIndex },
            { "BackHoloIconIndex", 0 },
            { "BackHoloModdedKey", "" },
            { "Team", Identity.Team },
            // Model colors are PUN Color custom types (code 67); encoded as raw RGBA float bytes.
            { "ModelMainColor", Color(Identity.ModelColors[0]) },
            { "ModelSecondaryColor", Color(Identity.ModelColors[1]) },
            { "ModelTertiaryColor", Color(Identity.ModelColors[2]) },
            { "ModelQuaternaryColor", Color(Identity.ModelColors[3]) },
        };
        session.RelayFrom(Actor, new EventData(PhotonCodes.Event.PropertiesChanged, new()
        {
            { PhotonCodes.Param.Properties, props },
            { PhotonCodes.Param.TargetActorNr, Actor },
        }));

        // 3) Instantiate the avatar (202): prefab "Player", our viewID, cached so late joiners see it.
        //    Key 6 is the server timestamp: PUN's NetworkInstantiate casts networkEvent[(byte)6] to
        //    int unconditionally, so omitting it NREs the client and nothing spawns.
        session.RelayFrom(Actor, new EventData(PhotonCodes.PunEvent.Instantiation, new()
        {
            { PhotonCodes.Param.Data, new Dictionary<object, object>
                {
                    { PhotonCodes.InstantiationKey.PrefabName, "Player" },
                    // Position (key 1) + rotation (key 2): PUN instantiates the prefab here. Omitting them
                    // spawns every bot at origin (they visually merge into one). Vec3/Quat are PUN custom
                    // types (big-endian floats), the same encoding the real client's spawns use.
                    { PhotonCodes.InstantiationKey.Position, Vec3(x, y, z) },
                    { PhotonCodes.InstantiationKey.Rotation, Quat() },
                    { PhotonCodes.InstantiationKey.ServerTime, System.Environment.TickCount },
                    { PhotonCodes.InstantiationKey.ViewId, ViewId },
                } },
        }));

        // 4) RefreshModel RPC (200): nudge clients to pull appearance from the props set in step 2.
        session.RelayFrom(Actor, new EventData(PhotonCodes.PunEvent.Rpc, new()
        {
            { PhotonCodes.Param.Code, PhotonCodes.PunEvent.Rpc },
            { PhotonCodes.Param.Data, new Dictionary<object, object>
                {
                    { PhotonCodes.RpcKey.ViewId, ViewId },
                    { PhotonCodes.RpcKey.MethodName, "RefreshModel" },
                    // PunRPC signature is RefreshModel(int playerViewID); an empty arg list never invokes it.
                    { PhotonCodes.RpcKey.Args, new object[] { ViewId } },
                } },
        }));
    }

    /// <summary>PUN Color custom type (code 67): four big-endian floats (r,g,b,a).</summary>
    private static PhotonCustomData Color(float[] rgba)
    {
        var b = new byte[16];
        for (int i = 0; i < 4; i++)
            System.Buffers.Binary.BinaryPrimitives.WriteSingleBigEndian(b.AsSpan(i * 4), rgba[i]);
        return new PhotonCustomData(PhotonCodes.CustomType.Color, b);
    }

    /// <summary>PUN Vector3 custom type (code 86): three big-endian floats (x,y,z).</summary>
    private static PhotonCustomData Vec3(float x, float y, float z)
    {
        var b = new byte[12];
        System.Buffers.Binary.BinaryPrimitives.WriteSingleBigEndian(b.AsSpan(0), x);
        System.Buffers.Binary.BinaryPrimitives.WriteSingleBigEndian(b.AsSpan(4), y);
        System.Buffers.Binary.BinaryPrimitives.WriteSingleBigEndian(b.AsSpan(8), z);
        return new PhotonCustomData(PhotonCodes.CustomType.Vector3, b);
    }

    /// <summary>PUN Quaternion custom type (code 81): identity rotation (x=y=z=0, w=1), big-endian floats.</summary>
    private static PhotonCustomData Quat()
    {
        var b = new byte[16];
        System.Buffers.Binary.BinaryPrimitives.WriteSingleBigEndian(b.AsSpan(12), 1f);
        return new PhotonCustomData(PhotonCodes.CustomType.Quaternion, b);
    }
}
