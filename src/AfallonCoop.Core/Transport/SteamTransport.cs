using System.Runtime.InteropServices;
using Steamworks;

namespace AfallonCoop.Transport;

/// <summary>
/// Steam P2P over ISteamNetworkingSockets: NAT traversal and Valve's relay (SDR), no port forwarding.
/// Requires <see cref="SteamPlatform.Initialized"/> and SteamAPI.RunCallbacks() being pumped.
/// </summary>
public sealed class SteamTransport : ITransport
{
    private const int VirtualPort = 0;
    private const int MaxMessagesPerPoll = 256;
    private const int InitialReceiveBufferBytes = 4096;
    private const int NoOptions = 0;
    private const int NoReasonCode = 0;

    private readonly HSteamListenSocket _listen = HSteamListenSocket.Invalid;
    private readonly HSteamNetPollGroup _group;
    private readonly Dictionary<int, HSteamNetConnection> _connections = new();
    private readonly Dictionary<uint, int> _idByHandle = new();
    private readonly IntPtr[] _messageBuffer = new IntPtr[MaxMessagesPerPoll];
    private readonly Callback<SteamNetConnectionStatusChangedCallback_t> _statusCallback;
    private readonly Queue<Action> _pending = new();
    private byte[] _receiveBuffer = new byte[InitialReceiveBufferBytes];
    private int _nextId = 1;

    public bool IsServer { get; }
    public string Description { get; }
    public event Action<int> Connected;
    public event Action<int, string> Disconnected;
    public event Action<int, ReadOnlyMemory<byte>> Received;

    private SteamTransport(bool server, string description)
    {
        if (!SteamPlatform.Initialized)
            throw new InvalidOperationException("Steam is not running or failed to initialize.");
        IsServer = server;
        Description = description;
        SteamNetworkingUtils.InitRelayNetworkAccess();
        _group = SteamNetworkingSockets.CreatePollGroup();
        _statusCallback = Callback<SteamNetConnectionStatusChangedCallback_t>.Create(OnStatusChanged);
        if (server) _listen = SteamNetworkingSockets.CreateListenSocketP2P(VirtualPort, NoOptions, null);
    }

    public static SteamTransport Host() => new(true, "Steam host");

    public static SteamTransport Connect(CSteamID host)
    {
        var transport = new SteamTransport(false, $"Steam -> {host.m_SteamID}");
        var identity = new SteamNetworkingIdentity();
        identity.SetSteamID(host);
        var connection = SteamNetworkingSockets.ConnectP2P(ref identity, VirtualPort, NoOptions, null);
        transport.Track(connection, ITransport.ServerConnection);
        return transport;
    }

    public void Poll()
    {
        RunPending();
        ForEachUnreadMessage(DeliverNow);
        RunPending();
    }

    public unsafe void Send(int connection, ReadOnlySpan<byte> data, Delivery delivery)
    {
        if (!_connections.TryGetValue(connection, out var handle)) return;
        int flags = delivery == Delivery.Reliable
            ? Constants.k_nSteamNetworkingSend_Reliable
            : Constants.k_nSteamNetworkingSend_UnreliableNoDelay;
        fixed (byte* bytes = data)
            SteamNetworkingSockets.SendMessageToConnection(handle, (IntPtr)bytes, (uint)data.Length, flags, out _);
    }

    public void Disconnect(int connection, string reason)
    {
        if (!_connections.TryGetValue(connection, out var handle)) return;
        SteamNetworkingSockets.CloseConnection(handle, NoReasonCode, reason, true);
        Forget(connection, handle);
        _pending.Enqueue(() => Disconnected?.Invoke(connection, reason));
    }

    public int GetPing(int connection)
    {
        if (!_connections.TryGetValue(connection, out var handle)) return -1;
        var status = new SteamNetConnectionRealTimeStatus_t();
        var lanes = new SteamNetConnectionRealTimeLaneStatus_t();
        var result = SteamNetworkingSockets.GetConnectionRealTimeStatus(handle, ref status, 0, ref lanes);
        return result == EResult.k_EResultOK ? status.m_nPing : -1;
    }

    public void Dispose()
    {
        foreach (var handle in _connections.Values)
            SteamNetworkingSockets.CloseConnection(handle, NoReasonCode, "shutdown", true);
        _connections.Clear();
        _idByHandle.Clear();
        if (_listen != HSteamListenSocket.Invalid) SteamNetworkingSockets.CloseListenSocket(_listen);
        SteamNetworkingSockets.DestroyPollGroup(_group);
        _statusCallback.Dispose();
    }

