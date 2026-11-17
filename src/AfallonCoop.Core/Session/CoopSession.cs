using AfallonCoop.Protocol;
using AfallonCoop.Transport;
using AfallonCoop.World;

namespace AfallonCoop.Session;

/// <summary>
/// Star topology: clients talk only to the host, the host relays. Host is player id 0.
/// Everything happens inside <see cref="Tick"/> on the caller's thread.
/// </summary>
public sealed class CoopSession : IDisposable
{
    private const int MaxPayloadBytes = 4000;
    private const int MaxChatLength = 300;

    private delegate void HostHandler(MsgType type, int connection, byte sender, ref NetReader reader);
    private delegate void ClientHandler(MsgType type, ref NetReader reader);

    private readonly ITransport _transport;
    private readonly SessionConfig _config;
    private readonly Func<double> _clock;

    private readonly HostPeers _peers = new();
    private readonly Outbox _outbox;
    private readonly PlayerKeys _playerKeys = new();
    private readonly PauseController _pause = new();
    private readonly JoinPolicy _joinPolicy;
    private readonly SessionLeases _leases;
    private readonly SessionGroups _groups;
    private readonly ReturnPlaces _returnPlaces = new();
    private readonly Dictionary<MsgType, HostHandler> _hostHandlers;
    private readonly Dictionary<MsgType, ClientHandler> _clientHandlers;

    private bool _localMenuOpen;

    public bool IsHost { get; }
    public byte LocalId { get; private set; }
    public SessionState State { get; private set; }
    public string Status { get; private set; } = "";
    public string Transport => _transport.Description;
    public readonly Dictionary<byte, PlayerInfo> Players = new();

    public event Action<PlayerInfo> PlayerJoined;
    public event Action<PlayerInfo> PlayerLeft;
    public event Action<PlayerInfo> PlayerInfoChanged;
    public event Action<PlayerStateMsg> StateReceived;
    public event Action<byte, string> ChatReceived;
    /// <summary>Opaque effect payload from another player (format owned by the game adapter).</summary>
    public event Action<byte, byte[]> FxReceived;
    public event Action<string> Closed;
    public event Action<string> Log;
    /// <summary>Authoritative pause changed (decided by the host, applied by everyone).</summary>
    public event Action<bool, PauseReason> PauseChanged;
    /// <summary>Opaque world payload (mobs...) from the player who owns that object.</summary>
    public event Action<byte, byte[]> WorldReceived;
    /// <summary>A world/fx payload this machine sent: fx?, its first byte (the sender's kind), its size. For traffic views.</summary>
    public event Action<bool, byte, int> PayloadSent;
    /// <summary>Spawner key, new owner (<see cref="LeaseRegistry.NoOwner"/> = dormant), epoch.</summary>
    public event Action<uint, byte, uint> LeaseChanged;
    /// <summary>Groups changed (someone joined, left, a new leader, bot counts).</summary>
    public event Action GroupsChanged;
    /// <summary>The local player is invited by this player.</summary>
    public event Action<byte> GroupInvited;
    /// <summary>A message for the local player about their group (declined, full, removed...).</summary>
    public event Action<string> GroupNotice;
    /// <summary>The group leader sent this bot companion (npc id) of the local player away.</summary>
    public event Action<int> GroupBotDismissed;
    /// <summary>Back in the group after a disconnect: where the character was (scene hash, position).</summary>
    public event Action<int, float, float, float> ReturnRequested;
    /// <summary>A group member's message for the group (sender id, content), see <see cref="GroupCast"/>.</summary>
    public event Action<byte, byte[]> GroupMessage;

    /// <summary>Host: players kept out, by Steam account (0 = none) or install id; asked when someone says hello.</summary>
    public Func<ulong, string, bool> IsBanned;

