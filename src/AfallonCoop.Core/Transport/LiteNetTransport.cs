using System.Net;
using System.Text;
using LiteNetLib;
using LiteNetLib.Utils;

namespace AfallonCoop.Transport;

/// <summary>
/// UDP transport over LiteNetLib: LAN, direct IP, and virtual LANs (Radmin VPN, ZeroTier, Hamachi, Tailscale).
/// Connection ids on the host are LiteNetLib peer ids + 1, so 0 stays free for "the server" on clients.
/// </summary>
public sealed class LiteNetTransport : ITransport
{
    public const int DefaultPort = 27555;

    private const string ConnectKey = "AfallonCoop/1";
    private const string DiscoverReplyPrefix = "AFCOOP!";
    private const int DisconnectTimeoutMs = 10000;
    private const int UpdateIntervalMs = 15;
    private const int DiscoveryPollIntervalMs = 30;
    private static readonly byte[] DiscoverProbe = Encoding.ASCII.GetBytes("AFCOOP?");

    private readonly EventBasedNetListener _listener = new();
    private readonly NetManager _net;
    private readonly Dictionary<int, NetPeer> _peers = new();
    private NetPeer _server;
    private bool _warnedTooBig;

    /// <summary>Host only: text returned to LAN discovery probes (game name, player count).</summary>
    public Func<string> DiscoveryInfo { get; set; }

    /// <summary>Diagnostics (oversized packets...).</summary>
    public Action<string> Log;

    public bool IsServer { get; }
    public string Description { get; }
    public event Action<int> Connected;
    public event Action<int, string> Disconnected;
    public event Action<int, ReadOnlyMemory<byte>> Received;

    private LiteNetTransport(bool server, string description)
    {
        IsServer = server;
        Description = description;
        _net = new NetManager(_listener)
        {
            AutoRecycle = true,
            UnconnectedMessagesEnabled = true,
            BroadcastReceiveEnabled = server,
            DisconnectTimeout = DisconnectTimeoutMs,
            UpdateTime = UpdateIntervalMs,
        };
        _listener.ConnectionRequestEvent += OnConnectionRequest;
        _listener.PeerConnectedEvent += OnPeerConnected;
        _listener.PeerDisconnectedEvent += OnPeerDisconnected;
        _listener.NetworkReceiveEvent += OnNetworkReceive;
        _listener.NetworkReceiveUnconnectedEvent += OnUnconnectedReceive;
    }

    public static LiteNetTransport Host(int port)
    {
        var transport = new LiteNetTransport(true, $"LAN/IP host :{port}");
        if (!transport._net.Start(port))
            throw new InvalidOperationException($"Port {port} is busy. Close the other game copy or pick another port.");
        return transport;
    }

    public static LiteNetTransport Connect(string host, int port)
    {
        var transport = new LiteNetTransport(false, $"LAN/IP {host}:{port}");
        if (!transport._net.Start()) throw new InvalidOperationException("Could not open a UDP socket.");
        transport._net.Connect(host, port, ConnectKey);
        return transport;
    }

    public void Poll() => _net.PollEvents();

    public void Send(int connection, ReadOnlySpan<byte> data, Delivery delivery)
    {
        var peer = PeerOf(connection);
        if (peer == null) return;
        var method = MethodFor(delivery);
        if (ExceedsSinglePacket(peer, method, data.Length)) return;
        peer.Send(data, 0, method);
    }

    public void Disconnect(int connection, string reason)
    {
        var peer = PeerOf(connection);
        if (peer == null) return;
        var writer = new NetDataWriter();
        writer.Put(reason ?? "");
        peer.Disconnect(writer);
    }

    public int GetPing(int connection) => PeerOf(connection)?.Ping ?? -1;

    public void Dispose() => _net.Stop(true);

    private NetPeer PeerOf(int connection)
    {
        if (!IsServer) return _server;
        return _peers.TryGetValue(connection, out var peer) ? peer : null;
    }

    private int ConnectionIdOf(NetPeer peer) => IsServer ? peer.Id + 1 : ITransport.ServerConnection;

    private static DeliveryMethod MethodFor(Delivery delivery) => delivery switch
    {
        Delivery.Reliable => DeliveryMethod.ReliableOrdered,
        Delivery.Unreliable => DeliveryMethod.Sequenced,
        _ => DeliveryMethod.Unreliable,
    };

