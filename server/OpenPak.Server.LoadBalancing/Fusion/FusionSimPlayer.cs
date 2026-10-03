namespace OpenPak.Server.LoadBalancing.Fusion;

/// <summary>Where one connection stands with one object (spec §8.2 <c>ObjectData</c>).</summary>
internal enum ObjectSendStatus { CreatedUnconfirmed, CreatedConfirmed, DestroyUnconfirmed, DestroyPending }

internal sealed class ObjectSendState(ObjectSendStatus status)
{
    public ObjectSendStatus Status = status;
    /// <summary>Stamp of the last packet that carried the object to this connection.</summary>
    public int Sent;
    /// <summary>Highest stamp of a delivered packet that carried it.</summary>
    public int Acked;
}

/// <summary>A reliable or unreliable message queued for one connection, with the sequence it took from
/// that connection's outgoing counter (kept across resends, spec §12.4).</summary>
internal sealed record OutMessage(SimMessage Message, ulong Sequence, int Tick);

/// <summary>What one sent packet carried — the notify token that comes back as delivered or lost (§8).</summary>
internal sealed class SentPacket(int stamp, int tick)
{
    public readonly int Stamp = stamp;
    public readonly int Tick = tick;
    public readonly List<(uint Id, int PreviousSent, bool Full)> Objects = new();
    public readonly List<uint> Destroys = new();
    public readonly List<OutMessage> Messages = new();
}

/// <summary>
/// The simulation's view of one connected player: its relay connection, per-object replication state,
/// outgoing and incoming message sequencing, and the time-feedback series (spec §8, §10.3, §12).
/// </summary>
internal sealed class FusionSimPlayer(FusionRelayConnection connection, uint playerDataId)
{
    public readonly FusionRelayConnection Connection = connection;
    public int Actor => Connection.Actor;
    /// <summary>The raw PlayerRef (index + 1); this server's player index is the actor number.</summary>
    public int PlayerRaw => Connection.PlayerId + 1;
    public int PlayerIndex => Connection.PlayerId;
    public readonly uint PlayerDataId = playerDataId;

    public readonly Dictionary<uint, ObjectSendState> Objects = new();
    public readonly List<uint> PendingDestroys = new();
    public readonly LinkedList<OutMessage> Outgoing = new();
    public ulong OutSequence;
    public ulong InDelivered;
    public readonly SortedDictionary<ulong, SimMessage> InPending = new();

    /// <summary>Where the next packet's object and PlayerData scans start (spec §8.3 rotation).</summary>
    public int ObjectCursor, PlayerDataCursor;

    // Time feedback and send pacing (spec §10.2-§10.3).
    public double LastReceived;
    public double LastPacketSent = double.NegativeInfinity;
    public int LastAckedTick;
    public int LastClientTick = int.MinValue;
    public double LastArrival = double.NaN;
    public FeedbackSeries Offsets = new(32), RecvDeltas = new(32);
}

/// <summary>A sliding window of seconds values and the two statistics TimeFeedback reports (spec §10.3):
/// an exponential average over the window, oldest first, and the scaled median absolute deviation.</summary>
internal sealed class FeedbackSeries(int capacity)
{
    private readonly Queue<double> _samples = new();

    public int Count => _samples.Count;

    public void Add(double value)
    {
        if (_samples.Count == capacity) _samples.Dequeue();
        _samples.Enqueue(value);
    }

    public void Clear() => _samples.Clear();

    public double Average
    {
        get
        {
            var average = 0.0;
            foreach (var sample in _samples) average = 0.5 * average + 0.5 * sample;
            return average;
        }
    }

    public double Deviation
    {
        get
        {
            if (_samples.Count == 0) return 0;
            var median = Median(_samples);
            // 1.4826 scales a median absolute deviation to a standard deviation for normal data.
            return 1.4826 * Median(_samples.Select(s => Math.Abs(s - median)));
        }
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }
}
