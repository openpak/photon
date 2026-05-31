using System;
using System.Collections.Generic;
using System.Net;
using BlackIce.Photon;
using BlackIce.Server.Core;
using BlackIce.Server.LoadBalancing;
using Xunit;

namespace BlackIce.Server.Tests;

/// <summary>
/// Packet edge-case fuzzing. A relayed event carries a loosely-typed Photon hashtable that originates
/// on the client, so the server must treat every shape as hostile: missing keys, wrong runtime types,
/// truncated custom-type blobs, degenerate arrays, absurd values. The parsers (<see cref="PositionInfo"/>,
/// <see cref="PunRpcInfo"/>) and the relay (<see cref="RoomSession"/>) must never throw an unhandled
/// exception — at worst they decline to extract a value and forward the event verbatim. A single
/// uncaught exception on the listener thread would take down the whole relay loop.
/// </summary>
public class RelayFuzzTests
{
    // ---- Parser fuzzing: PositionInfo (event 201) ---------------------------------------------

    private static PhotonCustomData Vec3(int byteLen)
    {
        var b = new byte[byteLen];
        for (int i = 0; i + 4 <= byteLen; i += 4) System.Buffers.Binary.BinaryPrimitives.WriteSingleBigEndian(b.AsSpan(i), i);
        return new PhotonCustomData(86, b);
    }

    public static IEnumerable<object[]> MalformedPositionEvents()
    {
        // Each is a 201 event whose PData(245) is degenerate in some way.
        yield return new object[] { new EventData(201, new()) };                                            // no 245 at all
        yield return new object[] { new EventData(201, new() { { 245, "not an array" } }) };                 // 245 wrong type
        yield return new object[] { new EventData(201, new() { { 245, new object[0] } }) };                  // empty batch
        yield return new object[] { new EventData(201, new() { { 245, new object[] { 1, null! } } }) };      // header only, no views
        yield return new object[] { new EventData(201, new() { { 245, new object[] { 1, null!, null! } } }) }; // null view entry
        yield return new object[] { new EventData(201, new() { { 245, new object[] { 1, null!, new object[] { 1, 2 } } } }) }; // view too short
        yield return new object[] { new EventData(201, new() { { 245, new object[] { 1, null!, new object[] { "notInt", false, null!, Vec3(12) } } } }) }; // viewId not int
        yield return new object[] { new EventData(201, new() { { 245, new object[] { 1, null!, new object[] { 5, false, null!, Vec3(8) } } } }) };         // vec3 truncated (<12B)
        yield return new object[] { new EventData(201, new() { { 245, new object[] { 1, null!, new object[] { 5, false, null!, Vec3(0) } } } }) };         // vec3 empty
        yield return new object[] { new EventData(201, new() { { 245, new object[] { 1, null!, new object[] { int.MinValue, false, null!, Vec3(12) } } } }) }; // extreme viewId
    }

    [Theory]
    [MemberData(nameof(MalformedPositionEvents))]
    public void PositionInfo_From_never_throws_on_malformed_201(EventData ev)
    {
        var ex = Record.Exception(() => PositionInfo.From(ev));
        Assert.Null(ex);
    }

    [Fact]
    public void PositionInfo_From_ignores_non_201_events()
    {
        Assert.Null(PositionInfo.From(new EventData(200, new() { { 245, new object[] { 1, null!, new object[] { 5, false, null!, Vec3(12) } } } })));
    }

    // ---- Parser fuzzing: PunRpcInfo (event 200) -----------------------------------------------

    public static IEnumerable<object[]> MalformedRpcEvents()
    {
        yield return new object[] { new EventData(200, new()) };                                             // no 245
        yield return new object[] { new EventData(200, new() { { 245, 12345 } }) };                          // 245 not a dictionary
        yield return new object[] { new EventData(200, new() { { 245, new Dictionary<object, object>() } }) }; // empty rpc table
        yield return new object[] { new EventData(200, new() { { 245, new Dictionary<object, object> { { (byte)0, "viewIdNotInt" } } } }) }; // viewId wrong type
        yield return new object[] { new EventData(200, new() { { 245, new Dictionary<object, object> { { (byte)4, "argsNotArray" } } } }) };  // args wrong type
        yield return new object[] { new EventData(200, new() { { 245, new Dictionary<object, object> { { (byte)4, new object[] { new PhotonCustomData(68, new byte[2]) } } } } }) }; // damage packet truncated (<4B)
        yield return new object[] { new EventData(200, new() { { 245, new Dictionary<object, object> { { (byte)4, new object[] { new PhotonCustomData(68, Array.Empty<byte>()) } } } } }) }; // damage packet empty
        yield return new object[] { new EventData(200, new() { { 245, new Dictionary<object, object> { { (byte)3, 9999 } } } }) }; // method name slot holds an int, not a string
    }