    private CoopSession(ITransport transport, SessionConfig config, Func<double> clock, bool isHost)
    {
        _transport = transport;
        _config = config;
        _clock = clock;
        IsHost = isHost;
        _outbox = new Outbox(transport, _peers, isHost);
        _joinPolicy = new JoinPolicy(config, (steamId, clientId) => IsBanned?.Invoke(steamId, clientId) == true);
        _hostHandlers = CreateHostHandlers();
        _clientHandlers = CreateClientHandlers();
        _leases = new SessionLeases(_outbox, clock, isHost, () => LocalId, () => State == SessionState.Connected,
            message => Log?.Invoke(message));
        _leases.Changed += (key, owner, epoch) => LeaseChanged?.Invoke(key, owner, epoch);
        _groups = new SessionGroups(_outbox, _peers, _playerKeys, _returnPlaces, Players, clock, isHost,
            () => LocalId, () => State == SessionState.Connected);
        ForwardGroupEvents();
        _transport.Connected += OnConnected;
        _transport.Disconnected += OnDisconnected;
        _transport.Received += OnReceived;
    }

    public static CoopSession StartHost(ITransport transport, SessionConfig config, Func<double> clock)
    {
        var session = new CoopSession(transport, config, clock, isHost: true)
        {
            LocalId = ProtocolInfo.HostId,
            State = SessionState.Connected,
            Status = "Hosting via " + transport.Description,
        };
        session.RegisterHostPlayer();
        return session;
    }

    public static CoopSession StartClient(ITransport transport, SessionConfig config, Func<double> clock) =>
        new(transport, config, clock, isHost: false)
        {
            State = SessionState.Connecting,
            Status = "Connecting via " + transport.Description,
        };

    private void RegisterHostPlayer()
    {
        var host = new PlayerInfo
        {
            Id = ProtocolInfo.HostId,
            Name = _config.PlayerName,
            SteamId = _config.SteamId,
            Key = _playerKeys.Lasting(ProtocolInfo.HostId, _config.SteamId, _config.ClientId, Players.Values),
        };
        Players[host.Id] = host;
        string groupKey = _playerKeys.Assign(host.Id, _config.SteamId, _config.ClientId);
        _groups.PlayerOnline(host.Id, groupKey, _config.PlayerName);
    }

    public PlayerInfo Local => Players.TryGetValue(LocalId, out var player) ? player : null;
    public int PlayerCount => Players.Count;
    public int PingToHost => IsHost ? 0 : _transport.GetPing(ITransport.ServerConnection);

    public bool Paused => _pause.Paused;
    public PauseReason PauseReason => _pause.Reason;
    public bool HostPauseRequested => _pause.HostRequested;

    public IReadOnlyList<GroupInfo> Groups => _groups.All;
    public GroupInfo GroupOf(byte player) => _groups.GroupOf(player);
    public GroupInfo MyGroup => GroupOf(LocalId);
    public bool InSameGroup(byte first, byte second) => _groups.AreInSameGroup(first, second);

    public void Tick()
    {
        if (State == SessionState.Closed) return;
        _transport.Poll();
        if (!IsHost) return;
        _leases.Tick();
        foreach (var connection in _peers.TakeTimedOutHandshakes(_clock()))
            _transport.Disconnect(connection, "handshake timeout");
    }

    /// <summary>Update what others know about us (character chosen, level, scene).</summary>
    public void UpdateLocalInfo(Action<PlayerInfo> change)
    {
        var me = Local;
        if (me == null) return;
        change(me);
        me.Id = LocalId;
        if (IsHost) _groups.SetName(LocalId, me.CharacterName);
        me.Write(_outbox.Begin(MsgType.PlayerInfo));
        _outbox.Publish(Delivery.Reliable);
    }

    public void SendLocalState(PlayerStateMsg state)
    {
        if (State != SessionState.Connected) return;
        state.Id = LocalId;
        state.Write(_outbox.BeginUntyped());
        _outbox.Publish(Delivery.Unreliable);
    }

    public void SendChat(string text)
    {
        if (State != SessionState.Connected || string.IsNullOrWhiteSpace(text)) return;
        var writer = _outbox.Begin(MsgType.Chat);
        writer.U8(LocalId);
        writer.Str(text);
        _outbox.Publish(Delivery.Reliable);
        ChatReceived?.Invoke(LocalId, text);
    }

    /// <summary>
    /// Send an effect played by the local character to everyone else. Unreliable is for streams where a lost packet
    /// is replaced by the next one (positions); events that happen once must be reliable.
    /// </summary>
    public void SendFx(ReadOnlySpan<byte> payload, bool reliable = true) =>
        SendPayload(isFx: true, reliable ? MsgType.Fx : MsgType.FxStream, payload, reliable);

