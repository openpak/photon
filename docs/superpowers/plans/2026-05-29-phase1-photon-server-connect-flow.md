# Phase 1 — Photon Transport + Connect Flow Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a clean-room C# Photon server (transport + GpBinary + Name/Master/Game roles) so the real Black Ice client, pointed at us by a config-driven redirect mod, connects and reaches the in-room state with Photon Cloud never contacted.

**Architecture:** One .NET process listens on the three Photon UDP ports (5058 Name, 5055 Master, 5056 Game) and routes each peer to a role-handler. A clean-room protocol library (`BlackIce.Photon`) implements Photon's reliable-UDP (eNet) transport and GpBinary v1.8 serialization, validated byte-for-byte against the real `Photon3Unity3D.dll` used as a **test-only oracle**. Ships no Photon code (GPLv3).

**Tech Stack:** .NET 8, xUnit, Mono.Cecil (already used in Phase 0), BepInEx/HarmonyX (redirect mod), the real `Photon3Unity3D.dll`/`PhotonRealtime.dll` as test-only serialization oracles.

**Environment notes:**
- `$REPO` = `C:\Users\natew\OneDrive\Documentos\blackice-re`; `$GAME` = `C:\Program Files (x86)\Steam\steamapps\common\Black Ice`; `$MANAGED` = `$GAME\Black Ice_Data\Managed`.
- `curl` needs `--ssl-no-revoke`. NuGet/dotnet restore is fine.
- Decompiled references already exist: `$REPO/decompiled/Photon3Unity3D`, `/PhotonRealtime`, `/PUN`.
- Phase 0 protocol docs: `$REPO/docs/protocol/`. Op-logger: `$GAME/BepInEx/oplog.jsonl`.
- Run Bash-tool commands from `$REPO`. Work on branch `phase1-connect` (created in Task 1).

---

## File Structure

```
server/
  BlackIce.Server.sln
  BlackIce.Photon/                         # clean-room protocol library
    BlackIce.Photon.csproj
    GpType.cs                              # v1.8 type tags
    GpBinaryWriter.cs  GpBinaryReader.cs   # value serialize/deserialize
    OperationData.cs                       # OperationRequest/Response/EventData records
    MessageSerializer.cs                   # operation/event envelope (de)serialization
    Transport/
      PhotonHeader.cs                      # 12-byte UDP packet header
      NCommand.cs                          # command header + command types
      EnetChannel.cs                       # per-channel sequencing
      EnetPeer.cs                          # connection state machine (reliable path)
  BlackIce.Photon.Crypto/
    BlackIce.Photon.Crypto.csproj
    EncryptionHandshake.cs                 # implemented after Task 7 spike
  BlackIce.Server.Core/
    BlackIce.Server.Core.csproj
    UdpListener.cs                         # socket loop
    PeerConnection.cs                      # per-peer state, owns an EnetPeer
    IOperationHandler.cs                   # role-handler interface
    OperationDispatcher.cs
  BlackIce.Server.LoadBalancing/
    BlackIce.Server.LoadBalancing.csproj
    NameServerHandler.cs  MasterServerHandler.cs  GameServerHandler.cs
    Room.cs  RoomRegistry.cs  AuthToken.cs
  BlackIce.Server.Host/
    BlackIce.Server.Host.csproj
    Program.cs  ServerConfig.cs
  BlackIce.Photon.Tests/                   # references real Photon DLL (test-only oracle)
    BlackIce.Photon.Tests.csproj
    OracleFixture.cs  GpBinaryTests.cs  MessageTests.cs  TransportTests.cs
  BlackIce.Server.Tests/
    BlackIce.Server.Tests.csproj
    NameServerTests.cs  MasterServerTests.cs  GameServerTests.cs
plugins/
  BlackIce.Redirect/                       # config-driven realmlist redirect
    BlackIce.Redirect.csproj  RedirectPlugin.cs
```

---

## Task 1: Solution scaffold and branch

**Files:** `server/BlackIce.Server.sln` and the seven project files above.

- [ ] **Step 1: Branch**

```bash
cd "$REPO" && git checkout -b phase1-connect && git branch --show-current
```
Expected: `phase1-connect`.

- [ ] **Step 2: Create projects and solution**

```bash
cd "$REPO/server" 2>/dev/null || { mkdir -p "$REPO/server"; cd "$REPO/server"; }
for p in BlackIce.Photon BlackIce.Photon.Crypto BlackIce.Server.Core BlackIce.Server.LoadBalancing; do
  dotnet new classlib -n $p -o $p --framework net8.0 >/dev/null && rm -f $p/Class1.cs; done
dotnet new console -n BlackIce.Server.Host -o BlackIce.Server.Host --framework net8.0 >/dev/null
dotnet new xunit -n BlackIce.Photon.Tests -o BlackIce.Photon.Tests --framework net8.0 >/dev/null && rm -f BlackIce.Photon.Tests/UnitTest1.cs
dotnet new xunit -n BlackIce.Server.Tests -o BlackIce.Server.Tests --framework net8.0 >/dev/null && rm -f BlackIce.Server.Tests/UnitTest1.cs
dotnet new sln -n BlackIce.Server >/dev/null
dotnet sln add */*.csproj >/dev/null && echo "projects added"
```

- [ ] **Step 3: Wire project references**

```bash
cd "$REPO/server"
dotnet add BlackIce.Photon.Crypto reference BlackIce.Photon
dotnet add BlackIce.Server.Core reference BlackIce.Photon BlackIce.Photon.Crypto
dotnet add BlackIce.Server.LoadBalancing reference BlackIce.Server.Core
dotnet add BlackIce.Server.Host reference BlackIce.Server.LoadBalancing
dotnet add BlackIce.Photon.Tests reference BlackIce.Photon
dotnet add BlackIce.Server.Tests reference BlackIce.Server.LoadBalancing
echo "references wired"
```

- [ ] **Step 4: Add the test-only Photon oracle reference to BlackIce.Photon.Tests**

Edit `BlackIce.Photon.Tests/BlackIce.Photon.Tests.csproj`, add inside an `<ItemGroup>`:

```xml
    <Reference Include="Photon3Unity3D">
      <HintPath>C:\Program Files (x86)\Steam\steamapps\common\Black Ice\Black Ice_Data\Managed\Photon3Unity3D.dll</HintPath>
      <Private>true</Private>
    </Reference>
```

> `Private=true` copies the DLL to the test bin for runtime use. It is **never** referenced
> by shippable projects and stays gitignored (`*.dll`).

- [ ] **Step 5: Verify the solution builds**

```bash
cd "$REPO/server" && dotnet build 2>&1 | tail -4
```
Expected: `Build succeeded.`

- [ ] **Step 6: Commit**

```bash
cd "$REPO" && git add server/ && git status -s | grep -iE '/bin/|/obj/|\.dll$' || echo "clean"
git commit -m "chore(server): scaffold BlackIce.Server solution"
```

---

## Task 2: Identify the oracle API

**Files:** none (investigation; record findings in the commit message and `OracleFixture.cs` next task).

- [ ] **Step 1: Find the public serialize entrypoint in the real DLL**

```bash
cd "$REPO"
F="decompiled/Photon3Unity3D/Photon3Unity3D.decompiled.cs"
grep -nE "class Protocol18|public (override )?(void|byte\[\]|StreamBuffer) Serialize|SerializeOperationRequest|public abstract class Protocol|GpType" "$F" | head -20
```
Expected: identifies `Protocol18 : Protocol` and its public `Serialize(StreamBuffer, object, bool setType)` plus `SerializeOperationRequest`/`SerializeEventData`. Record the exact method names and namespaces.

- [ ] **Step 2: Confirm GpType tag values match Phase 0 extraction**

