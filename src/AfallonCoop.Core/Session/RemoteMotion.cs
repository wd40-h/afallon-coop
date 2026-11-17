using AfallonCoop.Protocol;

namespace AfallonCoop.Session;

public struct V3
{
    public float X, Y, Z;

    public V3(float x, float y, float z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    public static V3 Lerp(V3 from, V3 to, float t) =>
        new(from.X + (to.X - from.X) * t, from.Y + (to.Y - from.Y) * t, from.Z + (to.Z - from.Z) * t);

    public override string ToString() => $"({X:0.00}, {Y:0.00}, {Z:0.00})";
}

/// <summary>
/// Snapshot interpolation for one remote player. Renders the past (by <see cref="Delay"/>) so there are always two
/// samples to blend between; short gaps are extrapolated from velocity, longer ones hold the last pose.
/// Sender timestamps are mapped to the local clock with a min-latency offset estimate, so jitter does not cause stutter.
/// </summary>
public sealed class RemoteMotion
{
    public const int Capacity = 32;

    private const double OffsetDriftRate = 0.02;
    private const double MinimumSampleSpan = 1e-6;
    private const float HalfTurnDegrees = 180f;
    private const float FullTurnDegrees = 360f;

    private readonly List<PlayerStateMsg> _samples = new(Capacity);
    private double _clockOffset = double.NaN;

    /// <summary>Seconds behind the sender: more than the send interval (1/15 s) plus typical jitter.</summary>
    public double Delay = 0.12;
    public double MaxExtrapolation = 0.25;

    public PlayerStateMsg Latest { get; private set; }
    public int Count => _samples.Count;

    public void Add(PlayerStateMsg sample, double localNow)
    {
        if (IsOldOrDuplicate(sample)) return;
        TrackClockOffset(localNow - sample.SenderTime);
        Latest = sample;
        _samples.Add(sample);
        if (_samples.Count > Capacity) _samples.RemoveAt(0);
    }

    /// <summary>
    /// The sender's time of the pose shown at <paramref name="localNow"/> (what this machine sees of them now), on the
    /// sender's own clock; NaN before the first sample. Lag compensation: the sender checks a hit against where it was then.
    /// </summary>
    public double ViewTime(double localNow) => double.IsNaN(_clockOffset) ? double.NaN : SenderTimeAt(localNow);

    /// <summary>Pose to show at <paramref name="localNow"/>. False until the first sample arrives.</summary>
    public bool Sample(double localNow, out V3 pos, out float rotY)
    {
        pos = default;
        rotY = 0;
        if (_samples.Count == 0) return false;
        double time = SenderTimeAt(localNow);
        if (time <= _samples[0].SenderTime) HoldPose(_samples[0], out pos, out rotY);
        else if (!TryInterpolate(time, out pos, out rotY)) Extrapolate(_samples[^1], time, out pos, out rotY);
        return true;
    }

    public static float LerpAngle(float from, float to, float t)
    {
        float shortestTurn = ((to - from) % FullTurnDegrees + FullTurnDegrees + HalfTurnDegrees) % FullTurnDegrees - HalfTurnDegrees;
        return from + shortestTurn * t;
    }

    private double SenderTimeAt(double localNow) => localNow - _clockOffset - Delay;

    private bool IsOldOrDuplicate(PlayerStateMsg sample)
    {
        if (Latest == null) return false;
        bool notNewer = sample.Seq <= Latest.Seq;
        bool wrappedAround = Latest.Seq - sample.Seq >= uint.MaxValue / 2;
        return notNewer && !wrappedAround;
    }

    /// <summary>The smallest offset seen is the best estimate; it drifts up slowly for clock drift and route changes.</summary>
    private void TrackClockOffset(double offset)
    {
        if (double.IsNaN(_clockOffset) || offset < _clockOffset) _clockOffset = offset;
        else _clockOffset += (offset - _clockOffset) * OffsetDriftRate;
    }

    private bool TryInterpolate(double time, out V3 pos, out float rotY)
    {
        for (int i = _samples.Count - 1; i > 0; i--)
        {
            var earlier = _samples[i - 1];
            var later = _samples[i];
            if (time < earlier.SenderTime || time > later.SenderTime) continue;
            double span = later.SenderTime - earlier.SenderTime;
            float blend = span <= MinimumSampleSpan ? 1f : (float)((time - earlier.SenderTime) / span);
            pos = V3.Lerp(PositionOf(earlier), PositionOf(later), blend);
            rotY = LerpAngle(earlier.RotY, later.RotY, blend);
            return true;
        }
        pos = default;
        rotY = 0;
        return false;
    }

    private void Extrapolate(PlayerStateMsg last, double time, out V3 pos, out float rotY)
    {
        float seconds = (float)Math.Min(time - last.SenderTime, MaxExtrapolation);
        pos = new V3(
            last.PosX + last.VelX * seconds,
            last.PosY + last.VelY * seconds,
            last.PosZ + last.VelZ * seconds);
        rotY = last.RotY;
    }

    private static void HoldPose(PlayerStateMsg sample, out V3 pos, out float rotY)
    {
        pos = PositionOf(sample);
        rotY = sample.RotY;
    }

    private static V3 PositionOf(PlayerStateMsg sample) => new(sample.PosX, sample.PosY, sample.PosZ);
}