    /// <summary>World payload about objects the local player owns (see <see cref="WorldReceived"/>).</summary>
    public void SendWorld(ReadOnlySpan<byte> payload, bool reliable = true) =>
        SendPayload(isFx: false, reliable ? MsgType.World : MsgType.WorldStream, payload, reliable);

    private void SendPayload(bool isFx, MsgType type, ReadOnlySpan<byte> payload, bool reliable)
    {
        if (!CanSendPayload(payload)) return;
        PayloadSent?.Invoke(isFx, payload[0], payload.Length);
        var writer = _outbox.Begin(type);
        writer.U8(LocalId);
        writer.Blob(payload);
        _outbox.Publish(reliable ? Delivery.Reliable : Delivery.UnreliableUnordered);
    }

    /// <summary>
    /// An unreliable world stream payload for one player only (what is near them: a mob stream is of no use to a player
    /// whose game has no copies of those mobs). The host sends it straight there, a client through the host; it arrives
    /// as <see cref="MsgType.WorldStream"/>.
    /// </summary>
    public void SendWorldStreamTo(byte player, ReadOnlySpan<byte> payload)
    {
        if (player == LocalId || !CanSendPayload(payload)) return;
        PayloadSent?.Invoke(false, payload[0], payload.Length);
        if (IsHost) SendWorldStreamFromHost(player, payload);
        else SendWorldStreamThroughHost(player, payload);
    }

    private void SendWorldStreamFromHost(byte player, ReadOnlySpan<byte> payload)
    {
        int connection = _peers.ConnectionOf(player);
        if (connection < 0) return;
        var writer = _outbox.Begin(MsgType.WorldStream);
        writer.U8(LocalId);
        writer.Blob(payload);
        _outbox.To(connection, Delivery.UnreliableUnordered);
    }

    private void SendWorldStreamThroughHost(byte player, ReadOnlySpan<byte> payload)
    {
        var writer = _outbox.Begin(MsgType.WorldStreamTo);
        writer.U8(LocalId);
        writer.U8(player);
        writer.Blob(payload);
        _outbox.ToHost(Delivery.UnreliableUnordered);
    }

    private bool CanSendPayload(ReadOnlySpan<byte> payload) =>
        State == SessionState.Connected && payload.Length > 0 && payload.Length <= MaxPayloadBytes;

    /// <summary>Host: send a player away, with the reason they see.</summary>
    public void Kick(byte player, string reason)
    {
        int connection = IsHost ? _peers.ConnectionOf(player) : -1;
        if (connection < 0) return;
        _outbox.Begin(MsgType.Reject).Str(reason);
        _outbox.To(connection, Delivery.Reliable);
        _transport.Disconnect(connection, reason);
    }

    /// <summary>Report whether the local player has a menu open (Esc menu, or not in the world).</summary>
    public void SetLocalMenuOpen(bool open)
    {
        if (open == _localMenuOpen) return;
        _localMenuOpen = open;
        if (IsHost) ReportHostMenu(open);
        else if (State == SessionState.Connected) SendMenuStateToHost(open);
    }

    private void ReportHostMenu(bool open)
    {
        _pause.SetMenuOpen(LocalId, open);
        RecomputePause();
    }

    private void SendMenuStateToHost(bool open)
    {
        _outbox.Begin(MsgType.MenuState).Bool(open);
        _outbox.ToHost(Delivery.Reliable);
    }

    /// <summary>Host only: pause or resume the world for everyone.</summary>
    public void SetHostPause(bool on)
    {
        if (!IsHost) return;
        _pause.HostRequested = on;
        RecomputePause();
    }

    private void RecomputePause()
    {
        var reason = _pause.Decide(Players.Keys);
        ApplyPause(reason != PauseReason.None, reason);
    }

    private void ApplyPause(bool paused, PauseReason reason)
    {
        if (!_pause.Apply(paused, reason)) return;
        if (IsHost)
        {
            WritePauseState();
            _outbox.ToEveryone(Delivery.Reliable);
        }
        Log?.Invoke(paused ? $"World paused ({reason})" : "World resumed");
        PauseChanged?.Invoke(paused, reason);
    }

    private void WritePauseState()
    {
        var writer = _outbox.Begin(MsgType.PauseState);
        writer.Bool(_pause.Paused);
        writer.U8((byte)_pause.Reason);
    }

