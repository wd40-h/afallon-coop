namespace AfallonCoop.Transport;

/// <summary>In-memory transport for tests: a hub plays the host, each client gets its own endpoint.</summary>
public sealed class LoopbackHub
{
    private readonly List<LoopbackTransport> _clients = new();
    private int _nextClientId = 1;

    internal LoopbackTransport Server { get; }

    public LoopbackHub()
    {
        Server = new LoopbackTransport(this, isServer: true, id: 0);
    }

    public ITransport Host => Server;

    public ITransport Connect()
    {
        var client = new LoopbackTransport(this, isServer: false, _nextClientId++);
        _clients.Add(client);
        client.Enqueue(() => client.RaiseConnected(ITransport.ServerConnection));
        Server.Enqueue(() => Server.RaiseConnected(client.Id));
        return client;
    }

    internal LoopbackTransport OpenClient(int id) => _clients.FirstOrDefault(client => client.Id == id && !client.Closed);

    internal void Drop(LoopbackTransport client, string reason)
    {
        if (client.Closed) return;
        client.Closed = true;
        client.Enqueue(() => client.RaiseDisconnected(ITransport.ServerConnection, reason));
        Server.Enqueue(() => Server.RaiseDisconnected(client.Id, reason));
    }
}

public sealed class LoopbackTransport : ITransport
{
    private readonly LoopbackHub _hub;
    private readonly Queue<Action> _inbox = new();

    internal int Id { get; }
    internal bool Closed { get; set; }

    public bool IsServer { get; }
    public string Description => IsServer ? "loopback host" : $"loopback client {Id}";
    public event Action<int> Connected;
    public event Action<int, string> Disconnected;
    public event Action<int, ReadOnlyMemory<byte>> Received;

    internal LoopbackTransport(LoopbackHub hub, bool isServer, int id)
    {
        _hub = hub;
        IsServer = isServer;
        Id = id;
    }

    /// <summary>Only what was queued before this poll runs, like a real network tick.</summary>
    public void Poll()
    {
        int queued = _inbox.Count;
        while (queued-- > 0) _inbox.Dequeue()();
    }

    public void Send(int connection, ReadOnlySpan<byte> data, Delivery delivery)
    {
        if (Closed) return;
        var copy = data.ToArray();
        if (IsServer) SendToClient(connection, copy);
        else SendToServer(copy);
    }

    public void Disconnect(int connection, string reason)
    {
        if (!IsServer)
        {
            _hub.Drop(this, reason);
            return;
        }
        var client = _hub.OpenClient(connection);
        if (client != null) _hub.Drop(client, reason);
    }

    public int GetPing(int connection) => 0;

    public void Dispose()
    {
        if (!IsServer) _hub.Drop(this, "disposed");
    }

    internal void Enqueue(Action action) => _inbox.Enqueue(action);

    internal void RaiseConnected(int connection) => Connected?.Invoke(connection);

    internal void RaiseDisconnected(int connection, string reason) => Disconnected?.Invoke(connection, reason);

    private void SendToClient(int connection, byte[] data)
    {
        var client = _hub.OpenClient(connection);
        client?.Enqueue(() => client.Received?.Invoke(ITransport.ServerConnection, data));
    }

    private void SendToServer(byte[] data)
    {
        var server = _hub.Server;
        int sender = Id;
        server.Enqueue(() => server.Received?.Invoke(sender, data));
    }
}