    private void Track(HSteamNetConnection handle, int id)
    {
        _connections[id] = handle;
        _idByHandle[handle.m_HSteamNetConnection] = id;
        SteamNetworkingSockets.SetConnectionPollGroup(handle, _group);
    }

    private void Forget(int id, HSteamNetConnection handle)
    {
        _connections.Remove(id);
        _idByHandle.Remove(handle.m_HSteamNetConnection);
    }

    private void RunPending()
    {
        while (_pending.Count > 0) _pending.Dequeue()();
    }

    private void OnStatusChanged(SteamNetConnectionStatusChangedCallback_t change)
    {
        var handle = change.m_hConn;
        var state = change.m_info.m_eState;
        bool known = _idByHandle.TryGetValue(handle.m_HSteamNetConnection, out int id);
        if (!known)
        {
            if (IsIncomingRequest(change)) AnswerIncoming(handle, change);
            return;
        }
        if (state == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected)
            _pending.Enqueue(() => Connected?.Invoke(id));
        else if (IsClosing(state))
            CloseAfterPeerLoss(id, handle, change);
    }

    private bool IsIncomingRequest(SteamNetConnectionStatusChangedCallback_t change) =>
        IsServer
        && change.m_info.m_eState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting
        && change.m_info.m_hListenSocket == _listen;

    private static bool IsClosing(ESteamNetworkingConnectionState state) =>
        state == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer
        || state == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally;

    private void AnswerIncoming(HSteamNetConnection handle, SteamNetConnectionStatusChangedCallback_t change)
    {
        var guest = change.m_info.m_identityRemote.GetSteamID();
        if (!IsHostFriend(guest))
            SteamNetworkingSockets.CloseConnection(handle, NoReasonCode, "Only the host's Steam friends can join.", false);
        else if (SteamNetworkingSockets.AcceptConnection(handle) == EResult.k_EResultOK)
            Track(handle, _nextId++);
        else
            SteamNetworkingSockets.CloseConnection(handle, NoReasonCode, "accept failed", false);
    }

    /// <summary>Only the host's Steam friends, as the friends-only lobby: anyone else who knows the host's id stays out.</summary>
    private static bool IsHostFriend(CSteamID guest) =>
        guest == SteamUser.GetSteamID() || SteamFriends.HasFriend(guest, EFriendFlags.k_EFriendFlagImmediate);

    /// <summary>Closing drops the unread messages, so what the peer sent last is queued before the disconnect.</summary>
    private void CloseAfterPeerLoss(int id, HSteamNetConnection handle, SteamNetConnectionStatusChangedCallback_t change)
    {
        string reason = string.IsNullOrEmpty(change.m_info.m_szEndDebug)
            ? change.m_info.m_eState.ToString()
            : change.m_info.m_szEndDebug;
        ForEachUnreadMessage(QueueForLater);
        SteamNetworkingSockets.CloseConnection(handle, NoReasonCode, null, false);
        Forget(id, handle);
        _pending.Enqueue(() => Disconnected?.Invoke(id, reason));
    }

    private void ForEachUnreadMessage(Action<int, SteamNetworkingMessage_t> handle)
    {
        int received;
        do
        {
            received = SteamNetworkingSockets.ReceiveMessagesOnPollGroup(_group, _messageBuffer, MaxMessagesPerPoll);
            for (int i = 0; i < received; i++) HandleAndRelease(_messageBuffer[i], handle);
        }
        while (received == MaxMessagesPerPoll);
    }

    private void HandleAndRelease(IntPtr messagePointer, Action<int, SteamNetworkingMessage_t> handle)
    {
        var message = SteamNetworkingMessage_t.FromIntPtr(messagePointer);
        try
        {
            if (_idByHandle.TryGetValue(message.m_conn.m_HSteamNetConnection, out int id)) handle(id, message);
        }
        finally { SteamNetworkingMessage_t.Release(messagePointer); }
    }

    /// <summary>The buffer is reused: the data is valid during the callback.</summary>
    private void DeliverNow(int id, SteamNetworkingMessage_t message)
    {
        if (_receiveBuffer.Length < message.m_cbSize)
            _receiveBuffer = new byte[Math.Max(message.m_cbSize, _receiveBuffer.Length * 2)];
        Marshal.Copy(message.m_pData, _receiveBuffer, 0, message.m_cbSize);
        Received?.Invoke(id, new ReadOnlyMemory<byte>(_receiveBuffer, 0, message.m_cbSize));
    }

    private void QueueForLater(int id, SteamNetworkingMessage_t message)
    {
        var data = new byte[message.m_cbSize];
        Marshal.Copy(message.m_pData, data, 0, message.m_cbSize);
        _pending.Enqueue(() => Received?.Invoke(id, data));
    }
}
