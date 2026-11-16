using AfallonCoop.Protocol;
using AfallonCoop.Transport;

namespace AfallonCoop.Session;

/// <summary>
/// Builds one message at a time in a reused buffer and sends it. Clients send to the host; the host sends to
/// connections. Not thread safe: the message in the buffer is the last one begun.
/// </summary>
internal sealed class Outbox
{
    private const int NoConnection = -1;

    private readonly ITransport _transport;
    private readonly HostPeers _peers;
    private readonly bool _isHost;
    private readonly NetWriter _writer = new();

    public Outbox(ITransport transport, HostPeers peers, bool isHost)
    {
        _transport = transport;
        _peers = peers;
        _isHost = isHost;
    }

    public ReadOnlySpan<byte> Pending => _writer.Span;

    public NetWriter Begin(MsgType type)
    {
        _writer.Reset();
        _writer.U8((byte)type);
        return _writer;
    }

    public NetWriter BeginUntyped()
    {
        _writer.Reset();
        return _writer;
    }

    public void ToHost(Delivery delivery) => _transport.Send(ITransport.ServerConnection, _writer.Span, delivery);

    public void To(int connection, Delivery delivery) => _transport.Send(connection, _writer.Span, delivery);

    public void ToEveryone(Delivery delivery, int exceptConnection = NoConnection)
    {
        foreach (var connection in _peers.Connections)
            if (connection != exceptConnection) _transport.Send(connection, _writer.Span, delivery);
    }

    /// <summary>What a player's own news needs: the host tells everyone, a client tells the host.</summary>
    public void Publish(Delivery delivery)
    {
        if (_isHost) ToEveryone(delivery);
        else ToHost(delivery);
    }
}
