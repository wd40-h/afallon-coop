namespace AfallonCoop.Session;

/// <summary>Host side: which connection is which player, and who still owes a hello.</summary>
internal sealed class HostPeers
{
    private const double HelloTimeoutSeconds = 10;

    private readonly Dictionary<int, byte> _playerByConnection = new();
    private readonly Dictionary<int, double> _connectedAt = new();
    private readonly List<int> _expired = new();

    public IEnumerable<int> Connections => _playerByConnection.Keys;

    public void AwaitHello(int connection, double now) => _connectedAt[connection] = now;

    public void StopAwaitingHello(int connection) => _connectedAt.Remove(connection);

    public bool Has(int connection) => _playerByConnection.ContainsKey(connection);

    public void Admit(int connection, byte player) => _playerByConnection[connection] = player;

    public bool TryGetPlayer(int connection, out byte player) => _playerByConnection.TryGetValue(connection, out player);

    public bool Remove(int connection, out byte player) => _playerByConnection.Remove(connection, out player);

    public int ConnectionOf(byte player)
    {
        foreach (var pair in _playerByConnection)
            if (pair.Value == player) return pair.Key;
        return -1;
    }

    public IReadOnlyList<int> TakeTimedOutHandshakes(double now)
    {
        _expired.Clear();
        foreach (var pair in _connectedAt)
            if (now - pair.Value > HelloTimeoutSeconds) _expired.Add(pair.Key);
        foreach (var connection in _expired) _connectedAt.Remove(connection);
        return _expired;
    }
}
