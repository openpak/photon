using OpenPak.Server.Core;

namespace OpenPak.Server.LoadBalancing.Fusion;

// Applying what a client sends (spec §9): header, messages, time feedback input, destroys, object records.
public sealed partial class FusionSharedRoom
{
    /// <summary>
    /// The room-memory bound of spec §15 duty 3, in place of the SDK's per-object heap limit. A client
    /// object costs 8 bytes a word (value + stamp) whatever its record carried, so a full record of a few
    /// bits can claim 256 KB: each creator gets a quota and the room a total. Sized from Outbound, whose
    /// biggest object is 9268 words (74 KB): one player may hold 200 of those.
    /// </summary>
    private const long MaxCreatorBytes = 16L << 20, MaxRoomBytes = 64L << 20;
    private const int MaxCreatorObjects = 4096, MaxRoomObjects = 16_384;
    private const int ObjectOverheadBytes = 64;
    /// <summary>Reliable messages a client may have waiting behind a sequence gap before it is dropped.</summary>
    private const int MaxPendingInbound = 1024;
    /// <summary>Destroyed transform-object ids remembered so late records for them still parse (§4.7).</summary>
    private const int MaxRememberedTransformIds = 65_536;

    private long _roomBytes;
    /// <summary>Bytes and objects each actor's live creations hold (quota of spec §15 duty 3).</summary>
    private readonly Dictionary<int, (long Bytes, int Objects)> _usage = new();

    private static long CostOf(int wordCount) => 8L * wordCount + ObjectOverheadBytes;
    private readonly HashSet<uint> _destroyedTransformIds = new();
    private readonly List<(int Index, int Value)> _recordWords = new();
    private readonly List<SimMessage> _packetMessages = new();

    private void Charge(FusionObject obj, int sign)
    {
        var cost = sign * CostOf(obj.WordCount);
        _roomBytes += cost;
        var used = _usage.GetValueOrDefault(obj.Creator);
        _usage[obj.Creator] = (used.Bytes + cost, used.Objects + sign);
    }

    private void RememberTransformId(uint id)
    {
        // GAP: forgetting them all at the cap lets a record for a long-gone transform object desync the
        // rest of its packet (the packet is then dropped) — revisit if rooms ever destroy that many.
        if (_destroyedTransformIds.Count >= MaxRememberedTransformIds) _destroyedTransformIds.Clear();
        _destroyedTransformIds.Add(id);
    }

    private void OnNotifyReceived(FusionRelayConnection connection, NetBitReader reader)
    {
        if (!_players.TryGetValue(connection.Actor, out var player)) return;
        _packetMessages.Clear();
        try
        {
            ReadPacket(player, reader);
        }
        catch (Exception ex) when (ex is EndOfStreamException or InvalidDataException)
        {
            // Records already applied stay applied, as in the SDK; the rest of the packet is lost.
            _malformed++;
            Log.Debug(Cat, $"\"{Name}\" malformed packet from actor {player.Actor} at bit {reader.BitPosition}: {ex.Message}");
        }
        // Messages are handled after the packet's records, so one may refer to an object created beside it.
        foreach (var message in _packetMessages) AcceptMessage(player, message);
        _packetMessages.Clear();
    }

    private void ReadPacket(FusionSimPlayer player, NetBitReader reader)
    {
        var (messages, inputs, updates, destroys, clientTick) = FusionSimCodec.ReadHeader(reader);
        if (inputs != 0) throw new InvalidDataException($"{inputs} inputs in a Shared-mode packet");

        reader.PadToByteBoundary();
        for (var i = 0; i < messages; i++) _packetMessages.Add(FusionSimCodec.ReadMessage(reader));
        reader.PadToByteBoundary();

        RecordClientTick(player, clientTick);

        for (var i = 0; i < destroys; i++) DestroyFromClient(player, reader.ReadUInt32VarLength(8));

        uint previousId = 0;
        for (var i = 0; i < updates; i++) ReadRecord(player, reader, ref previousId);
    }

    /// <summary>Time-feedback samples for one packet carrying client tick <paramref name="clientTick"/> (§10.3).</summary>
    private void RecordClientTick(FusionSimPlayer player, int clientTick)
    {
        if (_tick - player.LastAckedTick > TickRate)
        {
            player.Offsets.Clear();
            player.RecvDeltas.Clear();
            player.LastArrival = double.NaN;
        }
        if (clientTick > player.LastClientTick)
        {
            player.LastClientTick = clientTick;
            player.Offsets.Add(TickTime(clientTick) - _arrival);
        }
        // The first interval (also after a clear) is the nominal one; one under 1 ms is not an interval.
        if (double.IsNaN(player.LastArrival))
        {
            player.RecvDeltas.Add(1.0 / ClientSendRate);
            player.LastArrival = _arrival;
        }
        else if (_arrival - player.LastArrival >= 0.001)
        {
            player.RecvDeltas.Add(_arrival - player.LastArrival);
            player.LastArrival = _arrival;
        }
    }