    [Theory]
    [MemberData(nameof(MalformedRpcEvents))]
    public void PunRpcInfo_From_never_throws_on_malformed_200(EventData ev)
    {
        var ex = Record.Exception(() => PunRpcInfo.From(ev));
        Assert.Null(ex);
    }

    // ---- Relay fuzzing: malformed events through the REAL chain (with the authority validators) -

    private sealed class NullHandler : IOperationHandler
    {
        public void OnConnect(PeerConnection peer) { }
        public void OnOperationRequest(PeerConnection peer, OperationRequest request) { }
        public void OnDisconnect(PeerConnection peer) { }
    }

    private static PeerConnection Peer()
    {
        var p = new PeerConnection("test", new IPEndPoint(IPAddress.Loopback, 0), new NullHandler(), (_, _) => { });
        p.OnRaised = _ => { };
        return p;
    }

    public static IEnumerable<object[]> MalformedSpawnAndDestroyEvents()
    {
        yield return new object[] { new EventData(202, new()) };                                             // 202 no 245
        yield return new object[] { new EventData(202, new() { { 245, "notADict" } }) };                     // 202 245 wrong type
        yield return new object[] { new EventData(202, new() { { 245, new Dictionary<object, object> { { (byte)0, "Orphan" } } } }) }; // 202 no viewId -> synthetic key
        yield return new object[] { new EventData(202, new() { { 245, new Dictionary<object, object> { { (byte)7, "viewIdNotInt" } } } }) }; // 202 viewId wrong type
        yield return new object[] { new EventData(202, new() { { 245, new Dictionary<object, object> { { (byte)7, int.MaxValue } } } }) }; // 202 extreme viewId
        yield return new object[] { new EventData(204, new()) };                                             // 204 no 245
        yield return new object[] { new EventData(204, new() { { 245, new Dictionary<object, object>() } }) }; // 204 nothing resolvable
        yield return new object[] { new EventData(204, new() { { 245, new Dictionary<object, object> { { (byte)7, 999999 } } } }) }; // 204 destroys a never-spawned viewId
    }

    [Theory]
    [MemberData(nameof(MalformedSpawnAndDestroyEvents))]
    public void RelayFrom_never_throws_on_malformed_spawn_or_destroy(EventData ev)
    {
        var session = new RoomRegistry().Session("co-op");   // real chain incl. damage + movement validators
        session.Join(1, Peer());
        session.Join(2, Peer());

        var ex = Record.Exception(() => { session.RelayFrom(1, ev); session.ReplayCacheTo(2); });
        Assert.Null(ex);
    }

    [Fact]
    public void RelayFrom_tolerates_malformed_201_and_200_through_the_authority_chain()
    {
        var session = new RoomRegistry().Session("co-op");
        session.Join(1, Peer());
        session.Join(2, Peer());

        foreach (var row in MalformedPositionEvents())
            Assert.Null(Record.Exception(() => session.RelayFrom(1, (EventData)row[0])));
        foreach (var row in MalformedRpcEvents())
            Assert.Null(Record.Exception(() => session.RelayFrom(1, (EventData)row[0])));
    }

    [Fact]
    public void RelayFrom_with_an_event_carrying_no_parameters_does_not_throw()
    {
        var session = new RoomRegistry().Session("co-op");
        session.Join(1, Peer());
        session.Join(2, Peer());

        // Codes that hit special handling (202/204) AND arbitrary codes, all with empty parameter maps.
        foreach (byte code in new byte[] { 0, 200, 201, 202, 204, 253, 255 })
            Assert.Null(Record.Exception(() => session.RelayFrom(1, new EventData(code, new()))));
    }
}
