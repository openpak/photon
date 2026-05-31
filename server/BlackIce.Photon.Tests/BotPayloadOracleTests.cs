using BlackIce.Photon;
using Xunit;
using PhotonEventData = ExitGames.Client.Photon.EventData;
using PhotonHashtable = ExitGames.Client.Photon.Hashtable;

namespace BlackIce.Photon.Tests;

/// <summary>
/// Interop oracle coverage for the fabricated player-bot events. The structural bot tests in
/// BlackIce.Server.Tests assert our own shape; these prove the REAL Photon3Unity3D codec parses what we
/// emit. The bot types live in LoadBalancing (no DLL reference), so we rebuild the exact payloads the bot
/// constructs inline — the same EventData PlayerBot.Spawn / BotManager.BuildPositionEvent produce — and
/// round-trip them through the oracle. Custom types 86 (Vector3) and 81 (Quaternion) are registered in
/// the OracleFixture passthrough list.
/// </summary>
public class BotPayloadOracleTests
{
    private const byte PData = 245;

    private static PhotonCustomData PunVector3(float x, float y, float z)
    {
        var b = new byte[12];
        System.Buffers.Binary.BinaryPrimitives.WriteSingleBigEndian(b.AsSpan(0), x);
        System.Buffers.Binary.BinaryPrimitives.WriteSingleBigEndian(b.AsSpan(4), y);
        System.Buffers.Binary.BinaryPrimitives.WriteSingleBigEndian(b.AsSpan(8), z);
        return new PhotonCustomData(86, b);
    }

    private static PhotonCustomData PunQuaternion(float x, float y, float z, float w)
    {
        var b = new byte[16];
        System.Buffers.Binary.BinaryPrimitives.WriteSingleBigEndian(b.AsSpan(0), x);
        System.Buffers.Binary.BinaryPrimitives.WriteSingleBigEndian(b.AsSpan(4), y);
        System.Buffers.Binary.BinaryPrimitives.WriteSingleBigEndian(b.AsSpan(8), z);
        System.Buffers.Binary.BinaryPrimitives.WriteSingleBigEndian(b.AsSpan(12), w);
        return new PhotonCustomData(81, b);
    }

    [Fact]
    public void Client_parses_our_bot_instantiate_event_with_prefab_viewid_and_timestamp()
    {
        const int viewId = 2001;
        // Mirrors PlayerBot.Spawn's instantiate(202): payload dict under 245 with prefab(0),
        // server timestamp(6) and viewID(7). Key 6 is the defect-A fix the client casts unconditionally.
        var ours = MessageSerializer.SerializeEvent(new EventData(202, new()
        {
            { PData, new Dictionary<object, object>
                {
                    { (byte)0, "Player" },
                    { (byte)6, System.Environment.TickCount },
                    { (byte)7, viewId },
                } },
        }));

        var ev = (PhotonEventData)Oracle.DeserializeMessage(ours);
        Assert.Equal(202, ev.Code);

        var payload = Assert.IsAssignableFrom<PhotonHashtable>(ev.Parameters[PData]);
        Assert.Equal("Player", payload[(byte)0]);
        Assert.IsType<int>(payload[(byte)6]);                 // timestamp present (key 6)
        Assert.Equal(viewId, Assert.IsType<int>(payload[(byte)7]));
    }

    [Fact]
    public void Client_parses_our_bot_position_event_without_throwing()
    {
        const int viewId = 2001;
        var pos = PunVector3(1f, 2f, 3f);
        var rot = PunQuaternion(0f, 0f, 0f, 1f);
        // Mirrors BotManager.BuildPositionEvent: per-view stream carries pos, rot, then the confirmed
        // body-component fields (damageTaken, maxHealth, tempHP, headPitch) — the defect-B fix.
        var view = new object[] { viewId, false, null!, pos, rot, 0f, 200f, 0f, 0f };
        var batch = new object[] { System.Environment.TickCount, null!, view };
        var ours = MessageSerializer.SerializeEvent(new EventData(201, new() { { PData, batch } }));

        // The real codec must decode the whole batch without throwing.
        var ev = (PhotonEventData)Oracle.DeserializeMessage(ours);
        Assert.Equal(201, ev.Code);

        var decodedBatch = Assert.IsType<object[]>(ev.Parameters[PData]);
        var decodedView = Assert.IsType<object[]>(decodedBatch[2]);
        // viewId + flag + null + pos + rot + four body floats = 9 elements.
        Assert.Equal(9, decodedView.Length);
        Assert.Equal(viewId, Assert.IsType<int>(decodedView[0]));
    }
}
