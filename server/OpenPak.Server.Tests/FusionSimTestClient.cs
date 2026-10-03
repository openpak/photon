using OpenPak.Photon;
using OpenPak.Server.LoadBalancing.Fusion;

namespace OpenPak.Server.Tests;

/// <summary>One object record as a scripted client decoded it from a server packet.</summary>
public sealed record ReceivedRecord(uint Id, bool Full, int[]? Header, List<(int Index, int Value)> Words);

/// <summary>One server packet as decoded by <see cref="FusionSimTestClient"/>.</summary>
public sealed record ReceivedPacket(int Tick, List<SimMessage> Messages, double[] Feedback, List<uint> Destroys, List<ReceivedRecord> Records);

/// <summary>
/// A byte-level stand-in for a Fusion 2 Shared client, written from docs/protocol/07 and 08: it connects,
/// sends NotifyData packets built from scripted messages/destroys/records, acknowledges (or deliberately
/// drops) server packets, reassembles fragments, and decodes what the server sent.
/// </summary>
public sealed class FusionSimTestClient(int actor)
{
    public int Actor { get; } = actor;
    public int PlayerRaw => Actor + 1;
    public readonly List<ReceivedPacket> Packets = new();
    /// <summary>Object shapes this client knows (word count, flags), needed to parse transform blocks.</summary>
    private readonly Dictionary<uint, (int WordCount, int Flags)> _known = new();
    private ushort _sendSequence, _recvSequence;
    private ulong _recvMask;
    private int _clientTick = 100;
    private ulong _messageSequence;
    private byte[]? _fragments;
    /// <summary>When set, the next notify packet is ignored as if Photon lost it.</summary>
    public bool DropNext;
    public uint Counter;

    public byte[] Connect() => FusionRelayCodec.EncodeConnect(new NetConnectionId(0, 0, 1), [], UniqueId);

    public ulong UniqueId => BitConverter.ToUInt64(FusionProtocol.UniqueIdForActor(Actor));

    /// <summary>Feeds one server datagram to the client.</summary>
    public void Receive(byte[] datagram)
    {
        if (datagram[0] == (byte)NetPacketType.Command)
        {
            if (FusionRelayCodec.TryDecodeAccepted(datagram, out _, out _, out var counter)) Counter = counter;
            return;
        }
        if (datagram[0] != (byte)NetPacketType.NotifyData || !FusionRelayCodec.TryReadNotifyHeader(datagram, out var header)) return;
        byte[] packet;
        if (header.Fragment != 0)
        {
            var chunk = datagram.AsSpan(FusionRelayCodec.NotifyHeaderSize);
            _fragments = (header.Fragment & 0x7F) == 1 ? datagram.ToArray() : [.. _fragments!, .. chunk];
            if ((header.Fragment & 0x80) == 0) return;
            packet = _fragments;
        }
        else packet = datagram;
        if (DropNext)
        {
            DropNext = false;
            return;
        }
        var distance = (short)(header.Sequence - _recvSequence);
        if (distance <= 0) return;
        _recvMask = distance >= 64 ? 1UL : (_recvMask << distance) | 1UL;
        _recvSequence = header.Sequence;
        Packets.Add(Decode(new NetBitReader(packet, FusionRelayCodec.NotifyHeaderSize * 8)));
    }

