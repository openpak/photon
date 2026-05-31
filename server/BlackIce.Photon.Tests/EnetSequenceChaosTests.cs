using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BlackIce.Photon.Transport;
using Xunit;

namespace BlackIce.Photon.Tests;

/// <summary>
/// Chaos/stress coverage for <see cref="EnetPeer"/>'s outgoing sequence state under concurrency.
///
/// Context: a stray cross-thread send that corrupts the per-channel sequence dictionaries stamps a
/// bad reliable sequence number, which the client treats as an unacked command and force-disconnects
/// the live peer ~10s later. Commit 8d49495 added <c>_seqLock</c> as defense-in-depth. These tests
/// hammer the wrap methods from many threads at once and assert the per-channel sequence streams stay
/// gap-free and duplicate-free — the property the client relies on. Without the lock, the non-atomic
/// read-modify-write in NextSeq / WrapUnreliable loses or repeats values under contention and these
/// assertions fail.
/// </summary>
public class EnetSequenceChaosTests
{
    private const int Threads = 16;
    private const int PerThread = 4000;

    /// <summary>Runs <paramref name="body"/> on <see cref="Threads"/> threads released simultaneously
    /// (a barrier maximizes lock contention) and waits for all to finish.</summary>
    private static void Storm(Action<int> body)
    {
        using var ready = new Barrier(Threads);
        var tasks = new Task[Threads];
        for (int t = 0; t < Threads; t++)
        {
            int id = t;
            tasks[t] = Task.Factory.StartNew(() =>
            {
                ready.SignalAndWait();
                body(id);
            }, TaskCreationOptions.LongRunning);
        }
        Task.WaitAll(tasks);
    }

    /// <summary>Asserts the sequence numbers are exactly the contiguous run [1..count] — every value
    /// produced once, none skipped, none duplicated. NextSeq pre-increments from 0, so the first is 1.</summary>
    private static void AssertContiguousFromOne(IReadOnlyCollection<int> seqs, int expectedCount)
    {
        Assert.Equal(expectedCount, seqs.Count);
        var distinct = new HashSet<int>(seqs);
        Assert.Equal(expectedCount, distinct.Count);                 // no duplicates
        Assert.Equal(1, seqs.Min());
        Assert.Equal(expectedCount, seqs.Max());                     // no gaps given count == distinct == max
    }

    [Fact]
    public void Concurrent_reliable_wraps_on_one_channel_yield_a_gapfree_sequence()
    {
        var peer = new EnetPeer();
        var seqs = new ConcurrentBag<int>();

        Storm(_ =>
        {
            for (int i = 0; i < PerThread; i++)
                seqs.Add(peer.WrapReliable(new byte[] { 0xF3 }, channel: 0).ReliableSequenceNumber);
        });

        AssertContiguousFromOne(seqs.ToArray(), Threads * PerThread);
    }

    [Fact]
    public void Concurrent_unreliable_wraps_on_one_channel_yield_a_gapfree_unreliable_sequence()
    {
        var peer = new EnetPeer();
        var seqs = new ConcurrentBag<int>();

        Storm(_ =>
        {
            for (int i = 0; i < PerThread; i++)
                seqs.Add(peer.WrapUnreliable(new byte[] { 0xF3 }, channel: 0).UnreliableSequenceNumber);
        });

        AssertContiguousFromOne(seqs.ToArray(), Threads * PerThread);
    }

    [Fact]
    public void Reliable_sequences_are_independent_per_channel_under_contention()
    {
        var peer = new EnetPeer();
        var ch0 = new ConcurrentBag<int>();
        var ch1 = new ConcurrentBag<int>();

        // Half the threads pound channel 0, half pound channel 1. Each channel must keep its own
        // gap-free run — proof the dictionaries are keyed per channel and the lock guards both.
        Storm(id =>
        {
            byte channel = (byte)(id % 2);
            var sink = channel == 0 ? ch0 : ch1;
            for (int i = 0; i < PerThread; i++)
                sink.Add(peer.WrapReliable(new byte[] { 0xF3 }, channel).ReliableSequenceNumber);
        });

        int half = Threads / 2;
        AssertContiguousFromOne(ch0.ToArray(), half * PerThread);
        AssertContiguousFromOne(ch1.ToArray(), half * PerThread);
    }

    [Fact]
    public void Mixed_reliable_unreliable_and_control_sends_stay_consistent()
    {
        var peer = new EnetPeer();
        var reliable0 = new ConcurrentBag<int>();
        var unreliable0 = new ConcurrentBag<int>();
        var control = new ConcurrentBag<int>();   // channel 0xFF: Ping + VerifyConnect

        // Every thread interleaves all three wrap kinds. Reliable on ch0 and reliable on ch0xFF
        // (Ping) are different channels so they each get an independent contiguous run; the
        // unreliable run on ch0 is its own stream again.
        Storm(_ =>
        {
            for (int i = 0; i < PerThread; i++)
            {
                reliable0.Add(peer.WrapReliable(new byte[] { 0xF3 }, channel: 0).ReliableSequenceNumber);
                unreliable0.Add(peer.WrapUnreliable(new byte[] { 0xF3 }, channel: 0).UnreliableSequenceNumber);
                control.Add(peer.Ping().ReliableSequenceNumber);
            }
        });

        int total = Threads * PerThread;
        AssertContiguousFromOne(reliable0.ToArray(), total);
        AssertContiguousFromOne(unreliable0.ToArray(), total);
        AssertContiguousFromOne(control.ToArray(), total);
    }

