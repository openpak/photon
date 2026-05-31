using System.Collections.Concurrent;

namespace BlackIce.Server.LoadBalancing.Authority;

/// <summary>
/// Per-entity ring buffer of timestamped positions — the rewind primitive for lag-compensated hit
/// validation (Phase 3c). Each accepted position sample (from a validated PUN 201) is appended; the
/// buffer keeps the most recent <c>capacity</c> samples per viewID. <see cref="TryPositionAt"/> answers
/// "where was this entity at time T", linearly interpolating between the two bracketing samples and
/// clamping to the ends — so a later hit-validation rule can rewind a moving target to the moment the
/// shooter saw it before judging a shot.
///
/// <para>Only ACCEPTED positions are recorded (apply-after-validate): a snap-corrected teleport never
/// enters the timeline, so the rewind reflects the authoritative path, not the cheat's claim. Designed
/// for the single listener thread; the backing map is concurrent and each entity's list is locked, as
/// defense-in-depth matching the rest of the authority layer.</para>
///
/// <para><b>Deferred:</b> the enforcing hit-validation rule that consumes this history needs protocol
/// facts not yet reverse-engineered (shooter actor→viewID mapping, weapon ranges, per-shot ack ticks —
/// PUN's PhotonMessageInfo timestamp is not sent on the wire). This type provides the mechanism and the
/// query seam; wiring an enforcing rule is future work.</para>
/// </summary>
public sealed class WorldSnapshotHistory
{
    /// <summary>One position sample at a server-receive timestamp.</summary>
    public readonly record struct Sample(DateTime T, float X, float Y, float Z);

    private readonly int _capacity;
    private readonly ConcurrentDictionary<int, List<Sample>> _byView = new();

    public WorldSnapshotHistory(int capacity = 64)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity), "capacity must be >= 1");
        _capacity = capacity;
    }

    /// <summary>Appends a sample for <paramref name="viewId"/>, evicting the oldest beyond capacity.</summary>
    public void Record(int viewId, float x, float y, float z, DateTime t)
    {
        var list = _byView.GetOrAdd(viewId, _ => new List<Sample>());
        lock (list)
        {
            list.Add(new Sample(t, x, y, z));
            if (list.Count > _capacity) list.RemoveAt(0);
        }
    }

    /// <summary>
    /// Resolves the entity's position at time <paramref name="t"/>: clamped to the earliest/latest sample
    /// outside the recorded window, linearly interpolated between the two bracketing samples inside it.
    /// Returns false only when the entity has no recorded samples at all.
    /// </summary>
    public bool TryPositionAt(int viewId, DateTime t, out (float x, float y, float z) pos)
    {
        pos = default;
        if (!_byView.TryGetValue(viewId, out var list)) return false;
        lock (list)
        {
            if (list.Count == 0) return false;

            var first = list[0];
            if (t <= first.T) { pos = (first.X, first.Y, first.Z); return true; }   // clamp before window

            var last = list[^1];
            if (t >= last.T) { pos = (last.X, last.Y, last.Z); return true; }        // clamp after window

            for (int i = 1; i < list.Count; i++)
            {
                if (list[i].T >= t)
                {
                    var a = list[i - 1];
                    var b = list[i];
                    double span = (b.T - a.T).TotalSeconds;
                    double f = span <= 0 ? 0 : (t - a.T).TotalSeconds / span;
                    pos = (Lerp(a.X, b.X, f), Lerp(a.Y, b.Y, f), Lerp(a.Z, b.Z, f));
                    return true;
                }
            }

            pos = (last.X, last.Y, last.Z);   // unreachable (t is within window), kept for totality
            return true;
        }
    }

    /// <summary>Number of samples currently retained for <paramref name="viewId"/>.</summary>
    public int SampleCount(int viewId) => _byView.TryGetValue(viewId, out var l) ? l.Count : 0;

    private static float Lerp(float a, float b, double f) => (float)(a + (b - a) * f);
}