```bash
grep -A25 "enum GpType" "$REPO/decompiled/Photon3Unity3D/Photon3Unity3D.decompiled.cs" | head -25
```
Expected: the v1.8 ASCII tags (Byte=98, Boolean=111, Short=107, Integer=105, Long=108, Float=102, Double=100, String=115, Null=42, ByteArray=120, Array=121, etc.). These seed `GpType.cs`.

- [ ] **Step 3: Commit a short findings note**

```bash
cd "$REPO" && printf '%s\n' "# Oracle API (Phase 1)" "Protocol18 in ExitGames.Client.Photon; serialize via <recorded signature>." > server/ORACLE.md
git add server/ORACLE.md && git commit -m "docs(server): record Photon serialization oracle API"
```

---

## Task 3: GpBinary v1.8 — scalar values (oracle-driven TDD)

**Files:**
- Create: `server/BlackIce.Photon/GpType.cs`, `GpBinaryWriter.cs`, `GpBinaryReader.cs`
- Test: `server/BlackIce.Photon.Tests/OracleFixture.cs`, `GpBinaryTests.cs`

- [ ] **Step 1: Define the type tags**

Create `BlackIce.Photon/GpType.cs`:

```csharp
namespace BlackIce.Photon;

/// <summary>GpBinary v1.8 type markers (ASCII), extracted from Photon3Unity3D.</summary>
public enum GpType : byte
{
    Unknown = 0, Null = 42, Dictionary = 68, StringArray = 97, Byte = 98,
    Custom = 99, Double = 100, EventData = 101, Float = 102, Hashtable = 104,
    Integer = 105, Short = 107, Long = 108, IntegerArray = 110, Boolean = 111,
    OperationResponse = 112, OperationRequest = 113, String = 115, ByteArray = 120,
    Array = 121, ObjectArray = 122,
}
```

- [ ] **Step 2: Write the oracle fixture**

Create `BlackIce.Photon.Tests/OracleFixture.cs` (uses the exact API recorded in Task 2; the
signature below is the expected shape — adjust to the recorded one):

```csharp
using ExitGames.Client.Photon;

namespace BlackIce.Photon.Tests;

/// <summary>Produces reference bytes using the real Photon serializer (test-only oracle).</summary>
public static class Oracle
{
    static readonly Protocol Protocol = new Protocol18();

    public static byte[] Serialize(object value)
    {
        var sb = new StreamBuffer(64);
        Protocol.Serialize(sb, value, setType: true);
        return sb.ToArray();
    }
}
```

- [ ] **Step 3: Write failing scalar round-trip tests**

Create `BlackIce.Photon.Tests/GpBinaryTests.cs`:

```csharp
using Xunit;

namespace BlackIce.Photon.Tests;

public class GpBinaryTests
{
    public static IEnumerable<object[]> Scalars() => new[]
    {
        new object[] { (byte)200 }, new object[] { true }, new object[] { false },
        new object[] { (short)-1234 }, new object[] { 1_000_000 }, new object[] { -7L },
        new object[] { 3.5f }, new object[] { 2.5d }, new object[] { "Black Ice" },
    };

    [Theory]
    [MemberData(nameof(Scalars))]
    public void Writer_matches_oracle_bytes(object value)
    {
        var ours = new GpBinaryWriter().WriteTyped(value).ToArray();
        Assert.Equal(Oracle.Serialize(value), ours);
    }

    [Theory]
    [MemberData(nameof(Scalars))]
    public void Reader_roundtrips_oracle_bytes(object value)
    {
        var read = new GpBinaryReader(Oracle.Serialize(value)).ReadTyped();
        Assert.Equal(value, read);
    }
}
```

- [ ] **Step 4: Run to verify failure**

```bash
cd "$REPO/server" && dotnet test BlackIce.Photon.Tests --filter GpBinaryTests 2>&1 | tail -5
```
Expected: FAIL (types not defined).

- [ ] **Step 5: Implement the writer**

Create `BlackIce.Photon/GpBinaryWriter.cs`. Big-endian integers; one type byte then payload:

```csharp
using System.Buffers.Binary;
using System.Text;

namespace BlackIce.Photon;

/// <summary>Writes values in GpBinary v1.8. Integers are big-endian.</summary>
public sealed class GpBinaryWriter
{
    private readonly List<byte> _buf = new();

    public byte[] ToArray() => _buf.ToArray();

    public GpBinaryWriter WriteTyped(object value)
    {
        switch (value)
        {
            case byte b: _buf.Add((byte)GpType.Byte); _buf.Add(b); break;
            case bool flag: _buf.Add((byte)GpType.Boolean); _buf.Add((byte)(flag ? 1 : 0)); break;
            case short s: _buf.Add((byte)GpType.Short); WriteShort(s); break;
            case int i: _buf.Add((byte)GpType.Integer); WriteInt(i); break;
            case long l: _buf.Add((byte)GpType.Long); WriteLong(l); break;
            case float f: _buf.Add((byte)GpType.Float); WriteInt(BitConverter.SingleToInt32Bits(f)); break;
            case double d: _buf.Add((byte)GpType.Double); WriteLong(BitConverter.DoubleToInt64Bits(d)); break;
            case string str: _buf.Add((byte)GpType.String); WriteString(str); break;
            case null: _buf.Add((byte)GpType.Null); break;
            default: throw new NotSupportedException($"GpType for {value.GetType()} not yet implemented");
        }
        return this;
    }

    internal void WriteShort(short v) { Span<byte> t = stackalloc byte[2]; BinaryPrimitives.WriteInt16BigEndian(t, v); _buf.AddRange(t.ToArray()); }
    internal void WriteInt(int v) { Span<byte> t = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(t, v); _buf.AddRange(t.ToArray()); }
    internal void WriteLong(long v) { Span<byte> t = stackalloc byte[8]; BinaryPrimitives.WriteInt64BigEndian(t, v); _buf.AddRange(t.ToArray()); }
    internal void WriteString(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        WriteShort((short)bytes.Length);   // v1.8 string length prefix is int16
        _buf.AddRange(bytes);
    }
}
```

- [ ] **Step 6: Implement the reader**

Create `BlackIce.Photon/GpBinaryReader.cs`:

```csharp
using System.Buffers.Binary;
using System.Text;

namespace BlackIce.Photon;

/// <summary>Reads values in GpBinary v1.8.</summary>
public sealed class GpBinaryReader
{
    private readonly byte[] _data;
    private int _pos;
    public GpBinaryReader(byte[] data) { _data = data; }

    public object? ReadTyped()
    {
        var type = (GpType)_data[_pos++];
        return type switch
        {
            GpType.Byte => _data[_pos++],
            GpType.Boolean => _data[_pos++] != 0,
            GpType.Short => ReadShort(),
            GpType.Integer => ReadInt(),
            GpType.Long => ReadLong(),
            GpType.Float => BitConverter.Int32BitsToSingle(ReadInt()),
            GpType.Double => BitConverter.Int64BitsToDouble(ReadLong()),
            GpType.String => ReadString(),
            GpType.Null => null,
            _ => throw new NotSupportedException($"GpType {type} not yet implemented"),
        };
    }

    internal short ReadShort() { var v = BinaryPrimitives.ReadInt16BigEndian(_data.AsSpan(_pos)); _pos += 2; return v; }
    internal int ReadInt() { var v = BinaryPrimitives.ReadInt32BigEndian(_data.AsSpan(_pos)); _pos += 4; return v; }
    internal long ReadLong() { var v = BinaryPrimitives.ReadInt64BigEndian(_data.AsSpan(_pos)); _pos += 8; return v; }
    internal string ReadString() { int len = ReadShort(); var s = Encoding.UTF8.GetString(_data, _pos, len); _pos += len; return s; }
}
```

- [ ] **Step 7: Run tests; reconcile against the oracle**