    private ReceivedPacket Decode(NetBitReader reader)
    {
        var (messages, _, updates, destroys, tick) = FusionSimCodec.ReadHeader(reader);
        reader.PadToByteBoundary();
        var received = new List<SimMessage>();
        for (var i = 0; i < messages; i++) received.Add(FusionSimCodec.ReadMessage(reader));
        reader.PadToByteBoundary();
        var feedback = new double[4];
        for (var i = 0; i < 4; i++) feedback[i] = FusionSimCodec.UnZigZag32(reader.ReadUInt32VarLength(6)) / 256.0;
        var destroyed = new List<uint>();
        for (var i = 0; i < destroys; i++) destroyed.Add(reader.ReadUInt32VarLength(8));
        var records = new List<ReceivedRecord>();
        uint previous = 0;
        for (var i = 0; i < updates; i++)
        {
            var id = (uint)((int)previous + FusionSimCodec.UnZigZag32(reader.ReadUInt32VarLength(6)));
            previous = id;
            var full = reader.ReadBoolean();
            int[]? header = null;
            if (full)
            {
                header = new int[20];
                for (var w = 1; w < 20; w++) if (reader.ReadBoolean()) header[w] = FusionSimCodec.ReadWord(reader);
                _known[id] = ((short)header[1], header[6]);
            }
            var shape = _known.GetValueOrDefault(id);
            var transform = FusionObject.HasTransformBlock(shape.Flags, shape.WordCount);
            var words = new List<(int, int)>();
            long index = full ? 19 : 0;
            for (ulong delta; (delta = reader.ReadUInt64VarLength(4)) != 0;)
            {
                index += (long)delta;
                if (transform && index == 22)
                {
                    for (var axis = 0; axis < 3; axis++) words.Add((22 + axis, FusionSimCodec.UnZigZag32(reader.ReadUInt32VarLength(4))));
                    words.Add((25, (int)reader.ReadUInt32()));
                    index = 28;
                    continue;
                }
                words.Add(((int)index, FusionSimCodec.ReadWord(reader)));
            }
            records.Add(new ReceivedRecord(id, full, header, words));
        }
        foreach (var id in destroyed) _known.Remove(id);
        return new ReceivedPacket(tick, received, feedback, destroyed, records);
    }

    /// <summary>A NotifyData datagram carrying our acks and a payload written by <paramref name="body"/>.</summary>
    public byte[] Notify(Action<NetBitWriter> body)
    {
        var writer = new NetBitWriter();
        body(writer);
        var datagram = new byte[FusionRelayCodec.NotifyHeaderSize + writer.ByteCount];
        FusionRelayCodec.WriteNotifyHeader(datagram, new NotifyHeader(NetPacketType.NotifyData, 0, ++_sendSequence, _recvSequence, _recvMask));
        writer.Bytes.CopyTo(datagram.AsSpan(FusionRelayCodec.NotifyHeaderSize));
        return datagram;
    }

    /// <summary>A simulation packet: messages, destroys, then records (id, header or null, words).</summary>
    public byte[] Packet(IEnumerable<SimMessage>? messages = null, IEnumerable<uint>? destroys = null,
                         IEnumerable<(uint Id, int[]? Header, (int Index, int Value)[] Words)>? records = null) => Notify(w =>
    {
        var messageList = messages?.ToList() ?? [];
        var destroyList = destroys?.ToList() ?? [];
        var recordList = records?.ToList() ?? [];
        w.WriteBits(0, 64);
        w.PadToByteBoundary();
        foreach (var message in messageList)
            FusionSimCodec.WriteMessage(w, message, _clientTick, message.IsReliable ? ++_messageSequence : 0, message.Target);
        w.PadToByteBoundary();
        foreach (var id in destroyList) w.WriteUInt32VarLength(id, 8);
        uint previous = 0;
        foreach (var (id, header, words) in recordList)
        {
            w.WriteUInt32VarLength(FusionSimCodec.ZigZag32((int)(id - previous)), 6);
            previous = id;
            w.WriteBoolean(header is not null);
            if (header is not null)
                for (var i = 1; i < 20; i++)
                {
                    w.WriteBoolean(header[i] != 0);
                    if (header[i] != 0) FusionSimCodec.WriteWord(w, header[i]);
                }
            var cursor = header is not null ? 19 : 0;
            foreach (var (index, value) in words)
            {
                w.WriteUInt32VarLength((uint)(index - cursor), 4);
                FusionSimCodec.WriteWord(w, value);
                cursor = index;
            }
            w.WriteUInt32VarLength(0, 4);
        }
        FusionSimCodec.PatchHeader(w, 0, messageList.Count, recordList.Count, destroyList.Count, _clientTick++);
    });

    /// <summary>A Fusion object header: word count, prefab type, flags, state authority.</summary>
    public static int[] Header(int wordCount, int flags, int stateAuthority, int prefab = 7)
    {
        var header = new int[20];
        header[1] = wordCount;
        header[2] = prefab;
        header[6] = flags;
        header[8] = stateAuthority;
        return header;
    }

    /// <summary>The latest value of <paramref name="index"/> for object <paramref name="id"/> across received packets.</summary>
    public int? LatestWord(uint id, int index)
    {
        int? value = null;
        foreach (var record in Packets.SelectMany(p => p.Records).Where(r => r.Id == id))
        {
            if (record.Header is not null && index < 20) value = record.Header[index];
            foreach (var (i, v) in record.Words) if (i == index) value = v;
        }
        return value;
    }
}
