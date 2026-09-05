using System;
using System.Threading;
using System.Threading.Tasks;
using BlackIce.Photon;
using BlackIce.Photon.Transport;
using BlackIce.Server.Core;
using Xunit;

namespace BlackIce.Server.Tests;

/// <summary>
/// The host drives playerbot ticks off <see cref="UdpListener.OnMaintenance"/>, which must fire on
/// the listener's single loop thread (so bot relays never race the receive path) and must never let
/// a throwing hook kill the listener. These tests pin both via the live RunAsync loop on an
/// ephemeral port (port 0), cancelled after a couple of maintenance windows.
/// </summary>
public class UdpListenerMaintenanceTests
{
    private sealed class NullHandler : IOperationHandler
    {
        public void OnConnect(PeerConnection peer) { }
        public void OnOperationRequest(PeerConnection peer, OperationRequest request) { }
        public void OnDisconnect(PeerConnection peer) { }
    }

    [Fact]
    public async Task OnMaintenance_fires_on_the_listener_loop()
    {
        var listener = new UdpListener("test-maint", 0, new NullHandler());
        int calls = 0;
        listener.OnMaintenance = () => Interlocked.Increment(ref calls);

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(4500)); // several maintenance passes (interval 1s); generous so a slow CI start can't miss it
        try { await listener.RunAsync(cts.Token); }
        finally { cts.Cancel(); }

        Assert.True(calls >= 1, $"expected OnMaintenance to fire at least once, got {calls}");
    }

    [Fact]
    public async Task Current_identifies_the_loop_across_thread_hops()
    {
        // The async loop resumes on arbitrary pool threads; Current must still name this listener on
        // every OnTick, and must be null off-loop (regression: sim ownership keyed on a thread id).
        var listener = new UdpListener("test-current", 0, new NullHandler()) { TickInterval = TimeSpan.FromMilliseconds(5) };
        int ticks = 0, wrong = 0;
        var threads = new System.Collections.Concurrent.ConcurrentDictionary<int, byte>();
        listener.OnTick = () =>
        {
            Interlocked.Increment(ref ticks);
            threads[Environment.CurrentManagedThreadId] = 0;
            if (!ReferenceEquals(UdpListener.Current, listener)) Interlocked.Increment(ref wrong);
        };

        Assert.Null(UdpListener.Current);
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(1500));
        try { await listener.RunAsync(cts.Token); }
        finally { cts.Cancel(); }

        Assert.True(ticks >= 10, $"expected many ticks, got {ticks}");
        Assert.Equal(0, wrong);
        Assert.Null(UdpListener.Current);   // never leaks out of the loop
    }

    [Fact]
    public async Task OnMaintenance_exception_does_not_kill_the_listener()
    {
        var listener = new UdpListener("test-maint-throw", 0, new NullHandler());
        int calls = 0;
        listener.OnMaintenance = () => { Interlocked.Increment(ref calls); throw new InvalidOperationException("boom"); };

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(2400));
        // RunAsync must complete normally on cancellation despite the hook throwing every pass.
        try { await listener.RunAsync(cts.Token); }
        finally { cts.Cancel(); }

        Assert.True(calls >= 1, $"expected the throwing hook to be invoked, got {calls} calls");
    }
}
