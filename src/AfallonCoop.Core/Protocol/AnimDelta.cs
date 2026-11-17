namespace AfallonCoop.Protocol;

/// <summary>
/// Sender side of animator parameter compression. Only parameters that changed are sent; every change is repeated in
/// the next <see cref="Redundancy"/> packets (streams are unreliable, a single loss must not lose a change); a full
/// keyframe goes out every <see cref="KeyframeInterval"/> seconds so a receiver that missed more catches up quickly.
/// </summary>
public sealed class AnimDeltaEncoder
{
    public const int Redundancy = 3;
    public double KeyframeInterval = 1.0;
    public float Epsilon = 0.01f;

    private readonly Dictionary<int, (float value, int resend)> _sent = new();
    private double _nextKeyframe = double.NegativeInfinity;
    private readonly double _phase;

    /// <param name="phase">0..1: shifts this encoder's keyframes within the interval, so many encoders
    /// (one per mob) do not all send a full set in the same tick.</param>
    public AnimDeltaEncoder(double phase = 0) { _phase = phase; }

    public void Reset()
    {
        _sent.Clear();
        _nextKeyframe = double.NegativeInfinity;
    }

    /// <summary>Reduce a full parameter sample to what must be sent now. Returns true when it is a keyframe.</summary>
    public bool Encode(double now, List<AnimParam> all, List<AnimParam> toSend)
    {
        toSend.Clear();
        bool isKeyframe = now >= _nextKeyframe;
        if (isKeyframe) ScheduleNextKeyframe(now);
        foreach (var param in all)
        {
            bool changed = !_sent.TryGetValue(param.NameHash, out var sent) || Math.Abs(sent.value - param.Value) > Epsilon;
            int resendsLeft = changed ? Redundancy : sent.resend;
            if (isKeyframe || resendsLeft > 0) toSend.Add(param);
            _sent[param.NameHash] = (changed ? param.Value : sent.value, Math.Max(0, resendsLeft - 1));
        }
        return isKeyframe;
    }

    private void ScheduleNextKeyframe(double now)
    {
        bool isFirstKeyframe = double.IsNegativeInfinity(_nextKeyframe);
        double intervals = isFirstKeyframe ? 1 + _phase : 1;
        _nextKeyframe = now + KeyframeInterval * intervals;
    }
}

/// <summary>Receiver side: the full current parameter set, updated by deltas and keyframes.</summary>
public sealed class AnimParamState
{
    private readonly Dictionary<int, int> _index = new();
    public readonly List<AnimParam> Current = new();

    public void Apply(List<AnimParam> received, bool keyframe)
    {
        if (keyframe) Clear();
        foreach (var param in received) Set(param);
    }

    public void Clear()
    {
        _index.Clear();
        Current.Clear();
    }

    private void Set(AnimParam param)
    {
        if (_index.TryGetValue(param.NameHash, out int position))
        {
            Current[position] = param;
            return;
        }
        _index[param.NameHash] = Current.Count;
        Current.Add(param);
    }
}