    /// <summary>Who simulates spawner <paramref name="key"/>; <see cref="LeaseRegistry.NoOwner"/> if nobody or unknown yet.</summary>
    public byte LeaseOwner(uint key) => _leases.OwnerOf(key);
    public int LeaseCount => _leases.Count;

    /// <summary>The local player came near spawner <paramref name="key"/> (or left it). The host assigns an owner.</summary>
    /// <param name="leftPlace">Not near any more because this player left the scene or dungeon instance: the spawner's
    /// mobs are gone on this machine, so a fight cannot stay with it; the lease moves at once.</param>
    public void SetInterest(uint key, bool near, bool leftPlace = false) => _leases.SetInterest(key, near, leftPlace);

    public void ReportLeaseCombat(uint key, bool inCombat) => _leases.ReportCombat(key, inCombat);

    public void GroupInvite(byte target) => _groups.Invite(target);
    public void GroupAnswer(bool accept) => _groups.Answer(accept);
    public void GroupLeave() => _groups.Leave();
    public void GroupKick(string memberKey) => _groups.Kick(memberKey);
    public void GroupKick(byte target) => _groups.Kick(target);
    public void GroupPromote(byte target) => _groups.Promote(target);
    public void GroupDismissBot(byte owner, int npcId) => _groups.DismissBot(owner, npcId);

    /// <summary>Send content to the other online members of the local player's group (reliable, through the host).</summary>
    public void GroupCast(byte[] data) => _groups.Cast(data);

    /// <summary>The local player's number of bot companions (they count towards the group limit).</summary>
    public void ReportBots(int count) => _groups.ReportBots(count);

    /// <summary>Free member slots for the local player's bots (the game asks before recruiting one).</summary>
    public int FreeGroupSlots => _groups.FreeSlots;

    private void ForwardGroupEvents()
    {
        _groups.Changed += () => GroupsChanged?.Invoke();
        _groups.Invited += from => GroupInvited?.Invoke(from);
        _groups.Notice += text => GroupNotice?.Invoke(text);
        _groups.BotDismissed += npcId => GroupBotDismissed?.Invoke(npcId);
        _groups.ReturnRequested += (scene, x, y, z) => ReturnRequested?.Invoke(scene, x, y, z);
        _groups.Message += (from, data) => GroupMessage?.Invoke(from, data);
    }

    private void OnConnected(int connection)
    {
        if (IsHost) _peers.AwaitHello(connection, _clock());
        else SendHello(connection);
    }

    private void SendHello(int connection)
    {
        State = SessionState.Handshaking;
        Status = "Handshaking...";
        new HelloMsg
        {
            Protocol = ProtocolInfo.Version,
            ModVersion = _config.ModVersion,
            GameBuild = _config.GameBuild,
            PlayerName = _config.PlayerName,
            SteamId = _config.SteamId,
            ClientId = _config.ClientId,
            Password = _config.Password ?? "",
        }.Write(_outbox.BeginUntyped());
        _outbox.To(connection, Delivery.Reliable);
    }

    private void OnDisconnected(int connection, string reason)
    {
        if (!IsHost)
        {
            Close($"Disconnected: {reason}");
            return;
        }
        _peers.StopAwaitingHello(connection);
        if (_peers.Remove(connection, out var player)) RemovePlayer(player, reason);
    }

    private void RemovePlayer(byte id, string reason)
    {
        if (!Players.Remove(id, out var info)) return;
        _pause.Forget(id);
        _leases.PlayerLeft(id);
        _returnPlaces.SaveForReturn(id, _playerKeys.Of(info));
        _groups.PlayerOffline(id);
        _playerKeys.Release(id);
        _outbox.Begin(MsgType.PlayerLeft).U8(id);
        _outbox.ToEveryone(Delivery.Reliable);
        Log?.Invoke($"{info.Name} left ({reason})");
        PlayerLeft?.Invoke(info);
        RecomputePause();
    }

    private void OnReceived(int connection, ReadOnlyMemory<byte> data)
    {
        try
        {
            var reader = new NetReader(data.Span);
            var type = (MsgType)reader.U8();
            if (IsHost) HandleAsHost(connection, type, ref reader);
            else HandleAsClient(type, ref reader);
        }
        catch (ProtocolException e)
        {
            Log?.Invoke($"Bad packet from {connection}: {e.Message}");
            if (IsHost) _transport.Disconnect(connection, "protocol error");
        }
        catch (Exception e)
        {
            // a bug in one handler: that message is lost, the rest of the batch and the connection are not
            Log?.Invoke($"Handling a message from {connection} failed: {e}");
        }
    }

