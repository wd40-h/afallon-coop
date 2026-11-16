namespace AfallonCoop.Transport;

/// <summary>Network conditions to imitate on top of a real (or loopback) transport.</summary>
public sealed class NetConditions
{
    /// <summary>Added round-trip time, ms. Each side delays its own sends by half, so two conditioned peers see this ping.</summary>
    public int PingMs;
    /// <summary>Random extra one-way delay, 0..JitterMs.</summary>
    public int JitterMs;
    /// <summary>Unreliable packets lost, percent. Reliable ones are delayed by one extra round trip instead (a resend).</summary>
    public double LossPercent;

    public bool Active => PingMs > 0 || JitterMs > 0 || LossPercent > 0;

    public override string ToString() =>
        Active ? $"ping +{PingMs} ms, jitter {JitterMs} ms, loss {LossPercent:0.#}%" : "off";
}
