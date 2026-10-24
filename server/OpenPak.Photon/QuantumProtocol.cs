using System.Text;

namespace OpenPak.Photon;

/// <summary>
/// Photon Quantum 2's deterministic-session protocol as its client speaks it (read from the
/// PhotonDeterministic client, protocol V2_2_0_0): what a Quantum room's server must answer.
/// The messages ride Photon RaiseEvent payloads: event 100 (reliable) and 101 (unreliable) carry
/// protocol messages, event 102 carries tick inputs. A payload is an LSB-first bit stream.
/// </summary>
public static class QuantumProtocol
{
    public const byte ProtocolEvent = 100, ProtocolEventUnreliable = 101, InputEvent = 102;

    // Message ids (Serializer registry).
    public const byte Join = 1, Joined = 2, SessionConfig = 3, RuntimeConfig = 4, SimulationStart = 5,
                      SimulationStop = 6, ClockCorrect = 7, TickChecksum = 8, TickChecksumError = 9,
                      RttUpdate = 10, SetPlayerData = 11, Disconnect = 12, FrameSnapshot = 13, Command = 14,
                      TickChecksumErrorFrameDump = 15, InputMissing = 16, FrameSnapshotRequest = 17,
                      StartRequest = 21;

    // DeterministicInputFlags.
    public const int FlagRepeatable = 1, FlagPlayerNotPresent = 2, FlagReplacedByServer = 4, FlagCommand = 8;

    // JoinStatus.
    public const int JoinFailed = 0, JoinConfirmed = 1, JoinWaitForSnapshot = 2;

    /// <summary>The serialized DeterministicSessionConfig: 1 null bit, then 20 32-bit fields and 7 bools.</summary>
    public const int SessionConfigBits = 1 + 20 * 32 + 7;
}

/// <summary>Quantum's BitStream: bits fill each byte from bit 0; multi-byte values are little-endian.</summary>
public sealed class QuantumBitWriter
{
    private byte[] _buf = new byte[256];
    public int BitPosition { get; private set; }

    private void Ensure(int bits)
    {
        var need = (BitPosition + bits + 7) >> 3;
        if (need > _buf.Length) Array.Resize(ref _buf, Math.Max(need, _buf.Length * 2));
    }

    public void WriteBits(ulong value, int bits)
    {
        Ensure(bits);
        for (var i = 0; i < bits; i++, BitPosition++)
        {
            if (((value >> i) & 1) != 0) _buf[BitPosition >> 3] |= (byte)(1 << (BitPosition & 7));
            else _buf[BitPosition >> 3] &= (byte)~(1 << (BitPosition & 7));
        }
    }

    public void WriteBool(bool v) => WriteBits(v ? 1u : 0u, 1);
    public void WriteByte(byte v) => WriteBits(v, 8);
    public void WriteInt(int v) => WriteBits((uint)v, 32);
    public void WriteULong(ulong v) => WriteBits(v, 64);
    public void WriteDouble(double v) => WriteBits((ulong)BitConverter.DoubleToInt64Bits(v), 64);

    public void WriteBytes(ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes) WriteByte(b);
    }

    /// <summary>string: 1 bit "is null", then u16 UTF-8 length and the bytes.</summary>
    public void WriteString(string? s)
    {
        WriteBool(s is null);
        if (s is null) return;
        var b = Encoding.UTF8.GetBytes(s);
        WriteBits((ulong)b.Length, 16);
        WriteBytes(b);
    }

    /// <summary>byte[]: 1 bit "present", then u16 length and the bytes.</summary>
    public void WriteByteArray(byte[]? a)
    {
        WriteBool(a is not null);
        if (a is null) return;
        WriteBits((ulong)a.Length, 16);
        WriteBytes(a);
    }

    /// <summary>int[]: 1 bit "present", then u16 count and 32-bit elements.</summary>
    public void WriteIntArray(int[]? a)
    {
        WriteBool(a is not null);
        if (a is null) return;
        WriteBits((ulong)a.Length, 16);
        foreach (var v in a) WriteInt(v);
    }

    /// <summary>Copies a bit range verbatim (a SessionConfig the client sent, re-emitted unchanged).</summary>
    public void WriteBitsFrom(QuantumBitReader src, int start, int count)
    {
        for (var i = 0; i < count; i++) WriteBits(src.BitAt(start + i) ? 1u : 0u, 1);
    }

    public void AlignToByte() => BitPosition = (BitPosition + 7) & ~7;

    public byte[] ToArray() => _buf[..((BitPosition + 7) >> 3)];
}

public sealed class QuantumBitReader
{
    private readonly byte[] _buf;
    private readonly int _end;
    public int BitPosition { get; set; }

    public QuantumBitReader(byte[] buf, int lengthBytes = -1)
    {
        _buf = buf;
        _end = (lengthBytes < 0 ? buf.Length : lengthBytes) * 8;
    }

    public int BitsLeft => _end - BitPosition;
    public bool CanRead(int bits) => BitPosition + bits <= _end;

    public bool BitAt(int pos) => (_buf[pos >> 3] >> (pos & 7) & 1) != 0;

    public ulong ReadBits(int bits)
    {
        if (!CanRead(bits)) throw new EndOfStreamException("Quantum bit stream ended");
        ulong v = 0;
        for (var i = 0; i < bits; i++, BitPosition++)
            if (BitAt(BitPosition)) v |= 1UL << i;
        return v;
    }

    public bool ReadBool() => ReadBits(1) != 0;
    public byte ReadByte() => (byte)ReadBits(8);
    public int ReadInt() => (int)(uint)ReadBits(32);
    public ulong ReadULong() => ReadBits(64);
    public double ReadDouble() => BitConverter.Int64BitsToDouble((long)ReadBits(64));

    public byte[] ReadBytes(int n)
    {
        var b = new byte[n];
        for (var i = 0; i < n; i++) b[i] = ReadByte();
        return b;
    }

    public string? ReadString()
    {
        if (ReadBool()) return null;
        var n = (int)ReadBits(16);
        return Encoding.UTF8.GetString(ReadBytes(n));
    }

    public byte[]? ReadByteArray()
    {
        if (!ReadBool()) return null;
        return ReadBytes((int)ReadBits(16));
    }

    public int[]? ReadIntArray()
    {
        if (!ReadBool()) return null;
        var n = (int)ReadBits(16);
        var a = new int[n];
        for (var i = 0; i < n; i++) a[i] = ReadInt();
        return a;
    }

    public void AlignToByte() => BitPosition = (BitPosition + 7) & ~7;
}