    private Dictionary<MsgType, HostHandler> CreateHostHandlers() => new()
    {
        [MsgType.PlayerInfo] = OnPlayerInfoFromPeer,
        [MsgType.PlayerState] = OnPlayerStateFromPeer,
        [MsgType.MenuState] = OnMenuStateFromPeer,
        [MsgType.Fx] = RelayPayloadFromPeer,
        [MsgType.FxStream] = RelayPayloadFromPeer,
        [MsgType.World] = RelayPayloadFromPeer,
        [MsgType.WorldStream] = RelayPayloadFromPeer,
        [MsgType.WorldStreamTo] = RelayWorldStreamToOnePlayer,
        [MsgType.Interest] = OnInterestFromPeer,
        [MsgType.LeaseCombat] = OnLeaseCombatFromPeer,
        [MsgType.Group] = OnGroupOpFromPeer,
        [MsgType.Chat] = OnChatFromPeer,
    };

    private void HandleAsHost(int connection, MsgType type, ref NetReader reader)
    {
        if (type == MsgType.Hello)
        {
            HostAccept(connection, HelloMsg.Read(ref reader));
            return;
        }
        if (!_peers.TryGetPlayer(connection, out byte sender)) return;
        if (_hostHandlers.TryGetValue(type, out var handle)) handle(type, connection, sender, ref reader);
    }

    private void OnPlayerInfoFromPeer(MsgType type, int connection, byte sender, ref NetReader reader)
    {
        var info = PlayerInfo.Read(ref reader);
        var existing = Players[sender];
        info.Id = sender;
        info.Name = existing.Name;
        info.SteamId = existing.SteamId;
        info.Key = existing.Key;
        Players[sender] = info;
        _groups.SetName(sender, info.CharacterName);
        info.Write(_outbox.Begin(MsgType.PlayerInfo));
        _outbox.ToEveryone(Delivery.Reliable, exceptConnection: connection);
        PlayerInfoChanged?.Invoke(info);
    }

    private void OnPlayerStateFromPeer(MsgType type, int connection, byte sender, ref NetReader reader)
    {
        var state = PlayerStateMsg.Read(ref reader);
        state.Id = sender;
        _returnPlaces.Observe(sender, state);
        state.Write(_outbox.BeginUntyped());
        _outbox.ToEveryone(Delivery.Unreliable, exceptConnection: connection);
        StateReceived?.Invoke(state);
    }

    private void OnMenuStateFromPeer(MsgType type, int connection, byte sender, ref NetReader reader)
    {
        _pause.SetMenuOpen(sender, reader.Bool());
        RecomputePause();
    }

    private void RelayPayloadFromPeer(MsgType type, int connection, byte sender, ref NetReader reader)
    {
        reader.U8();
        var payload = reader.Blob();
        var writer = _outbox.Begin(type);
        writer.U8(sender);
        writer.Blob(payload);
        _outbox.ToEveryone(DeliveryOf(type), exceptConnection: connection);
        RaisePayloadReceived(type, sender, payload);
    }

    private void RelayWorldStreamToOnePlayer(MsgType type, int connection, byte sender, ref NetReader reader)
    {
        reader.U8();
        byte target = reader.U8();
        var payload = reader.Blob();
        if (target == LocalId)
        {
            WorldReceived?.Invoke(sender, payload);
            return;
        }
        int targetConnection = _peers.ConnectionOf(target);
        if (targetConnection < 0 || targetConnection == connection) return;
        var writer = _outbox.Begin(MsgType.WorldStream);
        writer.U8(sender);
        writer.Blob(payload);
        _outbox.To(targetConnection, Delivery.UnreliableUnordered);
    }

    private void OnInterestFromPeer(MsgType type, int connection, byte sender, ref NetReader reader) =>
        _leases.OnInterestFromPeer(sender, ref reader);

    private void OnLeaseCombatFromPeer(MsgType type, int connection, byte sender, ref NetReader reader) =>
        _leases.OnCombatFromPeer(sender, ref reader);

