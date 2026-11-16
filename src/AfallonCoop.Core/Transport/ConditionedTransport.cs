using System.Diagnostics;

namespace AfallonCoop.Transport;

/// <summary>
/// Wraps a transport to (1) imitate latency, jitter and loss on outgoing packets and (2) count traffic per message
/// type, both ways. Reliable packets keep their order (a late resend holds back the ones after it, as in a real
/// reliable channel); unreliable ones may arrive out of order or not at all.
/// </summary>
public sealed class ConditionedTransport : ITransport
{
    private const double ResendPenaltySeconds = 0.05;
    private const double MillisecondsPerSecond = 1000.0;
    private const double PercentScale = 100;

    private readonly ITransport _inner;
    private readonly Func<double> _clock;
    private readonly Random _random;
    private readonly List<Delayed> _queue = new();
    private readonly List<Delayed> _due = new();
    private readonly Dictionary<int, double> _lastReliableDue = new();
    private long _nextOrder;

    private readonly record struct Delayed(double Due, int Connection, byte[] Data, Delivery Delivery, long Order);

    public NetConditions Conditions { get; } = new();
    public TrafficStats Stats { get; } = new();

    public bool IsServer => _inner.IsServer;
    public string Description => Conditions.Active ? $"{_inner.Description} [sim {Conditions}]" : _inner.Description;
    public event Action<int> Connected;
    public event Action<int, string> Disconnected;
    public event Action<int, ReadOnlyMemory<byte>> Received;

    public ConditionedTransport(ITransport inner, Func<double> clock = null, int seed = 1)
    {
        _inner = inner;
        _clock = clock ?? StopwatchClock();
        _random = new Random(seed);
        _inner.Connected += connection => Connected?.Invoke(connection);
        _inner.Disconnected += (connection, reason) => Disconnected?.Invoke(connection, reason);
        _inner.Received += OnInnerReceived;
    }

    public void Send(int connection, ReadOnlySpan<byte> data, Delivery delivery)
    {
        Stats.Count(_clock(), incoming: false, FirstByte(data), data.Length);
        if (!Conditions.Active && _queue.Count == 0)
        {
            _inner.Send(connection, data, delivery);
            return;
        }
        if (delivery == Delivery.Reliable) ScheduleReliable(connection, data);
        else ScheduleUnreliable(connection, data, delivery);
    }

    public void Poll()
    {
        SendDuePackets();
        if (_queue.Count == 0) _lastReliableDue.Clear();
        _inner.Poll();
    }

    public void Disconnect(int connection, string reason) => _inner.Disconnect(connection, reason);

    public int GetPing(int connection)
    {
        int ping = _inner.GetPing(connection);
        return ping < 0 ? ping : ping + Conditions.PingMs;
    }

    public void Dispose() => _inner.Dispose();

    private static Func<double> StopwatchClock()
    {
        var stopwatch = Stopwatch.StartNew();
        return () => stopwatch.Elapsed.TotalSeconds;
    }

    private void OnInnerReceived(int connection, ReadOnlyMemory<byte> data)
    {
        Stats.Count(_clock(), incoming: true, FirstByte(data.Span), data.Length);
        Received?.Invoke(connection, data);
    }

    private static byte FirstByte(ReadOnlySpan<byte> data) => data.Length > 0 ? data[0] : (byte)0;

    private void ScheduleReliable(int connection, ReadOnlySpan<byte> data)
    {
        double delay = OneWayDelay();
        if (IsLost()) delay += Conditions.PingMs / MillisecondsPerSecond + ResendPenaltySeconds;
        double due = Math.Max(_clock() + delay, LastReliableDue(connection));
        _lastReliableDue[connection] = due;
        Enqueue(due, connection, data, Delivery.Reliable);
    }

    private void ScheduleUnreliable(int connection, ReadOnlySpan<byte> data, Delivery delivery)
    {
        double delay = OneWayDelay();
        if (IsLost())
        {
            Stats.Dropped++;
            return;
        }
        Enqueue(_clock() + delay, connection, data, delivery);
    }

    private double OneWayDelay() =>
        (Conditions.PingMs / 2.0 + _random.NextDouble() * Conditions.JitterMs) / MillisecondsPerSecond;

    private bool IsLost() =>
        Conditions.LossPercent > 0 && _random.NextDouble() * PercentScale < Conditions.LossPercent;

    private double LastReliableDue(int connection) =>
        _lastReliableDue.TryGetValue(connection, out double last) ? last : double.MinValue;

    private void Enqueue(double due, int connection, ReadOnlySpan<byte> data, Delivery delivery) =>
        _queue.Add(new Delayed(due, connection, data.ToArray(), delivery, _nextOrder++));

    private void SendDuePackets()
    {
        if (_queue.Count == 0) return;
        double now = _clock();
        _due.Clear();
        foreach (var packet in _queue)
            if (packet.Due <= now) _due.Add(packet);
        if (_due.Count == 0) return;
        _queue.RemoveAll(packet => packet.Due <= now);
        _due.Sort(CompareByDueThenOrder);
        foreach (var packet in _due) _inner.Send(packet.Connection, packet.Data, packet.Delivery);
    }

    private static int CompareByDueThenOrder(Delayed first, Delayed second) =>
        first.Due != second.Due ? first.Due.CompareTo(second.Due) : first.Order.CompareTo(second.Order);
}