    private void DestroyFromClient(FusionSimPlayer player, uint id)
    {
        if (!_objects.TryGetValue(id, out var obj) || obj.IsStruct || id < FirstServerId) return;
        if (ResolveAuthority(obj.StateAuthority) != player.PlayerRaw)
        {
            Log.Debug(Cat, $"\"{Name}\" actor {player.Actor} may not destroy {id} (authority {obj.StateAuthority})");
            return;
        }
        Destroy(obj);
        Log.Debug(Cat, $"\"{Name}\" actor {player.Actor} destroyed {id}");
    }

    /// <summary>One object record (spec §4.6, §9.2), parsed in full before anything is applied.</summary>
    private void ReadRecord(FusionSimPlayer player, NetBitReader reader, ref uint previousId)
    {
        var id = (uint)((int)previousId + FusionSimCodec.UnZigZag32(reader.ReadUInt32VarLength(6)));
        previousId = id;
        var full = reader.ReadBoolean();
        int[]? header = null;
        if (full)
        {
            header = new int[FusionObject.HeaderWords];
            for (var index = 1; index < FusionObject.HeaderWords; index++)
                if (reader.ReadBoolean()) header[index] = FusionSimCodec.ReadWord(reader);
        }

        _objects.TryGetValue(id, out var obj);
        int wordCount;
        bool transform;
        if (obj is not null) (wordCount, transform) = (obj.WordCount, obj.HasTransform);
        else if (header is not null)
        {
            wordCount = (short)header[FusionObject.WordCountWord];
            transform = FusionObject.HasTransformBlock(header[FusionObject.FlagsWord], wordCount);
        }
        else (wordCount, transform) = (int.MaxValue, _destroyedTransformIds.Contains(id));

        var valid = ReadChangedWords(reader, full ? FusionObject.HeaderWords - 1 : 0, wordCount, transform);
        if (!valid)
        {
            Log.Debug(Cat, $"\"{Name}\" actor {player.Actor} record for {id} has a word outside {wordCount}: discarded");
            return;
        }

        if (obj is null)
        {
            if (header is not null) CreateFromClient(player, id, header, wordCount);
            return;   // a delta for an object we do not have is skipped (§4.6)
        }
        if (obj.IsStruct)
        {
            // Clients echo every struct (§9.3). Only SceneInfo/PhysicsInfo take data, and only from their authority.
            if (obj != _sceneInfo && obj != _physicsInfo || ResolveAuthority(obj.StateAuthority) != player.PlayerRaw) return;
            foreach (var (index, value) in _recordWords)
                if (index >= FusionObject.HeaderWords) obj.Set(index, value, NextStamp);
            return;
        }
        if (ResolveAuthority(obj.StateAuthority) != player.PlayerRaw)
        {
            if (Log.Enabled(LogLevel.Trace)) Log.Trace(Cat, $"\"{Name}\" actor {player.Actor} is not the authority of {id} ({obj.StateAuthority}): record dropped");
            return;
        }
        if (header is not null)
        {
            obj.Set(FusionObject.InputAuthorityWord, header[FusionObject.InputAuthorityWord], NextStamp);
            for (var index = 10; index < FusionObject.HeaderWords; index++) obj.Set(index, header[index], NextStamp);
        }
        foreach (var (index, value) in _recordWords)
            if (index == FusionObject.InputAuthorityWord || index >= 10) obj.Set(index, value, NextStamp);
    }

    /// <summary>Reads the changed-word list into <see cref="_recordWords"/>. False when an index fell outside
    /// the object, in which case the whole record is to be discarded (the stream stays in step).</summary>
    private bool ReadChangedWords(NetBitReader reader, int cursor, int wordCount, bool transform)
    {
        _recordWords.Clear();
        long index = cursor;
        var valid = true;
        while (true)
        {
            var delta = reader.ReadUInt64VarLength(4);
            if (delta == 0) return valid;
            index = delta > int.MaxValue ? long.MaxValue / 2 : index + (long)delta;
            if (transform && index == FusionObject.TransformFirstWord)
            {
                for (var axis = 0; axis < 3; axis++)
                    _recordWords.Add((FusionObject.TransformFirstWord + axis, FusionSimCodec.UnZigZag32(reader.ReadUInt32VarLength(4))));
                _recordWords.Add((FusionObject.TransformFirstWord + 3, (int)reader.ReadUInt32()));
                index = FusionObject.TransformLastWord;
                continue;
            }
            if (transform && index > FusionObject.TransformFirstWord && index <= FusionObject.TransformLastWord)
                throw new InvalidDataException($"word {index} inside the transform block");
            var value = FusionSimCodec.ReadWord(reader);
            if (index >= wordCount) valid = false;
            else _recordWords.Add(((int)index, value));
        }
    }