```bash
cd "$REPO/server" && dotnet test BlackIce.Photon.Tests --filter GpBinaryTests 2>&1 | tail -6
```
Expected: PASS. **If any scalar mismatches the oracle** (e.g. string length width, bool encoding), the oracle bytes are ground truth — adjust the writer/reader to match exactly, do not change the assertion. Re-run until green.

- [ ] **Step 8: Commit**

```bash
cd "$REPO" && git add server/ && git commit -m "feat(photon): GpBinary v1.8 scalar serialization (oracle-verified)"
```

---

## Task 4: GpBinary v1.8 — collections

**Files:** extend `GpBinaryWriter.cs`, `GpBinaryReader.cs`; `GpBinaryTests.cs`.

- [ ] **Step 1: Failing tests for byte[], string[], int[], and a parameter Dictionary<byte,object>**

Append to `GpBinaryTests.cs`:

```csharp
    [Fact]
    public void ByteArray_matches_oracle()
    {
        var v = new byte[] { 1, 2, 250 };
        Assert.Equal(Oracle.Serialize(v), new GpBinaryWriter().WriteTyped(v).ToArray());
    }

    [Fact]
    public void ParameterDictionary_roundtrips()
    {
        var dict = new Dictionary<byte, object> { { 220, "v1" }, { 224, 7 }, { 210, "us/*" } };
        var bytes = Oracle.Serialize(dict);
        var read = (Dictionary<byte, object>)new GpBinaryReader(bytes).ReadTyped()!;
        Assert.Equal("v1", read[220]);
        Assert.Equal(7, read[224]);
    }
```

- [ ] **Step 2: Run to verify failure**

```bash
cd "$REPO/server" && dotnet test BlackIce.Photon.Tests --filter "ByteArray_matches_oracle|ParameterDictionary_roundtrips" 2>&1 | tail -5
```
Expected: FAIL.

- [ ] **Step 3: Implement collection write paths**

Add cases to `GpBinaryWriter.WriteTyped` (before `default`):

```csharp
            case byte[] ba: _buf.Add((byte)GpType.ByteArray); WriteInt(ba.Length); _buf.AddRange(ba); break;
            case string[] sa: _buf.Add((byte)GpType.StringArray); WriteShort((short)sa.Length); foreach (var s in sa) WriteString(s); break;
            case int[] ia: _buf.Add((byte)GpType.IntegerArray); WriteInt(ia.Length); foreach (var n in ia) WriteInt(n); break;
            case Dictionary<byte, object> d2: _buf.Add((byte)GpType.Dictionary); WriteByteKeyedDictionary(d2); break;
```

Add the dictionary helper (a `Dictionary` value in v1.8 writes key-type, value-type, count,
then entries; for the byte-keyed parameter map the simplest faithful form is key-type=Byte,
value-type=Object/Unknown so each value carries its own type):

```csharp
    internal void WriteByteKeyedDictionary(Dictionary<byte, object> d)
    {
        _buf.Add((byte)GpType.Byte);      // key type
        _buf.Add((byte)GpType.Unknown);   // value type 0 => each value is self-typed
        WriteShort((short)d.Count);
        foreach (var kv in d) { _buf.Add(kv.Key); WriteTyped(kv.Value); }
    }
```

> The exact Dictionary header layout is the most likely place to differ from the oracle —
> reconcile in Step 5 against `Oracle.Serialize(dict)` bytes.

- [ ] **Step 4: Implement collection read paths**

Add to `GpBinaryReader.ReadTyped`'s switch:

```csharp
            GpType.ByteArray => ReadByteArray(),
            GpType.StringArray => ReadStringArray(),
            GpType.IntegerArray => ReadIntArray(),
            GpType.Dictionary => ReadByteKeyedDictionary(),
```

And the helpers:

```csharp
    internal byte[] ReadByteArray() { int n = ReadInt(); var a = _data.AsSpan(_pos, n).ToArray(); _pos += n; return a; }
    internal string[] ReadStringArray() { int n = ReadShort(); var a = new string[n]; for (int i = 0; i < n; i++) a[i] = ReadString(); return a; }
    internal int[] ReadIntArray() { int n = ReadInt(); var a = new int[n]; for (int i = 0; i < n; i++) a[i] = ReadInt(); return a; }
    internal Dictionary<byte, object> ReadByteKeyedDictionary()
    {
        _pos++; var valueType = (GpType)_data[_pos++]; int count = ReadShort();
        var d = new Dictionary<byte, object>(count);
        for (int i = 0; i < count; i++) { byte key = _data[_pos++]; d[key] = ReadTyped()!; }
        return d;
    }
```

- [ ] **Step 5: Run and reconcile against oracle**

```bash
cd "$REPO/server" && dotnet test BlackIce.Photon.Tests 2>&1 | tail -6
```
Expected: PASS. Reconcile any dictionary/array header mismatch against oracle bytes (ground truth) until green.

- [ ] **Step 6: Commit**

```bash
cd "$REPO" && git add server/ && git commit -m "feat(photon): GpBinary v1.8 collections + parameter dictionaries"
```

---

## Task 5: Operation/Event message envelopes

**Files:**
- Create: `server/BlackIce.Photon/OperationData.cs`, `MessageSerializer.cs`
- Test: `server/BlackIce.Photon.Tests/MessageTests.cs`

- [ ] **Step 1: Define the message records**

Create `BlackIce.Photon/OperationData.cs`:

```csharp
namespace BlackIce.Photon;

public sealed record OperationRequest(byte OperationCode, Dictionary<byte, object> Parameters);
public sealed record OperationResponse(byte OperationCode, short ReturnCode, string? DebugMessage, Dictionary<byte, object> Parameters);
public sealed record EventData(byte Code, Dictionary<byte, object> Parameters);
```

- [ ] **Step 2: Failing test for an OperationRequest envelope vs oracle**

Create `BlackIce.Photon.Tests/MessageTests.cs` (the oracle call uses the
`SerializeOperationRequest` signature recorded in Task 2):

```csharp
using ExitGames.Client.Photon;
using Xunit;

namespace BlackIce.Photon.Tests;

public class MessageTests
{
    [Fact]
    public void OperationRequest_matches_oracle()
    {
        var parms = new Dictionary<byte, object> { { 220, "Early Access v0.9.226_2.20.1" }, { 210, "us/*" } };
        // Oracle: build the real OperationRequest and serialize it.
        var sb = new StreamBuffer(64);
        var realParams = new Dictionary<byte, object>(parms);
        new Protocol18().SerializeOperationRequest(sb, 230, realParams, setType: true);
        var expected = sb.ToArray();

        var ours = MessageSerializer.SerializeRequest(new OperationRequest(230, parms));
        Assert.Equal(expected, ours);
    }
}
```

- [ ] **Step 3: Run to verify failure**

```bash
cd "$REPO/server" && dotnet test BlackIce.Photon.Tests --filter OperationRequest_matches_oracle 2>&1 | tail -5
```
Expected: FAIL.

- [ ] **Step 4: Implement MessageSerializer**

Create `BlackIce.Photon/MessageSerializer.cs`. The v1.8 operation-request envelope is:
`[OperationCode:byte][paramCount:int16]` then for each param `[key:byte][typed value]`:

