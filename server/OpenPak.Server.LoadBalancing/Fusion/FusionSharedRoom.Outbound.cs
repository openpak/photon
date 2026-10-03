using OpenPak.Server.Core;

namespace OpenPak.Server.LoadBalancing.Fusion;

// Building one packet per connection per send round (spec §8.3) and the delivered/lost feedback (§8.4).
public sealed partial class FusionSharedRoom
{
    /// <summary>The message block stops growing once the packet passes 9088 bits from the start of the
    /// notify datagram (spec §4.3), i.e. this many payload bits.</summary>
    private const int MessageBudgetBits = 9088 - FusionRelayCodec.NotifyHeaderSize * 8;
    private const int MaxRecordsPerPacket = 255, MaxDestroysPerPacket = 32, MaxPlayerDataPerPacket = 5;

    private enum WriteResult { Written, Nothing, DidNotFit }

    private void SendPacket(FusionSimPlayer player)
    {
        if (!player.Connection.CanSendNotify) return;
        // A connection silent for a second gets at most two packets a second (spec §10.2).
        if (_now - player.LastReceived >= 1 && _now - player.LastPacketSent < 0.5) return;

        var packet = new SentPacket(_round, _tick);
        var writer = new NetBitWriter(256);
        writer.WriteBits(0, FusionSimCodec.HeaderBits);

        writer.PadToByteBoundary();
        var messages = WriteMessages(player, writer, packet);
        writer.PadToByteBoundary();

        FusionSimCodec.WriteTimeFeedback(writer, player.Offsets.Average, player.Offsets.Deviation,
                                         player.RecvDeltas.Average, player.RecvDeltas.Deviation);

        var destroys = Math.Min(player.PendingDestroys.Count, MaxDestroysPerPacket);
        for (var i = 0; i < destroys; i++)
        {
            var id = player.PendingDestroys[i];
            writer.WriteUInt32VarLength(id, 8);
            player.Objects[id].Status = ObjectSendStatus.DestroyPending;
            packet.Destroys.Add(id);
        }
        player.PendingDestroys.RemoveRange(0, destroys);

        var records = WriteRecords(player, writer, packet);
        FusionSimCodec.PatchHeader(writer, 0, messages, records, destroys, _tick);

        _peer.TrySendNotify(player.Connection, writer, packet);
        player.LastPacketSent = _now;
    }

    /// <summary>Messages from the head of the queue (§12.4), stopping at the budget, at 255, or at an RPC
    /// whose object this connection has not confirmed yet (§12.3: a client drops an RPC for an object it
    /// does not have, and reads messages before the records of the same packet).</summary>
    private int WriteMessages(FusionSimPlayer player, NetBitWriter writer, SentPacket packet)
    {
        var count = 0;
        while (player.Outgoing.First is { } node && count < 255)
        {
            if (count > 0 && writer.BitCount > MessageBudgetBits) break;
            var queued = node.Value;
            if (WaitsForObject(player, queued.Message)) break;
            FusionSimCodec.WriteMessage(writer, queued.Message, queued.Tick, queued.Sequence);
            player.Outgoing.RemoveFirst();
            if (queued.Message.IsReliable) packet.Messages.Add(queued);
            count++;
        }
        return count;
    }

    private static bool WaitsForObject(FusionSimPlayer player, SimMessage message)
    {
        const SimMessageFlags notRpc = SimMessageFlags.UserMessage | SimMessageFlags.Static | SimMessageFlags.Internal;
        if ((message.Flags & notRpc) != 0 || message.Payload.Length < 4) return false;
        var id = BitConverter.ToUInt32(message.Payload, 0);
        return player.Objects.TryGetValue(id, out var state) && state.Status == ObjectSendStatus.CreatedUnconfirmed;
    }

    /// <summary>Structs first, then up to five PlayerData, then client objects in rotating order (§8.3).</summary>
    private int WriteRecords(FusionSimPlayer player, NetBitWriter writer, SentPacket packet)
    {
        uint previousId = 0;
        var count = 0;
        foreach (var global in (ReadOnlySpan<FusionObject>)[_runtimeConfig, _sceneInfo, _physicsInfo])
            if (WriteObject(player, global, writer, packet, ref previousId) == WriteResult.Written) count++;

        var playerDataWritten = 0;
        for (var i = 0; i < _playerData.Count && playerDataWritten < MaxPlayerDataPerPacket; i++)
        {
            var index = (player.PlayerDataCursor + i) % _playerData.Count;
            if (WriteObject(player, _playerData[index], writer, packet, ref previousId) != WriteResult.Written) continue;
            count++;
            playerDataWritten++;
            player.PlayerDataCursor = index + 1;
        }

        var attempted = 0;
        for (var i = 0; i < _worldObjects.Count && count < MaxRecordsPerPacket; i++)
        {
            var index = (player.ObjectCursor + i) % _worldObjects.Count;
            var result = WriteObject(player, _worldObjects[index], writer, packet, ref previousId);
            if (result == WriteResult.Nothing) continue;
            if (result == WriteResult.DidNotFit)
            {
                // Start the next packet here; past it if it could not fit even first in line.
                player.ObjectCursor = attempted == 0 ? index + 1 : index;
                if (attempted == 0) Log.Warn(Cat, $"\"{Name}\" object {_worldObjects[index].Id} ({_worldObjects[index].WordCount} words) does not fit a packet");
                break;
            }
            attempted++;
            count++;
        }
        return count;
    }

