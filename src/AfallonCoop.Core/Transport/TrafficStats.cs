using System.Text;

namespace AfallonCoop.Transport;

/// <summary>Bytes and packets per message type (first byte), in and out, over a sliding window.</summary>
public sealed class TrafficStats
{
    private const double WindowSeconds = 5.0;
    private const double BytesPerKilobyte = 1024;

    private readonly Queue<Sample> _samples = new();

    private readonly record struct Sample(double Time, bool Incoming, byte Type, int Bytes);

    public long TotalOut;
    public long TotalIn;
    public int Dropped;

    public void Count(double now, bool incoming, byte type, int bytes)
    {
        if (incoming) TotalIn += bytes;
        else TotalOut += bytes;
        _samples.Enqueue(new Sample(now, incoming, type, bytes));
        while (_samples.Count > 0 && now - _samples.Peek().Time > WindowSeconds) _samples.Dequeue();
    }

    /// <summary>Per type and direction: bytes per second over the last few seconds.</summary>
    public List<(byte type, bool incoming, double bytesPerSecond)> Rates()
    {
        var bytesByKind = new Dictionary<(byte type, bool incoming), long>();
        foreach (var sample in _samples)
        {
            var kind = (sample.Type, sample.Incoming);
            bytesByKind.TryGetValue(kind, out long sum);
            bytesByKind[kind] = sum + sample.Bytes;
        }
        return bytesByKind
            .Select(entry => (entry.Key.type, entry.Key.incoming, entry.Value / WindowSeconds))
            .ToList();
    }

    /// <summary>Per type: bytes/s and packets/s out and in, averaged over the last few seconds.</summary>
    public string Report(Func<byte, string> typeName)
    {
        var rows = BuildRows();
        var report = new StringBuilder();
        double outBytesPerSecond = rows.Where(row => !row.Incoming).Sum(row => row.BytesPerSecond);
        double inBytesPerSecond = rows.Where(row => row.Incoming).Sum(row => row.BytesPerSecond);
        report.AppendLine(
            $"total out {outBytesPerSecond / BytesPerKilobyte:0.0} KB/s, in {inBytesPerSecond / BytesPerKilobyte:0.0} KB/s " +
            $"(last {WindowSeconds:0} s); lost by sim: {Dropped}");
        foreach (var row in rows) report.AppendLine(FormatRow(row, typeName));
        return report.ToString();
    }

    private readonly record struct Row(byte Type, bool Incoming, double BytesPerSecond, double PacketsPerSecond, double AverageBytes);

    private List<Row> BuildRows() => _samples
        .GroupBy(sample => (sample.Type, sample.Incoming))
        .Select(group => new Row(
            group.Key.Type,
            group.Key.Incoming,
            group.Sum(sample => sample.Bytes) / WindowSeconds,
            group.Count() / WindowSeconds,
            group.Average(sample => sample.Bytes)))
        .OrderByDescending(row => row.BytesPerSecond)
        .ToList();

    private static string FormatRow(Row row, Func<byte, string> typeName)
    {
        string direction = row.Incoming ? "in " : "out";
        return $"  {direction} {typeName(row.Type),-12} {row.BytesPerSecond / BytesPerKilobyte,6:0.00} KB/s  " +
               $"{row.PacketsPerSecond,6:0.0} pkt/s  avg {row.AverageBytes,5:0} B";
    }
}