    [Fact]
    public void Unreliable_command_carries_the_current_reliable_seq_of_its_channel()
    {
        // The client only delivers an unreliable command once it has reached the carried reliable
        // seq. After N reliable sends on a channel, an unreliable on that channel must report a
        // reliableSoFar of at least N (it can be higher if concurrent reliables raced in, but never
        // lower — that would make the client buffer the packet forever).
        var peer = new EnetPeer();
        const int reliableCount = 500;
        for (int i = 0; i < reliableCount; i++) peer.WrapReliable(new byte[] { 0xF3 }, channel: 0);

        var u = peer.WrapUnreliable(new byte[] { 0xF3 }, channel: 0);
        Assert.True(u.ReliableSequenceNumber >= reliableCount,
            $"unreliable carried reliableSoFar={u.ReliableSequenceNumber}, expected >= {reliableCount}");
    }

    // ---- Inbound command handling: replays, duplicates, and corrupted ordering ----------------

    private static NCommand Inbound(byte type, int reliableSeq, byte[]? payload = null) =>
        new(type, ChannelId: 0, Flags: NCommand.FlagReliable, ReservedByte: 4, reliableSeq, payload ?? Array.Empty<byte>());

    [Fact]
    public void Replayed_reliable_command_is_acked_each_time_with_its_own_seq()
    {
        // A client (or an attacker) that retransmits the SAME reliable command must get an ACK every
        // time, echoing the command's own reliable seq — that is how a real client's retransmit
        // (its first ack was lost) gets satisfied. We characterize that the ack seq tracks the
        // command, not server state.
        var peer = new EnetPeer();
        var cmd = Inbound(NCommand.SendReliable, reliableSeq: 42, payload: new byte[] { 0xF3, 0x01 });

        var first = peer.HandleCommand(cmd, incomingSentTime: 1000, out var p1);
        var again = peer.HandleCommand(cmd, incomingSentTime: 2000, out var p2);

        var ack1 = Assert.Single(first, c => c.CommandType == NCommand.Acknowledge);
        var ack2 = Assert.Single(again, c => c.CommandType == NCommand.Acknowledge);
        Assert.Equal(42, ack1.ReliableSequenceNumber);
        Assert.Equal(42, ack2.ReliableSequenceNumber);
        // The payload is surfaced to the app layer BOTH times — the transport does no inbound dedup.
        // (Documented finding: replay protection, if wanted, belongs above the transport.)
        Assert.NotNull(p1);
        Assert.NotNull(p2);
    }

    [Fact]
    public void Inbound_ack_is_never_acked_but_reliable_disconnect_is()
    {
        var peer = new EnetPeer();

        // An inbound ACK must not itself produce an ACK (that would ping-pong forever).
        var ackIn = new NCommand(NCommand.Acknowledge, 0, NCommand.FlagReliable, 4, 7, Array.Empty<byte>());
        var outForAck = peer.HandleCommand(ackIn, 0, out _);
        Assert.Empty(outForAck);

        // A *reliable* Disconnect, by contrast, IS acked: eNet requires acking every reliable command
        // except Acknowledge (commit f65248b — an unacked reliable command force-disconnects the peer
        // ~10s later). Disconnect produces only that ack — no VerifyConnect, no payload.
        var disc = Inbound(NCommand.Disconnect, 9);
        var outForDisc = peer.HandleCommand(disc, 0, out var discPayload);
        var ack = Assert.Single(outForDisc);
        Assert.Equal(NCommand.Acknowledge, ack.CommandType);
        Assert.Equal(9, ack.ReliableSequenceNumber);
        Assert.Null(discPayload);
    }

    [Fact]
    public void Replayed_connect_keeps_a_stable_peer_id_and_re_verifies()
    {
        // eNet retransmits Connect until it sees VerifyConnect. Every replay must re-send a
        // VerifyConnect, and the assigned PeerId must not change across replays (the client keys its
        // session on the first one).
        var peer = new EnetPeer();
        var connect = Inbound(NCommand.Connect, 1);

        peer.HandleCommand(connect, 0, out _);
        short firstId = peer.PeerId;
        Assert.True(firstId >= 0, "Connect assigns a non-negative peer id");

        for (int i = 0; i < 5; i++)
        {
            var outc = peer.HandleCommand(connect, 0, out _);
            Assert.Contains(outc, c => c.CommandType == NCommand.VerifyConnect);
            Assert.Equal(firstId, peer.PeerId);
        }
    }

    [Fact]
    public void Every_reliable_inbound_gets_exactly_one_ack_under_a_replay_storm()
    {
        // Hammer one peer with a mix of replayed and fresh reliable commands from many threads.
        // EnetPeer.HandleCommand has no per-call shared mutable state beyond the seq dicts (only
        // touched on the Connect path's VerifyConnect), so each call must independently yield exactly
        // one ACK carrying the command's own seq — no lost or doubled acks, no exception.
        var peer = new EnetPeer();
        var acks = new ConcurrentBag<int>();

        Storm(id =>
        {
            for (int i = 0; i < PerThread; i++)
            {
                int seq = (id * PerThread) + i;
                var outc = peer.HandleCommand(Inbound(NCommand.SendReliable, seq, new byte[] { 0xF3 }), i, out _);
                foreach (var c in outc.Where(c => c.CommandType == NCommand.Acknowledge))
                    acks.Add(c.ReliableSequenceNumber);
            }
        });

        Assert.Equal(Threads * PerThread, acks.Count);   // exactly one ack per inbound reliable
    }
}