    /// <summary>An oversized unreliable packet is a sender bug (packet budget), not a network fault: drop it, say so once.</summary>
    private bool ExceedsSinglePacket(NetPeer peer, DeliveryMethod method, int length)
    {
        if (method == DeliveryMethod.ReliableOrdered) return false;
        int max = peer.GetMaxSinglePacketSize(method);
        if (length <= max) return false;
        if (_warnedTooBig) return true;
        _warnedTooBig = true;
        Log?.Invoke($"Dropped an unreliable packet of {length} bytes (max {max})");
        return true;
    }

    private void OnConnectionRequest(ConnectionRequest request)
    {
        if (IsServer) request.AcceptIfKey(ConnectKey);
        else request.Reject();
    }

    private void OnPeerConnected(NetPeer peer)
    {
        if (IsServer) _peers[ConnectionIdOf(peer)] = peer;
        else _server = peer;
        Connected?.Invoke(ConnectionIdOf(peer));
    }

    private void OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
    {
        int connection = ConnectionIdOf(peer);
        if (IsServer) _peers.Remove(connection);
        else _server = null;
        Disconnected?.Invoke(connection, ReasonOf(info));
    }

    private static string ReasonOf(DisconnectInfo info)
    {
        bool hasReasonText = info.AdditionalData != null && info.AdditionalData.AvailableBytes > 0;
        if (!hasReasonText) return info.Reason.ToString();
        try { return info.AdditionalData.GetString(); }
        catch { return info.Reason.ToString(); }
    }

    /// <summary>No copy: the bytes are valid during the callback (the session reads them there).</summary>
    private void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod method)
    {
        var segment = reader.GetRemainingBytesSegment();
        Received?.Invoke(ConnectionIdOf(peer), new ReadOnlyMemory<byte>(segment.Array, segment.Offset, segment.Count));
    }

    private void OnUnconnectedReceive(IPEndPoint endPoint, NetPacketReader reader, UnconnectedMessageType type)
    {
        if (!IsServer) return;
        if (!reader.GetRemainingBytes().AsSpan().SequenceEqual(DiscoverProbe)) return;
        var reply = Encoding.UTF8.GetBytes(DiscoverReplyPrefix + (DiscoveryInfo?.Invoke() ?? ""));
        _net.SendUnconnectedMessage(reply, endPoint);
    }

    /// <summary>A host found by LAN broadcast.</summary>
    public sealed record LanHost(string Address, int Port, string Info);

    /// <summary>Broadcasts a probe on the LAN and collects replies for <paramref name="waitMs"/>.</summary>
    public static async Task<List<LanHost>> DiscoverAsync(int port = DefaultPort, int waitMs = 700)
    {
        var found = new List<LanHost>();
        var listener = new EventBasedNetListener();
        var net = new NetManager(listener) { UnconnectedMessagesEnabled = true, BroadcastReceiveEnabled = false };
        listener.NetworkReceiveUnconnectedEvent += (endPoint, reader, type) => CollectReply(found, endPoint, reader);
        if (!net.Start()) return found;
        try
        {
            net.SendBroadcast(DiscoverProbe, port);
            // also localhost, so a second game copy on the same PC finds the first one
            net.SendUnconnectedMessage(DiscoverProbe, new IPEndPoint(IPAddress.Loopback, port));
            await PollUntil(net, DateTime.UtcNow.AddMilliseconds(waitMs)).ConfigureAwait(false);
        }
        finally { net.Stop(); }
        return found;
    }

    private static void CollectReply(List<LanHost> found, IPEndPoint endPoint, NetPacketReader reader)
    {
        string text = Encoding.UTF8.GetString(reader.GetRemainingBytes());
        if (!text.StartsWith(DiscoverReplyPrefix)) return;
        string address = endPoint.Address.ToString();
        if (found.Any(host => host.Address == address && host.Port == endPoint.Port)) return;
        found.Add(new LanHost(address, endPoint.Port, text.Substring(DiscoverReplyPrefix.Length)));
    }

    private static async Task PollUntil(NetManager net, DateTime deadline)
    {
        while (DateTime.UtcNow < deadline)
        {
            net.PollEvents();
            await Task.Delay(DiscoveryPollIntervalMs).ConfigureAwait(false);
        }
    }
}