```csharp
namespace BlackIce.Photon;

/// <summary>Serializes/deserializes Photon operation requests, responses, and events.</summary>
public static class MessageSerializer
{
    public static byte[] SerializeRequest(OperationRequest req)
    {
        var w = new GpBinaryWriter();
        w.WriteRawByte(req.OperationCode);
        w.WriteShort((short)req.Parameters.Count);
        foreach (var kv in req.Parameters) { w.WriteRawByte(kv.Key); w.WriteTyped(kv.Value); }
        return w.ToArray();
    }

    public static byte[] SerializeResponse(OperationResponse resp)
    {
        var w = new GpBinaryWriter();
        w.WriteRawByte(resp.OperationCode);
        w.WriteShort(resp.ReturnCode);
        if (resp.DebugMessage is null) w.WriteRawByte((byte)GpType.Null);
        else { w.WriteRawByte((byte)GpType.String); w.WriteString(resp.DebugMessage); }
        w.WriteShort((short)resp.Parameters.Count);
        foreach (var kv in resp.Parameters) { w.WriteRawByte(kv.Key); w.WriteTyped(kv.Value); }
        return w.ToArray();
    }

    public static byte[] SerializeEvent(EventData ev)
    {
        var w = new GpBinaryWriter();
        w.WriteRawByte(ev.Code);
        w.WriteShort((short)ev.Parameters.Count);
        foreach (var kv in ev.Parameters) { w.WriteRawByte(kv.Key); w.WriteTyped(kv.Value); }
        return w.ToArray();
    }

    public static OperationRequest DeserializeRequest(byte[] data)
    {
        var r = new GpBinaryReader(data);
        byte op = r.ReadRawByte();
        int count = r.ReadShort();
        var p = new Dictionary<byte, object>(count);
        for (int i = 0; i < count; i++) { byte key = r.ReadRawByte(); p[key] = r.ReadTyped()!; }
        return new OperationRequest(op, p);
    }
}
```

Add the raw (untyped) helpers to `GpBinaryWriter` and `GpBinaryReader`:

```csharp
// GpBinaryWriter:
public void WriteRawByte(byte b) => _buf.Add(b);
// GpBinaryReader:
public byte ReadRawByte() => _data[_pos++];
```

(Make `GpBinaryReader.ReadShort/ReadString` and `GpBinaryWriter.WriteShort/WriteString`
`public` so the serializer can use them.)

- [ ] **Step 5: Run and reconcile**

```bash
cd "$REPO/server" && dotnet test BlackIce.Photon.Tests 2>&1 | tail -6
```
Expected: PASS. The response/event envelopes mirror the request; if the oracle differs (e.g.
response return-code width), match it.

- [ ] **Step 6: Commit**

```bash
cd "$REPO" && git add server/ && git commit -m "feat(photon): operation/response/event message envelopes (oracle-verified)"
```

---

## Task 6: Transport — packet & command framing

**Files:**
- Create: `server/BlackIce.Photon/Transport/PhotonHeader.cs`, `NCommand.cs`
- Test: `server/BlackIce.Photon.Tests/TransportTests.cs`

> Layouts are defined by `docs/protocol/05-transport.md` (from the decompiled enet layer);
> these tests are self-contained (no oracle needed).

- [ ] **Step 1: Failing test for the 12-byte packet header round-trip**

Create `BlackIce.Photon.Tests/TransportTests.cs`:

```csharp
using BlackIce.Photon.Transport;
using Xunit;

namespace BlackIce.Photon.Tests;

public class TransportTests
{
    [Fact]
    public void PacketHeader_roundtrips()
    {
        var h = new PhotonHeader(PeerId: 7, CrcEnabled: false, CommandCount: 3, ServerTime: 123456, Challenge: -42);
        var buf = new byte[PhotonHeader.Size];
        h.WriteTo(buf);
        var parsed = PhotonHeader.ReadFrom(buf);
        Assert.Equal(h, parsed);
    }
}
```

- [ ] **Step 2: Run to verify failure**

```bash
cd "$REPO/server" && dotnet test BlackIce.Photon.Tests --filter PacketHeader_roundtrips 2>&1 | tail -5
```
Expected: FAIL.

- [ ] **Step 3: Implement PhotonHeader**

Create `BlackIce.Photon/Transport/PhotonHeader.cs` (big-endian; layout per 05-transport.md):

```csharp
using System.Buffers.Binary;

namespace BlackIce.Photon.Transport;

/// <summary>The 12-byte Photon UDP packet header (one per datagram, followed by N commands).</summary>
public readonly record struct PhotonHeader(short PeerId, bool CrcEnabled, byte CommandCount, int ServerTime, int Challenge)
{
    public const int Size = 12;

    public void WriteTo(Span<byte> b)
    {
        BinaryPrimitives.WriteInt16BigEndian(b, PeerId);
        b[2] = (byte)(CrcEnabled ? 1 : 0);
        b[3] = CommandCount;
        BinaryPrimitives.WriteInt32BigEndian(b[4..], ServerTime);
        BinaryPrimitives.WriteInt32BigEndian(b[8..], Challenge);
    }

    public static PhotonHeader ReadFrom(ReadOnlySpan<byte> b) => new(
        BinaryPrimitives.ReadInt16BigEndian(b),
        b[2] != 0,
        b[3],
        BinaryPrimitives.ReadInt32BigEndian(b[4..]),
        BinaryPrimitives.ReadInt32BigEndian(b[8..]));
}
```

- [ ] **Step 4: Failing test + impl for the command header**

Append to `TransportTests.cs`:

```csharp
    [Fact]
    public void Command_roundtrips()
    {
        var c = new NCommand(NCommand.SendReliable, channelId: 0, flags: 1, reservedByte: 4,
                             reliableSequenceNumber: 5, payload: new byte[] { 9, 9, 9 });
        var bytes = c.ToBytes();
        var parsed = NCommand.Parse(bytes, out int consumed);
        Assert.Equal(bytes.Length, consumed);
        Assert.Equal(c.CommandType, parsed.CommandType);
        Assert.Equal(c.Payload, parsed.Payload);
    }
```

Create `BlackIce.Photon/Transport/NCommand.cs` (command header is 12 bytes: type, channel,
flags, reserved, length(int32 incl. header), reliableSeq(int32)):

```csharp
using System.Buffers.Binary;

namespace BlackIce.Photon.Transport;

/// <summary>One eNet command: a 12-byte header followed by an optional payload.</summary>
public sealed record NCommand(byte CommandType, byte ChannelId, byte Flags, byte ReservedByte,
                              int ReliableSequenceNumber, byte[] Payload)
{
    public const int HeaderSize = 12;
    // Command type constants (from docs/protocol/05-transport.md)
    public const byte Acknowledge = 1, Connect = 2, VerifyConnect = 3, Disconnect = 4,
                      Ping = 5, SendReliable = 6, SendUnreliable = 7, SendFragment = 8;

    public byte[] ToBytes()
    {
        int total = HeaderSize + Payload.Length;
        var b = new byte[total];
        b[0] = CommandType; b[1] = ChannelId; b[2] = Flags; b[3] = ReservedByte;
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(4), total);
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(8), ReliableSequenceNumber);
        Payload.CopyTo(b.AsSpan(HeaderSize));
        return b;
    }

    public static NCommand Parse(ReadOnlySpan<byte> b, out int consumed)
    {
        byte type = b[0], channel = b[1], flags = b[2], reserved = b[3];
        int length = BinaryPrimitives.ReadInt32BigEndian(b[4..]);
        int seq = BinaryPrimitives.ReadInt32BigEndian(b[8..]);
        var payload = b[HeaderSize..length].ToArray();
        consumed = length;
        return new NCommand(type, channel, flags, reserved, seq, payload);
    }
}
```

- [ ] **Step 5: Run tests**

```bash
cd "$REPO/server" && dotnet test BlackIce.Photon.Tests --filter "Command_roundtrips|PacketHeader_roundtrips" 2>&1 | tail -6
```
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
cd "$REPO" && git add server/ && git commit -m "feat(photon): packet + command framing (eNet)"
```

---

## Task 7: Encryption spike + handshake

**Files:**
- Create: `server/BlackIce.Photon.Crypto/EncryptionHandshake.cs`
- Modify: `plugins/BlackIce.OpLogger/PhotonPatches.cs` (temporary crypto trace)

> The exact handshake is the highest-risk unknown. Confirm ground truth from the live client
> before implementing.

- [ ] **Step 1: Trace the client's crypto path**

Inspect the decompiled crypto namespace and the connect path for whether/when encryption is
established:

```bash
cd "$REPO"
grep -rnE "EstablishEncryption|ExchangeKeysForEncryption|DiffieHellman|InitEncryption|EncryptionData|class .*Encryptor|EncryptionMode" \
  decompiled/Photon3Unity3D/ decompiled/PhotonRealtime/ | head -25
