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
    private const byte EvJoin = 255, EvInstantiate = 202, EvRpc = 200, EvPropertiesChanged = 253;
    private const byte PActorNr = 254, PActorList = 252, PProperties = 251, PTargetActorNr = 253, PData = 245;

    public int Actor { get; }
    public int ViewId { get; }
    public BotIdentity Identity { get; }

    public PlayerBot(int actor, BotIdentity identity)
    {
        Actor = actor; Identity = identity; ViewId = actor * 1000 + 1;
    }

    /// <summary>Emits join → identity properties → avatar instantiate → model-refresh, to the room's real players.</summary>
    public void Spawn(RoomSession session)
    {
        // 1) Join (255): announce the new actor.
        session.RelayFrom(Actor, new EventData(EvJoin, new() { { PActorNr, Actor } }));

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
        session.RelayFrom(Actor, new EventData(EvPropertiesChanged, new()
        {
            { PProperties, props },
            { PTargetActorNr, Actor },
        }));

        // 3) Instantiate the avatar (202): prefab "Player", our viewID, cached so late joiners see it.
        //    Key 6 is the server timestamp: PUN's NetworkInstantiate casts networkEvent[(byte)6] to
        //    int unconditionally, so omitting it NREs the client and nothing spawns.
        session.RelayFrom(Actor, new EventData(EvInstantiate, new()
        {
            { PData, new Dictionary<object, object>
                {
                    { (byte)0, "Player" },
                    { (byte)6, System.Environment.TickCount },
                    { (byte)7, ViewId },
                } },
        }));

        // 4) RefreshModel RPC (200): nudge clients to pull appearance from the props set in step 2.
        session.RelayFrom(Actor, new EventData(EvRpc, new()
        {
            { (byte)244, (byte)200 },
            { PData, new Dictionary<object, object>
                {
                    { (byte)0, ViewId },
                    { (byte)3, "RefreshModel" },
                    // PunRPC signature is RefreshModel(int playerViewID); an empty arg list never invokes it.
                    { (byte)4, new object[] { ViewId } },
                } },
        }));
    }

    /// <summary>PUN Color custom type (code 67): four big-endian floats (r,g,b,a).</summary>
    private static PhotonCustomData Color(float[] rgba)
    {
        var b = new byte[16];
        for (int i = 0; i < 4; i++)
            System.Buffers.Binary.BinaryPrimitives.WriteSingleBigEndian(b.AsSpan(i * 4), rgba[i]);
        return new PhotonCustomData(67, b);
    }
}