    /// <summary>A full record for an unknown id creates the object (spec §9.2) and schedules it on every
    /// other connection (§15 duty 2).</summary>
    private void CreateFromClient(FusionSimPlayer player, uint id, int[] header, int wordCount)
    {
        string? refusal = wordCount < FusionObject.HeaderWords ? $"word count {wordCount}"
            : id < FirstClientId ? "id in the server's range"
            : (header[FusionObject.FlagsWord] & FusionObject.FlagStruct) != 0 ? "struct flag"
            : _worldObjects.Count >= MaxRoomObjects || _roomBytes + CostOf(wordCount) > MaxRoomBytes ? "room memory bound reached"
            : _usage.GetValueOrDefault(player.Actor) is var used && (used.Objects >= MaxCreatorObjects || used.Bytes + CostOf(wordCount) > MaxCreatorBytes)
                ? "the creator's memory quota is used up"
            : null;
        if (refusal is not null)
        {
            Log.Debug(Cat, $"\"{Name}\" actor {player.Actor} object {id} refused: {refusal}");
            return;
        }

        var obj = new FusionObject(id, wordCount) { Creator = player.Actor };
        var stamp = NextStamp;
        for (var index = 1; index < FusionObject.HeaderWords; index++)
            if (index != FusionObject.InterestWord) obj.Set(index, header[index], stamp);
        foreach (var (index, value) in _recordWords) if (index >= FusionObject.HeaderWords) obj.Set(index, value, stamp);
        _objects.Add(id, obj);
        obj.WorldIndex = _worldObjects.Count;
        _worldObjects.Add(obj);
        Charge(obj, +1);

        foreach (var other in _players.Values)
        {
            // GAP: an id reused while the old object's destroy is still queued for someone replaces that
            // destroy with the new object's full record, which a client applies over the old one. Clients
            // allocate ids from a rising counter, so only a misbehaving one gets here — revisit if a title
            // ever reuses ids.
            other.PendingDestroys.Remove(id);
            other.Objects[id] = new ObjectSendState(other == player ? ObjectSendStatus.CreatedConfirmed : ObjectSendStatus.CreatedUnconfirmed);
        }
        Log.Debug(Cat, $"\"{Name}\" actor {player.Actor} created {id}: type {header[FusionObject.TypeWord]}/{header[FusionObject.TypeWord + 1]} " +
                       $"words {wordCount} flags {header[FusionObject.FlagsWord]} authority {header[FusionObject.StateAuthorityWord]}");
    }

    /// <summary>Reliable messages are delivered in the source's sequence order, unreliable ones as they come (§12.2).</summary>
    private void AcceptMessage(FusionSimPlayer player, SimMessage message)
    {
        message.Source = player.PlayerIndex;
        if (!message.IsReliable)
        {
            DeliverMessage(player, message);
            return;
        }
        if (message.Sequence <= player.InDelivered || player.InPending.ContainsKey(message.Sequence)) return;   // duplicate
        if (player.InPending.Count >= MaxPendingInbound)
        {
            Log.Warn(Cat, $"\"{Name}\" actor {player.Actor} has {player.InPending.Count} messages behind a sequence gap: disconnected");
            _peer.Disconnect(player.Connection);
            return;
        }
        player.InPending.Add(message.Sequence, message);
        while (player.InPending.Remove(player.InDelivered + 1, out var next))
        {
            player.InDelivered++;
            DeliverMessage(player, next);
            if (!_players.ContainsKey(player.Actor)) return;   // a handler disconnected the source
        }
    }