```
Record: which `EncryptionMode` PUN uses, whether `ExchangeKeysForEncryption` (op 250) is sent
on connect, and which operations are encrypted.

- [ ] **Step 2: Add a temporary crypto trace to the op-logger and run the client**

Add a Harmony patch logging calls to the encryption-establishment method found in Step 1
(e.g. `PhotonPeer.EstablishEncryption` or the DH init), rebuild/redeploy the plugin (per
Phase 0 Task 5 Step 6), launch the game ~30s, then:

```bash
grep -iE "encrypt|exchangekeys|diffie" "/c/Program Files (x86)/Steam/steamapps/common/Black Ice/BepInEx/oplog.jsonl" | head
```
Expected: confirms whether the client initiates encryption before `Authenticate`.

- [ ] **Step 3: Implement the confirmed path**

Create `BlackIce.Photon.Crypto/EncryptionHandshake.cs`.

- **If the trace shows NO pre-auth encryption** (token-only auth, as the Phase 0 op-log
  suggested): implement a pass-through that records the auth token and performs no datagram
  crypto:

```csharp
namespace BlackIce.Photon.Crypto;

/// <summary>Encryption handshake. Phase 0 evidence indicates token-only auth (no datagram
/// crypto); this pass-through is replaced if the Task 7 spike shows otherwise.</summary>
public sealed class EncryptionHandshake
{
    public bool RequiresKeyExchange => false;
    public byte[]? SharedSecret { get; private set; }

    /// <summary>Called if the client sends ExchangeKeysForEncryption (op 250).</summary>
    public byte[] OnKeyExchange(byte[] clientPublicKey)
        => throw new NotSupportedException("Key exchange not required per spike; see Task 7.");
}
```

- **If the trace shows DH key exchange:** implement Photon's curve25519/OakleyGroup DH (the
  algorithm is in `ExitGames.Client.Photon.Encryption` in the decompiled source) to derive
  `SharedSecret` from the client public key, returning the server public key. Port the exact
  curve/KDF from the decompiled source; verify the derived secret decrypts a captured
  encrypted command.

- **Documented fallback:** if encryption blocks Phase 1, set the client's `EncryptionMode` to
  none via the BepInEx mod and proceed; record this in `server/ORACLE.md`.

- [ ] **Step 4: Commit**

```bash
cd "$REPO" && git add server/ plugins/ && git commit -m "feat(crypto): encryption handshake per client-path spike"
```

---

## Task 8: Server core — UDP loop, peers, dispatch

**Files:**
- Create: `server/BlackIce.Server.Core/UdpListener.cs`, `PeerConnection.cs`, `IOperationHandler.cs`, `OperationDispatcher.cs`
- Create: `server/BlackIce.Photon/Transport/EnetPeer.cs`, `EnetChannel.cs`
- Test: `server/BlackIce.Server.Tests/TransportLoopTests.cs`

- [ ] **Step 1: Define the role-handler interface**

Create `BlackIce.Server.Core/IOperationHandler.cs`:

```csharp
using BlackIce.Photon;

namespace BlackIce.Server.Core;

/// <summary>A server role (Name/Master/Game) reacts to an operation and may reply / raise events.</summary>
public interface IOperationHandler
{
    void OnConnect(PeerConnection peer);
    void OnOperationRequest(PeerConnection peer, OperationRequest request);
    void OnDisconnect(PeerConnection peer);
}
```

- [ ] **Step 2: Implement EnetPeer reliable state (failing test first)**

Create `BlackIce.Server.Tests/TransportLoopTests.cs`:

```csharp
using BlackIce.Photon.Transport;
using Xunit;

namespace BlackIce.Server.Tests;

public class TransportLoopTests
{
    [Fact]
    public void Connect_yields_verifyconnect()
    {
        var peer = new EnetPeer();
        var outCmds = peer.HandleIncoming(new NCommand(NCommand.Connect, 0, 1, 4, 1, Array.Empty<byte>()));
        Assert.Contains(outCmds, c => c.CommandType == NCommand.VerifyConnect);
    }

    [Fact]
    public void Reliable_command_is_acked()
    {
        var peer = new EnetPeer();
        peer.HandleIncoming(new NCommand(NCommand.Connect, 0, 1, 4, 1, Array.Empty<byte>()));
        var outCmds = peer.HandleIncoming(new NCommand(NCommand.SendReliable, 0, 1, 4, 2, new byte[] { 1 }));
        Assert.Contains(outCmds, c => c.CommandType == NCommand.Acknowledge);
    }
}
```

Create `BlackIce.Photon/Transport/EnetChannel.cs` and `EnetPeer.cs` implementing the minimal
reliable path: respond to `Connect` with `VerifyConnect` (assigning a PeerId), `Ack` every
incoming reliable command, surface reassembled reliable payloads, and queue outgoing reliable
commands with incrementing sequence numbers per channel. (Fragmentation/unreliable deferred.)

```csharp
namespace BlackIce.Photon.Transport;

public sealed class EnetChannel
{
    public int OutgoingReliableSequenceNumber;
    public int IncomingReliableSequenceNumber;
}

public sealed class EnetPeer
{
    private static short _nextPeerId = 1;
    public short PeerId { get; private set; } = -1;
    private readonly EnetChannel _ch0 = new();

    /// <summary>Processes one incoming command, returns commands to send back.</summary>
    public List<NCommand> HandleIncoming(NCommand cmd)
    {
        var outgoing = new List<NCommand>();
        switch (cmd.CommandType)
        {
            case NCommand.Connect:
                PeerId = _nextPeerId++;
                outgoing.Add(new NCommand(NCommand.VerifyConnect, 0, 1, 4, NextSeq(), BitConverter.GetBytes(PeerId)));
                break;
            case NCommand.SendReliable:
                outgoing.Add(new NCommand(NCommand.Acknowledge, cmd.ChannelId, 0, 4, cmd.ReliableSequenceNumber, Array.Empty<byte>()));
                ReceivedReliablePayload?.Invoke(cmd.Payload);
                break;
            case NCommand.Ping:
                outgoing.Add(new NCommand(NCommand.Acknowledge, cmd.ChannelId, 0, 4, cmd.ReliableSequenceNumber, Array.Empty<byte>()));
                break;
        }
        return outgoing;
    }

    public event Action<byte[]>? ReceivedReliablePayload;

    public NCommand WrapReliable(byte[] payload) =>
        new(NCommand.SendReliable, 0, 1, 4, NextSeq(), payload);

    private int NextSeq() => ++_ch0.OutgoingReliableSequenceNumber;
}
```

- [ ] **Step 3: Run transport-loop tests**

```bash
cd "$REPO/server" && dotnet test BlackIce.Server.Tests --filter TransportLoopTests 2>&1 | tail -6
```
Expected: PASS.

- [ ] **Step 4: Implement the UDP listener + peer connection + dispatcher**

Create `BlackIce.Server.Core/PeerConnection.cs` (owns an `EnetPeer`, a remote endpoint, and a
`Send` callback that frames commands into a packet), `UdpListener.cs` (a `UdpClient` receive
loop that parses the packet header + commands, feeds each to the peer, and sends back queued
commands wrapped in a `PhotonHeader`), and `OperationDispatcher.cs` (decodes reliable payloads
into `OperationRequest` via `MessageSerializer` and forwards to the role's `IOperationHandler`;
serializes replies via `MessageSerializer` and wraps them with `EnetPeer.WrapReliable`).

Key listener logic:

```csharp
using System.Net;
using System.Net.Sockets;
using BlackIce.Photon.Transport;

