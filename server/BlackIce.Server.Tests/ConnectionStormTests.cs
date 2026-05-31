using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using BlackIce.Photon;
using BlackIce.Server.Core;
using BlackIce.Server.LoadBalancing;
using Xunit;

namespace BlackIce.Server.Tests;

/// <summary>
/// Concurrent connection-storm chaos on the relay. The relay is designed to run on the single UDP
/// listener thread, but commit 8d49495 hardened <see cref="BlackIce.Photon.Transport.EnetPeer"/>'s
/// sequence state against stray cross-thread sends as defense-in-depth. These tests apply the same
/// scrutiny to the rest of the relay path: many threads simultaneously joining, leaving, relaying, and
/// replaying — looking for unguarded shared mutable state that a stray cross-thread caller would
/// corrupt (a corrupted Dictionary mid-resize throws and would kill the listener loop).
/// </summary>
public class ConnectionStormTests
{
    private const int Threads = 16;
    private const int PerThread = 5000;

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

    /// <summary>A well-formed PUN position event (201) for a viewId — parses cleanly into PositionInfo,
    /// so it drives the MovementValidationInterceptor's per-view state map.</summary>
    private static EventData Position(int viewId, float x, float y, float z)
    {
        var b = new byte[12];
        System.Buffers.Binary.BinaryPrimitives.WriteSingleBigEndian(b.AsSpan(0), x);
        System.Buffers.Binary.BinaryPrimitives.WriteSingleBigEndian(b.AsSpan(4), y);
        System.Buffers.Binary.BinaryPrimitives.WriteSingleBigEndian(b.AsSpan(8), z);
        var view = new object[] { viewId, false, null!, new PhotonCustomData(86, b) };
        return new EventData(201, new() { { 245, new object[] { 0, null!, view } } });
    }

    private static void Storm(Action<int> body)
    {
        using var ready = new Barrier(Threads);
        var tasks = new Task[Threads];
        var errors = new ConcurrentBag<Exception>();
        for (int t = 0; t < Threads; t++)
        {
            int id = t;
            tasks[t] = Task.Factory.StartNew(() =>
            {
                ready.SignalAndWait();
                try { body(id); }
                catch (Exception ex) { errors.Add(ex); }
            }, TaskCreationOptions.LongRunning);
        }
        Task.WaitAll(tasks);
        if (!errors.IsEmpty)
            throw new AggregateException($"{errors.Count} thread(s) threw during the storm", errors);
    }

    [Fact]
    public void Concurrent_relay_of_position_events_through_the_authority_chain_is_race_free()
    {
        // Real chain: DamageValidationInterceptor + MovementValidationInterceptor + Passthrough. The
        // movement validator keeps a per-(room,viewId) Dictionary of last positions, written on every
        // 201. Hammering RelayFrom from many threads with MANY distinct viewIds forces that Dictionary
        // to grow/resize while other threads read+write it. If it is not concurrency-safe, a resize
        // races and throws (IndexOutOfRange / InvalidOperation) — and in production that exception is
        // on the listener thread.
        var session = new RoomRegistry().Session("co-op");
        for (int a = 1; a <= 4; a++) session.Join(a, Peer());

        Storm(id =>
        {
            var rng = new Random(id);
            for (int i = 0; i < PerThread; i++)
            {
                int viewId = rng.Next(0, 4000);   // wide spread -> dictionary churn + resizes
                session.RelayFrom(senderActor: 1 + (id % 4), Position(viewId, i, id, i * 0.5f));
            }
        });
    }

    [Fact]
    public void Concurrent_join_leave_relay_replay_on_one_session_is_race_free()
    {
        // Membership churn (Join/Leave) concurrent with fan-out (RelayFrom) and late-joiner replay
        // (ReplayCacheTo). Stresses the _gate-guarded membership map + spawn cache from every angle at
        // once. The _gate lock should make this safe; this proves it (and would catch a future change
        // that snapshots outside the lock).
        var session = new RoomRegistry().Session("co-op");
        session.Join(0, Peer());   // a stable anchor member

        Storm(id =>
        {
            var rng = new Random(id);
            int actor = 1000 + id;
            for (int i = 0; i < PerThread / 2; i++)
            {
                switch (rng.Next(5))
                {
                    case 0: session.Join(actor, Peer()); break;
                    case 1: session.Leave(actor); break;
                    case 2: session.RelayFrom(0, Position(actor, i, i, i)); break;
                    case 3: session.RelayFrom(0, new EventData(202, new() {
                                { 245, new Dictionary<object, object> { { (byte)0, "Player" }, { (byte)7, actor } } } })); break;
                    case 4: session.ReplayCacheTo(actor); break;
                }
            }
        });

        Assert.True(session.Count >= 1);   // the anchor never left
    }

    // ---- Probe: unguarded mutable state inside the authority interceptors ---------------------

    /// <summary>A 200 RPC event carrying a DamagePacket (custom code 68) of the given damage value.</summary>
    private static EventData DamageRpc(float damage)
    {
        var b = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteSingleBigEndian(b, damage);
        return new EventData(200, new()
        {
            { 245, new Dictionary<object, object>
                {
                    { (byte)0, 1 },
                    { (byte)3, "TakeDamage" },
                    { (byte)4, new object[] { new PhotonCustomData(68, b) } },
                } },
        });
    }

    [Fact]
    public void DamageValidator_FlaggedCount_is_exact_under_concurrent_intercepts()
    {
        // Phase 3a hardened the authority flag tally: FlaggedCount is now an Interlocked counter (and the
        // shared ViolationTracker uses Interlocked too). This is the same class of "relay-path mutable
        // state assumes a single thread" issue that commit 8d49495 hardened for EnetPeer — now closed.
        //
        // The interceptor must be at Warn or above to count at all (Observe is a pure forward), so this
        // drives a Warn-strictness validator. Hammered from many threads, the count must be EXACTLY the
        // number of flagged events — no lost updates, no phantom over-counts. The spec calls for
        // tightening this storm test from tolerant bounds to exact-equality; this is that test.
        var dmg = new BlackIce.Server.LoadBalancing.Authority.DamageValidationInterceptor(
            maxDamage: 1f,
            policy: new BlackIce.Server.LoadBalancing.Authority.AuthorityPolicy(
                BlackIce.Server.LoadBalancing.Authority.AuthorityStrictness.Warn),
            violations: new BlackIce.Server.LoadBalancing.Authority.ViolationTracker(
                int.MaxValue, System.TimeSpan.FromHours(1)));
        var ctx = new EventContext("co-op", senderActor: 1, DamageRpc(1000f), unreliable: false);

        int expected = Threads * PerThread;
        Storm(_ =>
        {
            for (int i = 0; i < PerThread; i++) dmg.Intercept(ctx);
        });

        Assert.Equal(expected, dmg.FlaggedCount);   // exact: Interlocked never loses or invents an increment
    }
}