    /// <summary>The relay rule (spec §12.3) and the internal messages (§12.5).</summary>
    private void DeliverMessage(FusionSimPlayer source, SimMessage message)
    {
        if ((message.Flags & SimMessageFlags.Dummy) != 0) return;
        if ((message.Flags & SimMessageFlags.Internal) != 0)
        {
            HandleInternal(source, message);
            return;
        }
        if ((message.Flags & SimMessageFlags.TargetServer) != 0)
        {
            Log.Debug(Cat, $"\"{Name}\" message for the server from actor {source.Actor} ignored (flags {message.Flags})");
            return;
        }

        var forwarded = 0;
        if ((message.Flags & SimMessageFlags.TargetPlayer) != 0)
        {
            if (message.Target != source.PlayerIndex && _players.TryGetValue(message.Target, out var target) && Enqueue(target, message))
                forwarded++;
        }
        else
        {
            foreach (var other in _players.Values.ToArray())
                if (other != source && Enqueue(other, message)) forwarded++;
        }
        _relayed += forwarded;

        if (Log.Enabled(LogLevel.Debug))
        {
            var payload = message.Payload;
            var rpc = payload.Length >= 8 && (message.Flags & SimMessageFlags.UserMessage) == 0
                ? $"obj={BitConverter.ToUInt32(payload, 0)} beh={BitConverter.ToUInt16(payload, 4)} method={BitConverter.ToUInt16(payload, 6)}"
                : "raw";
            var target = (message.Flags & SimMessageFlags.TargetPlayer) != 0 ? $"player {message.Target}" : "all";
            Log.Debug(Cat, $"\"{Name}\" rpc from actor {source.Actor} target={target} flags={message.Flags & ~SimMessageFlags.Remote} {rpc} size={payload.Length} fwd={forwarded}");
        }
    }

    /// <summary>Queues a message for <paramref name="player"/> with a sequence from its own counter (§12.3-§12.4).</summary>
    private bool Enqueue(FusionSimPlayer player, SimMessage message)
    {
        const int maxQueued = 8192;
        if (player.Outgoing.Count >= maxQueued)
        {
            Log.Warn(Cat, $"\"{Name}\" actor {player.Actor} is {player.Outgoing.Count} messages behind: disconnected");
            _peer.Disconnect(player.Connection);
            return false;
        }
        var sequence = message.IsReliable ? ++player.OutSequence : 0;
        player.Outgoing.AddLast(new OutMessage(message, sequence, _tick));
        return true;
    }

    private void HandleInternal(FusionSimPlayer source, SimMessage message)
    {
        var payload = message.Payload;
        var type = payload.Length >= 4 ? BitConverter.ToInt32(payload, 0) : -1;
        switch (type)
        {
            case 4 when payload.Length >= 12:   // SharedModeRequestStateAuthority
                RequestStateAuthority(source, BitConverter.ToUInt32(payload, 4), BitConverter.ToInt32(payload, 8) != 0);
                break;
            case 6 when payload.Length >= 8:    // SetPlayerObject
                var objectId = BitConverter.ToUInt32(payload, 4);
                if (_objects.TryGetValue(source.PlayerDataId, out var playerData))
                    playerData.Set(FusionObject.HeaderWords + 1, (int)objectId, NextStamp);
                Log.Debug(Cat, $"\"{Name}\" actor {source.Actor} player object -> {objectId}");
                break;
            case 3 or 7:   // SharedModeSetAlwaysInterested / SetAreaOfInterest: every object already reaches everyone (§14)
                Log.Debug(Cat, $"\"{Name}\" internal {type} from actor {source.Actor} ignored: no area of interest");
                break;
            default:
                Log.Debug(Cat, $"\"{Name}\" internal message {type} ({payload.Length} bytes) from actor {source.Actor} dropped");
                break;
        }
    }

    /// <summary>The Shared-mode authority rules (spec §12.6). Header word 8 is the only reply.</summary>
    private void RequestStateAuthority(FusionSimPlayer source, uint id, bool acquire)
    {
        if (!_objects.TryGetValue(id, out var obj) || obj.IsStruct || id < FirstServerId) return;
        var raw = obj.StateAuthority;
        var current = ResolveAuthority(raw);
        var next = raw;
        if (acquire)
        {
            var currentGone = current == FusionObject.PlayerNone || !_players.ContainsKey(current - 1);
            if (current != source.PlayerRaw && raw != FusionObject.PlayerMasterClient
                && ((obj.Flags & FusionObject.FlagAllowStateAuthorityOverride) != 0 || currentGone))
                next = source.PlayerRaw;
        }
        else if (current == source.PlayerRaw && raw != FusionObject.PlayerMasterClient)
            next = FusionObject.PlayerNone;

        if (next != raw) obj.Set(FusionObject.StateAuthorityWord, next, NextStamp);
        Log.Debug(Cat, $"\"{Name}\" actor {source.Actor} {(acquire ? "requests" : "releases")} {id}: authority {raw} -> {next}");
    }
}