namespace BlackIce.Server.Core;

/// <summary>Receives Photon UDP datagrams for one role and routes commands to peers.</summary>
public sealed class UdpListener
{
    private readonly UdpClient _socket;
    private readonly IOperationHandler _handler;
    private readonly Dictionary<IPEndPoint, PeerConnection> _peers = new();

    public UdpListener(int port, IOperationHandler handler)
    {
        _socket = new UdpClient(port);
        _handler = handler;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var result = await _socket.ReceiveAsync(ct);
            try { Process(result.Buffer, result.RemoteEndPoint); }
            catch (Exception ex) { Console.Error.WriteLine($"drop packet from {result.RemoteEndPoint}: {ex.Message}"); }
        }
    }

    private void Process(byte[] datagram, IPEndPoint from)
    {
        var header = PhotonHeader.ReadFrom(datagram);
        if (!_peers.TryGetValue(from, out var peer))
        {
            peer = new PeerConnection(from, cmds => SendCommands(from, cmds), _handler);
            _peers[from] = peer;
        }
        int offset = PhotonHeader.Size;
        for (int i = 0; i < header.CommandCount; i++)
        {
            var cmd = NCommand.Parse(datagram.AsSpan(offset), out int consumed);
            offset += consumed;
            peer.HandleCommand(cmd);
        }
    }

    private void SendCommands(IPEndPoint to, IReadOnlyList<NCommand> cmds)
    {
        if (cmds.Count == 0) return;
        var body = cmds.SelectMany(c => c.ToBytes()).ToArray();
        var packet = new byte[PhotonHeader.Size + body.Length];
        new PhotonHeader(0, false, (byte)cmds.Count, Environment.TickCount, 0).WriteTo(packet);
        body.CopyTo(packet, PhotonHeader.Size);
        _socket.Send(packet, packet.Length, to);
    }
}
```

`PeerConnection.HandleCommand` feeds the command to its `EnetPeer`, sends the returned
commands, and on a reliable payload invokes the dispatcher → `_handler.OnOperationRequest`.

- [ ] **Step 5: Build (integration covered in Task 13)**

```bash
cd "$REPO/server" && dotnet build 2>&1 | tail -3
```
Expected: `Build succeeded.`

- [ ] **Step 6: Commit**

```bash
cd "$REPO" && git add server/ && git commit -m "feat(core): UDP listener, peer lifecycle, reliable transport loop, dispatch"
```

---

## Task 9: Name Server role

**Files:**
- Create: `server/BlackIce.Server.LoadBalancing/AuthToken.cs`, `NameServerHandler.cs`
- Test: `server/BlackIce.Server.Tests/NameServerTests.cs`

- [ ] **Step 1: Failing test — Authenticate returns a Master address + token**

Create `BlackIce.Server.Tests/NameServerTests.cs`:

```csharp
using BlackIce.Photon;
using BlackIce.Server.LoadBalancing;
using Xunit;

namespace BlackIce.Server.Tests;

public class NameServerTests
{
    [Fact]
    public void Authenticate_returns_master_address_and_token()
    {
        var ns = new NameServerHandler(masterAddress: "127.0.0.1:5055", secret: "test-secret");
        var resp = ns.Authenticate(new OperationRequest(230, new()
        {
            { 220, "Early Access v0.9.226_2.20.1" }, { 224, "app-id" }, { 210, "us/*" },
        }));
        Assert.Equal(0, resp.ReturnCode);
        Assert.Equal("127.0.0.1:5055", resp.Parameters[230]); // ParameterCode.Address
        Assert.True(resp.Parameters.ContainsKey(221));         // token (Secret)
        Assert.True(resp.Parameters.ContainsKey(225));         // UserId
    }
}
```

- [ ] **Step 2: Run to verify failure**

```bash
cd "$REPO/server" && dotnet test BlackIce.Server.Tests --filter NameServerTests 2>&1 | tail -5
```
Expected: FAIL.

- [ ] **Step 3: Implement AuthToken (HMAC-signed) and the handler**

Create `BlackIce.Server.LoadBalancing/AuthToken.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;

namespace BlackIce.Server.LoadBalancing;

/// <summary>Opaque token minted by the Name Server, validated by Master/Game. HMAC-signed userId.</summary>
public static class AuthToken
{
    public static string Mint(string userId, string secret)
    {
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var sig = Convert.ToBase64String(h.ComputeHash(Encoding.UTF8.GetBytes(userId)));
        return $"{userId}.{sig}";
    }

    public static bool TryValidate(string token, string secret, out string userId)
    {
        userId = "";
        var parts = token.Split('.', 2);
        if (parts.Length != 2) return false;
        if (Mint(parts[0], secret) != token) return false;
        userId = parts[0];
        return true;
    }
}
```

Create `NameServerHandler.cs`:

```csharp
using BlackIce.Photon;
using BlackIce.Server.Core;

namespace BlackIce.Server.LoadBalancing;

/// <summary>Photon Name Server role: authenticates and hands the client to the Master Server.</summary>
public sealed class NameServerHandler : IOperationHandler
{
    private readonly string _masterAddress;
    private readonly string _secret;
    public NameServerHandler(string masterAddress, string secret) { _masterAddress = masterAddress; _secret = secret; }

    public void OnConnect(PeerConnection peer) { }
    public void OnDisconnect(PeerConnection peer) { }

    public void OnOperationRequest(PeerConnection peer, OperationRequest request)
    {
        if (request.OperationCode == 230) peer.SendResponse(Authenticate(request));
    }

    public OperationResponse Authenticate(OperationRequest request)
    {
        var userId = Guid.NewGuid().ToString();
        return new OperationResponse(230, 0, null, new()
        {
            { 230, _masterAddress },                 // ParameterCode.Address -> Master
            { 221, AuthToken.Mint(userId, _secret) },// ParameterCode.Secret/Token
            { 225, userId },                          // ParameterCode.UserId
        });
    }
}
```

(Add `PeerConnection.SendResponse(OperationResponse)` that serializes via `MessageSerializer`
and sends through the peer's `EnetPeer.WrapReliable`.)

- [ ] **Step 4: Run tests**

```bash
cd "$REPO/server" && dotnet test BlackIce.Server.Tests --filter NameServerTests 2>&1 | tail -5
```
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
cd "$REPO" && git add server/ && git commit -m "feat(lb): Name Server role + HMAC auth token"
```

---

## Task 10: Master Server role

**Files:**
- Create: `server/BlackIce.Server.LoadBalancing/MasterServerHandler.cs`
- Test: `server/BlackIce.Server.Tests/MasterServerTests.cs`

- [ ] **Step 1: Failing tests — Authenticate(token), JoinLobby, CreateGame→Game address**

Create `BlackIce.Server.Tests/MasterServerTests.cs`:

```csharp
using BlackIce.Photon;
using BlackIce.Server.LoadBalancing;
using Xunit;

namespace BlackIce.Server.Tests;

public class MasterServerTests
{
    private static MasterServerHandler New() =>
        new(gameAddress: "127.0.0.1:5056", secret: "test-secret", registry: new RoomRegistry());

    [Fact]
    public void Authenticate_with_valid_token_succeeds()
    {
        var token = AuthToken.Mint("user-1", "test-secret");
        var resp = New().Authenticate(new OperationRequest(230, new() { { 221, token } }));
        Assert.Equal(0, resp.ReturnCode);
    }

    [Fact]
    public void CreateGame_returns_game_server_address()
    {
        var resp = New().CreateGame(new OperationRequest(227, new() { { 255, "Room #1" } }));
        Assert.Equal(0, resp.ReturnCode);
        Assert.Equal("127.0.0.1:5056", resp.Parameters[230]); // Address -> Game Server
    }

    [Fact]
    public void JoinLobby_succeeds()
    {
        Assert.Equal(0, New().JoinLobby(new OperationRequest(229, new())).ReturnCode);
    }
}
```