    private void OnGroupOpFromPeer(MsgType type, int connection, byte sender, ref NetReader reader) =>
        _groups.OnOpFromPeer(sender, ref reader);

    private void OnChatFromPeer(MsgType type, int connection, byte sender, ref NetReader reader)
    {
        reader.U8();
        string text = reader.Str();
        if (text.Length > MaxChatLength) text = text.Substring(0, MaxChatLength);
        var writer = _outbox.Begin(MsgType.Chat);
        writer.U8(sender);
        writer.Str(text);
        _outbox.ToEveryone(Delivery.Reliable, exceptConnection: connection);
        ChatReceived?.Invoke(sender, text);
    }

    private static Delivery DeliveryOf(MsgType type) =>
        type is MsgType.Fx or MsgType.World ? Delivery.Reliable : Delivery.UnreliableUnordered;

    private void RaisePayloadReceived(MsgType type, byte sender, byte[] payload)
    {
        if (type is MsgType.Fx or MsgType.FxStream) FxReceived?.Invoke(sender, payload);
        else WorldReceived?.Invoke(sender, payload);
    }

    private void HostAccept(int connection, HelloMsg hello)
    {
        _peers.StopAwaitingHello(connection);
        string rejection = _joinPolicy.RejectionReason(hello, _peers.Has(connection), Players.Count);
        if (rejection != null)
        {
            Reject(connection, hello, rejection);
            return;
        }
        var info = AdmitPlayer(connection, hello);
        SendWelcome(connection, info.Id);
        AnnounceJoined(connection, info);
        SendCurrentWorldState(connection);
        _groups.PlayerOnline(info.Id, _playerKeys.Assign(info.Id, hello.SteamId, hello.ClientId), info.Name);
        _pause.SetMenuOpen(info.Id, open: true);
        RecomputePause();
        WritePauseState();
        _outbox.To(connection, Delivery.Reliable);
    }

    private void Reject(int connection, HelloMsg hello, string reason)
    {
        _outbox.Begin(MsgType.Reject).Str(reason);
        _outbox.To(connection, Delivery.Reliable);
        _transport.Disconnect(connection, reason);
        Log?.Invoke($"Rejected {hello.PlayerName}: {reason}");
    }

    private PlayerInfo AdmitPlayer(int connection, HelloMsg hello)
    {
        byte id = FirstFreePlayerId();
        var info = new PlayerInfo
        {
            Id = id,
            Name = string.IsNullOrWhiteSpace(hello.PlayerName) ? $"Player {id}" : hello.PlayerName,
            SteamId = hello.SteamId,
            Key = _playerKeys.Lasting(id, hello.SteamId, hello.ClientId, Players.Values),
        };
        Players[id] = info;
        _peers.Admit(connection, id);
        return info;
    }

    private byte FirstFreePlayerId()
    {
        byte id = 1;
        while (Players.ContainsKey(id)) id++;
        return id;
    }

    private void SendWelcome(int connection, byte assignedId)
    {
        var writer = _outbox.Begin(MsgType.Welcome);
        writer.U8(assignedId);
        writer.U8((byte)Players.Count);
        foreach (var player in Players.Values) player.Write(writer);
        _outbox.To(connection, Delivery.Reliable);
    }

    private void AnnounceJoined(int connection, PlayerInfo info)
    {
        info.Write(_outbox.Begin(MsgType.PlayerJoined));
        _outbox.ToEveryone(Delivery.Reliable, exceptConnection: connection);
        Log?.Invoke($"{info.Name} joined as #{info.Id}");
        PlayerJoined?.Invoke(info);
    }

    private void SendCurrentWorldState(int connection)
    {
        _leases.SendCurrentOwners(connection);
        _groups.SendCurrentState(connection);
    }

    private Dictionary<MsgType, ClientHandler> CreateClientHandlers() => new()
    {
        [MsgType.Welcome] = OnWelcome,
        [MsgType.Fx] = OnRelayedPayload,
        [MsgType.FxStream] = OnRelayedPayload,
        [MsgType.World] = OnRelayedPayload,
        [MsgType.WorldStream] = OnRelayedPayload,
        [MsgType.LeaseOwner] = OnLeaseOwner,
        [MsgType.Group] = OnGroupOpFromHost,
        [MsgType.PauseState] = OnPauseState,
        [MsgType.Reject] = OnReject,
        [MsgType.PlayerJoined] = OnPlayerJoined,
        [MsgType.PlayerLeft] = OnPlayerLeft,
        [MsgType.PlayerInfo] = OnPlayerInfo,
        [MsgType.PlayerState] = OnPlayerState,
        [MsgType.Chat] = OnChat,
    };