    /// <summary>One record for <paramref name="obj"/> if this connection has anything unsent for it (§8.3).</summary>
    private WriteResult WriteObject(FusionSimPlayer player, FusionObject obj, NetBitWriter writer, SentPacket packet, ref uint previousId)
    {
        if (!player.Objects.TryGetValue(obj.Id, out var state)
            || state.Status is not (ObjectSendStatus.CreatedUnconfirmed or ObjectSendStatus.CreatedConfirmed)
            || obj.ChangedStamp <= state.Sent)
            return WriteResult.Nothing;

        var mark = writer.BitCount;
        var full = state.Status == ObjectSendStatus.CreatedUnconfirmed;
        writer.WriteUInt32VarLength(FusionSimCodec.ZigZag32((int)(obj.Id - previousId)), 6);
        // Without area of interest: InAreaOfInterest (1) for every object, plus ForceInterest (2) for a
        // GlobalObjectInterest one; 0 for structs (§14). Constant for the object's life.
        var interest = obj.IsStruct ? 0 : (obj.Flags & FusionObject.FlagGlobalObjectInterest) != 0 ? 3 : 1;
        // The state authority gets its own changes echoed, as from the SDK: it ignores them, and on losing
        // authority it falls back to its last received snapshot, which must not be stale (§9.2).
        var words = obj.WriteRecordBody(writer, full, state.Acked, interest);

        if (!full && words == 0)
        {
            writer.Rewind(mark);
            state.Sent = _round;
            return WriteResult.Nothing;
        }
        if (writer.ByteCount > FusionRelayCodec.MaxNotifyPayloadBytes)
        {
            writer.Rewind(mark);
            return WriteResult.DidNotFit;
        }
        packet.Objects.Add((obj.Id, state.Sent, full));
        state.Sent = _round;
        previousId = obj.Id;
        return WriteResult.Written;
    }

    /// <summary>The transport resolved a packet we sent (§8.4).</summary>
    private void OnPacketResolved(FusionRelayConnection connection, object? token, bool delivered)
    {
        if (token is not SentPacket packet || !_players.TryGetValue(connection.Actor, out var player)) return;
        if (delivered)
        {
            foreach (var (id, _, full) in packet.Objects)
            {
                if (!player.Objects.TryGetValue(id, out var state) || state.Status is not (ObjectSendStatus.CreatedUnconfirmed or ObjectSendStatus.CreatedConfirmed)) continue;
                state.Acked = Math.Max(state.Acked, packet.Stamp);
                if (full) state.Status = ObjectSendStatus.CreatedConfirmed;
            }
            foreach (var id in packet.Destroys)
                if (player.Objects.TryGetValue(id, out var state) && state.Status == ObjectSendStatus.DestroyPending)
                    player.Objects.Remove(id);
            player.LastAckedTick = Math.Max(player.LastAckedTick, packet.Tick);
            return;
        }

        foreach (var (id, previousSent, _) in packet.Objects)
        {
            if (!player.Objects.TryGetValue(id, out var state) || state.Status is not (ObjectSendStatus.CreatedUnconfirmed or ObjectSendStatus.CreatedConfirmed)) continue;
            state.Sent = Math.Min(state.Sent, previousSent);
            state.Acked = Math.Min(state.Acked, previousSent);
        }
        foreach (var id in packet.Destroys)
        {
            if (!player.Objects.TryGetValue(id, out var state) || state.Status != ObjectSendStatus.DestroyPending) continue;
            state.Status = ObjectSendStatus.DestroyUnconfirmed;
            player.PendingDestroys.Add(id);
        }
        // Lost reliable messages go back to the head of the queue in their original order and sequences.
        for (var i = packet.Messages.Count - 1; i >= 0; i--) player.Outgoing.AddFirst(packet.Messages[i]);
        if (Log.Enabled(LogLevel.Trace)) Log.Trace(Cat, $"\"{Name}\" packet {packet.Stamp} to actor {player.Actor} lost: {packet.Objects.Count} records, {packet.Messages.Count} messages requeued");
    }
}