- [ ] **Step 2: Run to verify failure**

```bash
cd "$REPO/server" && dotnet test BlackIce.Server.Tests --filter MasterServerTests 2>&1 | tail -5
```
Expected: FAIL.

- [ ] **Step 3: Implement the Master handler and the room registry stub**

Create `RoomRegistry.cs` and `Room.cs`:

```csharp
namespace BlackIce.Server.LoadBalancing;

public sealed class Room
{
    public required string Name { get; init; }
    public Dictionary<string, object> Properties { get; } = new();
    public List<int> ActorNumbers { get; } = new();
}

public sealed class RoomRegistry
{
    private readonly Dictionary<string, Room> _rooms = new();
    public Room GetOrCreate(string name) => _rooms.TryGetValue(name, out var r) ? r : (_rooms[name] = new Room { Name = name });
    public IReadOnlyCollection<Room> All => _rooms.Values;
}
```

Create `MasterServerHandler.cs`:

```csharp
using BlackIce.Photon;
using BlackIce.Server.Core;

namespace BlackIce.Server.LoadBalancing;

/// <summary>Photon Master Server role: lobby + matchmaking; routes the client to the Game Server.</summary>
public sealed class MasterServerHandler : IOperationHandler
{
    private readonly string _gameAddress;
    private readonly string _secret;
    private readonly RoomRegistry _registry;
    public MasterServerHandler(string gameAddress, string secret, RoomRegistry registry)
    { _gameAddress = gameAddress; _secret = secret; _registry = registry; }

    public void OnConnect(PeerConnection peer) { }
    public void OnDisconnect(PeerConnection peer) { }

    public void OnOperationRequest(PeerConnection peer, OperationRequest request)
    {
        OperationResponse resp = request.OperationCode switch
        {
            230 => Authenticate(request),
            229 => JoinLobby(request),
            227 => CreateGame(request),
            226 => CreateGame(request),               // JoinGame also routed to a game server
            _ => new OperationResponse(request.OperationCode, -2, "Unknown operation", new()),
        };
        peer.SendResponse(resp);
        if (request.OperationCode == 229) peer.RaiseEvent(new EventData(230, new() { { 1, Array.Empty<byte>() } })); // GameList (empty)
    }

    public OperationResponse Authenticate(OperationRequest r)
        => r.Parameters.TryGetValue(221, out var t) && t is string token && AuthToken.TryValidate(token, _secret, out _)
            ? new OperationResponse(230, 0, null, new())
            : new OperationResponse(230, -1, "Invalid token", new());

    public OperationResponse JoinLobby(OperationRequest r) => new(229, 0, null, new());

    public OperationResponse CreateGame(OperationRequest r)
    {
        var name = r.Parameters.TryGetValue(255, out var n) ? n.ToString()! : $"Room #{Guid.NewGuid():N}";
        _registry.GetOrCreate(name);
        return new OperationResponse(r.OperationCode, 0, null, new()
        {
            { 230, _gameAddress },  // Address -> Game Server
            { 255, name },          // RoomName
        });
    }
}
```

(Add `PeerConnection.RaiseEvent(EventData)` mirroring `SendResponse` but using
`MessageSerializer.SerializeEvent`.)

- [ ] **Step 4: Run tests**

```bash
cd "$REPO/server" && dotnet test BlackIce.Server.Tests --filter MasterServerTests 2>&1 | tail -5
```
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
cd "$REPO" && git add server/ && git commit -m "feat(lb): Master Server role + room registry"
```

---

## Task 11: Game Server role

**Files:**
- Create: `server/BlackIce.Server.LoadBalancing/GameServerHandler.cs`
- Test: `server/BlackIce.Server.Tests/GameServerTests.cs`

- [ ] **Step 1: Failing tests — Authenticate(token), CreateGame assigns actor + Join event**

Create `BlackIce.Server.Tests/GameServerTests.cs`:

```csharp
using BlackIce.Photon;
using BlackIce.Server.LoadBalancing;
using Xunit;

namespace BlackIce.Server.Tests;

public class GameServerTests
{
    private static GameServerHandler New() => new(secret: "test-secret", registry: new RoomRegistry());

    [Fact]
    public void CreateGame_assigns_actor_and_returns_room_props()
    {
        var (resp, joinEvent) = New().EnterRoom(new OperationRequest(227, new() { { 255, "Room #1" } }));
        Assert.Equal(0, resp.ReturnCode);
        Assert.True(resp.Parameters.ContainsKey(254));   // ActorNr assigned
        Assert.Equal(255, joinEvent.Code);                // Join event raised
    }
}
```

- [ ] **Step 2: Run to verify failure**

```bash
cd "$REPO/server" && dotnet test BlackIce.Server.Tests --filter GameServerTests 2>&1 | tail -5
```
Expected: FAIL.

- [ ] **Step 3: Implement the Game handler**

Create `GameServerHandler.cs`:

```csharp
using BlackIce.Photon;
using BlackIce.Server.Core;

namespace BlackIce.Server.LoadBalancing;

/// <summary>Photon Game Server role: enters the actual room and emits the Join event.</summary>
public sealed class GameServerHandler : IOperationHandler
{
    private readonly string _secret;
    private readonly RoomRegistry _registry;
    private int _nextActor;
    public GameServerHandler(string secret, RoomRegistry registry) { _secret = secret; _registry = registry; }

    public void OnConnect(PeerConnection peer) { }
    public void OnDisconnect(PeerConnection peer) { }

    public void OnOperationRequest(PeerConnection peer, OperationRequest request)
    {
        switch (request.OperationCode)
        {
            case 230:
                peer.SendResponse(request.Parameters.TryGetValue(221, out var t) && t is string token && AuthToken.TryValidate(token, _secret, out _)
                    ? new OperationResponse(230, 0, null, new())
                    : new OperationResponse(230, -1, "Invalid token", new()));
                break;
            case 227:
            case 226:
                var (resp, join) = EnterRoom(request);
                peer.SendResponse(resp);
                peer.RaiseEvent(join);
                break;
            default:
                peer.SendResponse(new OperationResponse(request.OperationCode, -2, "Unknown operation", new()));
                break;
        }
    }

    public (OperationResponse, EventData) EnterRoom(OperationRequest r)
    {
        var name = r.Parameters.TryGetValue(255, out var n) ? n.ToString()! : "room";
        var room = _registry.GetOrCreate(name);
        int actor = ++_nextActor;
        room.ActorNumbers.Add(actor);
        var resp = new OperationResponse(r.OperationCode, 0, null, new()
        {
            { 254, actor },                                  // ActorNr
            { 248, new Dictionary<byte, object>() },          // game (room) properties
            { 249, new Dictionary<byte, object>() },          // actor properties
        });
        var join = new EventData(255, new() { { 254, actor }, { 252, new int[] { actor } } }); // Join: actorNr + actor list
        return (resp, join);
    }
}
```

- [ ] **Step 4: Run tests**

```bash
cd "$REPO/server" && dotnet test BlackIce.Server.Tests --filter GameServerTests 2>&1 | tail -5
```
Expected: PASS.

- [ ] **Step 5: Wire the host entrypoint**

Create `BlackIce.Server.Host/ServerConfig.cs` and `Program.cs` to start three `UdpListener`s
(Name 5058, Master 5055, Game 5056) sharing one `RoomRegistry` and `secret`, bound to the
configured address:

```csharp
using BlackIce.Server.Core;
using BlackIce.Server.LoadBalancing;