    private void HandleAsClient(MsgType type, ref NetReader reader)
    {
        if (_clientHandlers.TryGetValue(type, out var handle)) handle(type, ref reader);
    }

    private void OnWelcome(MsgType type, ref NetReader reader)
    {
        LocalId = reader.U8();
        int count = reader.U8();
        Players.Clear();
        for (int i = 0; i < count; i++)
        {
            var player = PlayerInfo.Read(ref reader);
            Players[player.Id] = player;
        }
        State = SessionState.Connected;
        Status = "Connected via " + _transport.Description;
        Log?.Invoke($"Joined as #{LocalId}, {Players.Count} players");
        foreach (var player in Players.Values.Where(p => p.Id != LocalId)) PlayerJoined?.Invoke(player);
        ReportStateKnownBeforeWelcome();
    }

    private void ReportStateKnownBeforeWelcome()
    {
        SendMenuStateToHost(_localMenuOpen);
        _leases.ResendInterest();
        _groups.ResendBots();
    }

    private void OnRelayedPayload(MsgType type, ref NetReader reader)
    {
        byte sender = reader.U8();
        var payload = reader.Blob();
        bool isKnownRemotePlayer = sender != LocalId && Players.ContainsKey(sender);
        if (isKnownRemotePlayer) RaisePayloadReceived(type, sender, payload);
    }

    private void OnGroupOpFromHost(MsgType type, ref NetReader reader) => _groups.OnOpFromHost(ref reader);

    private void OnLeaseOwner(MsgType type, ref NetReader reader) => _leases.OnOwnerAnnounced(ref reader);

    private void OnPauseState(MsgType type, ref NetReader reader)
    {
        bool paused = reader.Bool();
        ApplyPause(paused, (PauseReason)reader.U8());
    }

    private void OnReject(MsgType type, ref NetReader reader)
    {
        Status = reader.Str();
        Close(Status);
    }

    private void OnPlayerJoined(MsgType type, ref NetReader reader)
    {
        var player = PlayerInfo.Read(ref reader);
        Players[player.Id] = player;
        PlayerJoined?.Invoke(player);
    }

    private void OnPlayerLeft(MsgType type, ref NetReader reader)
    {
        byte id = reader.U8();
        if (Players.Remove(id, out var player)) PlayerLeft?.Invoke(player);
    }

    private void OnPlayerInfo(MsgType type, ref NetReader reader)
    {
        var player = PlayerInfo.Read(ref reader);
        if (player.Id == LocalId) return;
        Players[player.Id] = player;
        PlayerInfoChanged?.Invoke(player);
    }

    /// <summary>Unreliable, so it can come after PlayerLeft: it would bring back a puppet whose sequence hides the next one with that id.</summary>
    private void OnPlayerState(MsgType type, ref NetReader reader)
    {
        var state = PlayerStateMsg.Read(ref reader);
        if (state.Id != LocalId && Players.ContainsKey(state.Id)) StateReceived?.Invoke(state);
    }

    private void OnChat(MsgType type, ref NetReader reader)
    {
        byte sender = reader.U8();
        ChatReceived?.Invoke(sender, reader.Str());
    }

    private void Close(string reason)
    {
        if (State == SessionState.Closed) return;
        State = SessionState.Closed;
        Status = reason;
        Log?.Invoke("Session closed: " + reason);
        Closed?.Invoke(reason);
    }

    public void Dispose()
    {
        if (State != SessionState.Closed) DisconnectEveryone();
        Close("Left the session");
        _transport.Connected -= OnConnected;
        _transport.Disconnected -= OnDisconnected;
        _transport.Received -= OnReceived;
        _transport.Dispose();
    }

    private void DisconnectEveryone()
    {
        if (IsHost)
            foreach (var connection in _peers.Connections.ToList()) _transport.Disconnect(connection, "Host left the session");
        else
            _transport.Disconnect(ITransport.ServerConnection, "left");
        _transport.Poll();
    }
}