var bind = args.Length > 0 ? args[0] : "0.0.0.0";
var secret = "change-me-phase1";
var registry = new RoomRegistry();
var listeners = new[]
{
    new UdpListener(5058, new NameServerHandler($"{bind}:5055", secret)),
    new UdpListener(5055, new MasterServerHandler($"{bind}:5056", secret, registry)),
    new UdpListener(5056, new GameServerHandler(secret, registry)),
};
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
Console.WriteLine($"BlackIce.Server listening on {bind} (NS 5058 / Master 5055 / Game 5056)");
await Task.WhenAll(listeners.Select(l => l.RunAsync(cts.Token)));
```

- [ ] **Step 6: Build + commit**

```bash
cd "$REPO/server" && dotnet build 2>&1 | tail -3
cd "$REPO" && git add server/ && git commit -m "feat(lb): Game Server role + host entrypoint (NS/Master/Game)"
```

---

## Task 12: Realmlist-style redirect mod

**Files:**
- Create: `plugins/BlackIce.Redirect/BlackIce.Redirect.csproj`, `RedirectPlugin.cs`

- [ ] **Step 1: Scaffold the plugin (mirror the op-logger project setup)**

```bash
cd "$REPO/plugins"
dotnet new classlib -n BlackIce.Redirect -o BlackIce.Redirect --framework netstandard2.0 >/dev/null
rm -f BlackIce.Redirect/Class1.cs
```
Replace the csproj with the same `<Reference>` block as `BlackIce.OpLogger` (BepInEx, 0Harmony,
UnityEngine, UnityEngine.CoreModule, PhotonRealtime, PhotonUnityNetworking), plus
`<Nullable>annotations</Nullable>` and `<LangVersion>latest</LangVersion>`.

- [ ] **Step 2: Implement the config-driven redirect**

Create `BlackIce.Redirect/RedirectPlugin.cs`:

```csharp
using BepInEx;
using BepInEx.Configuration;
using Photon.Pun;

namespace BlackIce.Redirect;

/// <summary>Realmlist-style redirect: points the client at a custom server via one config value.</summary>
[BepInPlugin("blackice.redirect", "BlackIce Server Redirect", "0.1.0")]
public sealed class RedirectPlugin : BaseUnityPlugin
{
    private void Awake()
    {
        var address = Config.Bind("Server", "ServerAddress", "127.0.0.1",
            "Custom BlackIce server address (the Name Server). Like WoW's realmlist.").Value;
        var port = Config.Bind("Server", "Port", 5058, "Name Server UDP port.").Value;

        var settings = PhotonNetwork.PhotonServerSettings.AppSettings;
        settings.Server = address;
        settings.Port = port;
        settings.UseNameServer = true;   // client does full Name->Master->Game against us
        Logger.LogInfo($"BlackIce redirect active -> {address}:{port}");
    }
}
```

- [ ] **Step 3: Build and deploy**

```bash
cd "$REPO/plugins/BlackIce.Redirect" && dotnet build -c Release 2>&1 | grep -E "Build succeeded|error" | head
cp bin/Release/netstandard2.0/BlackIce.Redirect.dll "/c/Program Files (x86)/Steam/steamapps/common/Black Ice/BepInEx/plugins/"
echo "deployed"
```
Expected: `Build succeeded.` and the DLL copied.

- [ ] **Step 4: Commit (source only)**

```bash
cd "$REPO" && git add plugins/BlackIce.Redirect/ && git status -s | grep -iE '/bin/|/obj/|\.dll$' || echo clean
git commit -m "feat(redirect): realmlist-style config-driven server redirect mod"
```

---

## Task 13: Integration — real client reaches in-room

**Files:** none (acceptance test using the live client + op-logger).

- [ ] **Step 1: Run the server**

```bash
cd "$REPO/server" && dotnet run --project BlackIce.Server.Host 127.0.0.1 &
sleep 2 && echo "server started"
```
Expected: prints the listening banner.

- [ ] **Step 2: Clear the op-log and launch the redirected client**

Clear `$GAME/BepInEx/oplog.jsonl`, ensure both `BlackIce.OpLogger.dll` and
`BlackIce.Redirect.dll` are in `$GAME/BepInEx/plugins`, launch the game ~40s (it auto-connects),
then stop it (use the Phase 0 PowerShell launch/stop pattern).

- [ ] **Step 3: Verify the client reached the Game Server and Join**

```bash
cd "$REPO/tools/capture"
python parse_oplog.py "/c/Program Files (x86)/Steam/steamapps/common/Black Ice/BepInEx/oplog.jsonl" \
  "$REPO/docs/protocol/generated/photon_constants.csv" | head -30
```
Expected: a timeline showing `send Authenticate` → `response Authenticate` (to our NS),
then Master ops, then a Game Server `response CreateGame` and an `event Join` — i.e. the
client connected through our three servers and reached the in-room state. Confirm the server
console logged peers on all three ports.

- [ ] **Step 4: Stop the server and record the result**

```bash
kill %1 2>/dev/null; echo "server stopped"
```
Append the observed timeline (sanitized) to `docs/protocol/01-connection-flow.md` under a new
"Verified against BlackIce.Server (Phase 1)" section.

- [ ] **Step 5: Commit**

```bash
cd "$REPO" && git add docs/ && git commit -m "test(phase1): client reaches in-room through BlackIce.Server"
```

> If the client stalls before `Join`, debug with: the server console (which op failed), the
> op-logger (last successful op), and dnSpyEx on the client (break in the PUN state machine).
> Most likely culprits, in order: (1) encryption mismatch (revisit Task 7), (2) a serialization
> byte mismatch the oracle tests didn't cover (add a test for that operation), (3) a missing
> expected parameter in a response (compare against a real Photon response in the op-log).

---

## Self-Review

**Spec coverage:**
- §3 architecture (one process, 3 ports, 7 projects) → Task 1 + Task 11 Step 5. ✓
- §3 protocol library (transport + GpBinary) → Tasks 3–6. ✓
- §3 crypto (isolated) → Task 7. ✓
- §3 core (loop/peer/dispatch) → Task 8. ✓
- §3 LoadBalancing roles + registry → Tasks 9–11. ✓
- §3 redirect mod (realmlist) → Task 12. ✓
- §4 data flow → exercised end-to-end in Task 13. ✓
- §5 encryption spike-first → Task 7 Steps 1–2 before impl. ✓
- §6 testing (oracle round-trip, transport units, role units, integration) → Tasks 3–6 (oracle), 6/8 (transport), 9–11 (roles), 13 (integration). ✓
- §7 error handling (drop malformed, unknown-op returnCode) → Task 8 Step 4 (try/catch drop), Tasks 10/11 (`-2` unknown-op). ✓
- §2/§8 DoD (client in-room) → Task 13. ✓

**Placeholder scan:** No "TBD/TODO". Adaptive points are gated by explicit ground-truth steps:
oracle API (Task 2), oracle reconciliation (Tasks 3–5 final steps), encryption spike (Task 7
Steps 1–2). These mirror Phase 0's "confirm signature against the binary" pattern and are
correct for clean-room RE, not vagueness.

**Type consistency:** `OperationRequest/OperationResponse/EventData` (Task 5) are used
unchanged in Tasks 9–11. `MessageSerializer.Serialize{Request,Response,Event}` (Task 5) are
called by `PeerConnection.SendResponse/RaiseEvent` (Tasks 8–10). `NCommand` constants and
`EnetPeer.WrapReliable` (Tasks 6, 8) are consistent. `RoomRegistry.GetOrCreate` (Task 10) is
reused in Task 11. `AuthToken.Mint/TryValidate` (Task 9) used by Tasks 10–11. ParameterCode
byte keys (230 Address, 221 Secret, 225 UserId, 255 RoomName, 254 ActorNr, 248/249 props)
match `docs/protocol/02-operations.md`.

**Known reconciliation risks (by design, gated):** GpBinary Dictionary header layout (Task 4),
v1.8 string length width (Task 3), the encryption handshake (Task 7), and exact response
parameter sets (Task 13). Each has an explicit oracle/spike/debug step.
```
