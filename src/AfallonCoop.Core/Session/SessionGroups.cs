using AfallonCoop.Protocol;
using AfallonCoop.Transport;
using AfallonCoop.World;

namespace AfallonCoop.Session;

/// <summary>
/// Player groups for one session: members ask the host (who owns the <see cref="GroupRegistry"/>), the host
/// announces the result, everyone keeps a <see cref="GroupDirectory"/> copy and raises the notices.
/// </summary>
internal sealed class SessionGroups
{
    private const int BotCountUnknown = -1;

    private readonly Outbox _outbox;
    private readonly HostPeers _peers;
    private readonly PlayerKeys _playerKeys;
    private readonly ReturnPlaces _returnPlaces;
    private readonly IReadOnlyDictionary<byte, PlayerInfo> _players;
    private readonly Func<double> _clock;
    private readonly bool _isHost;
    private readonly Func<byte> _localId;
    private readonly Func<bool> _isConnected;

    private readonly GroupDirectory _directory = new();
    private readonly GroupRegistry _registry = new();
    private int _reportedBots = BotCountUnknown;

    public SessionGroups(Outbox outbox, HostPeers peers, PlayerKeys playerKeys, ReturnPlaces returnPlaces,
        IReadOnlyDictionary<byte, PlayerInfo> players, Func<double> clock, bool isHost, Func<byte> localId,
        Func<bool> isConnected)
    {
        _outbox = outbox;
        _peers = peers;
        _playerKeys = playerKeys;
        _returnPlaces = returnPlaces;
        _players = players;
        _clock = clock;
        _isHost = isHost;
        _localId = localId;
        _isConnected = isConnected;
        ListenToRegistry();
    }

    public event Action Changed;
    public event Action<byte> Invited;
    public event Action<string> Notice;
    public event Action<int> BotDismissed;
    public event Action<int, float, float, float> ReturnRequested;
    public event Action<byte, byte[]> Message;

    public IReadOnlyList<GroupInfo> All => _directory.All;

    public GroupInfo GroupOf(byte player) => _directory.GroupOf(player);

    public bool AreInSameGroup(byte first, byte second) => _directory.AreInSameGroup(first, second);

    public int FreeSlots
    {
        get
        {
            var group = GroupOf(_localId());
            if (group != null) return GroupRegistry.MaxSize - group.Size;
            int ownBots = Math.Max(0, _reportedBots);
            return GroupRegistry.MaxSize - 1 - ownBots;
        }
    }

    public void PlayerOnline(byte id, string key, string name) => _registry.PlayerOnline(id, key, name);

    public void PlayerOffline(byte id) => _registry.PlayerOffline(id);

    public void SetName(byte id, string name) => _registry.SetName(id, name);

    public void Invite(byte target) => Request(GroupOp.Invite, w => w.U8(target));

    public void Answer(bool accept) => Request(GroupOp.Answer, w => w.Bool(accept));

    public void Leave() => Request(GroupOp.Leave, _ => { });

    public void Kick(string memberKey) => Request(GroupOp.Kick, w => w.Str(memberKey));

    public void Kick(byte target)
    {
        var member = GroupOf(_localId())?.Member(target);
        if (member != null) Kick(member.Key);
    }

    public void Promote(byte target) => Request(GroupOp.Promote, w => w.U8(target));

    public void DismissBot(byte owner, int npcId) =>
        Request(GroupOp.DismissBot, w => { w.U8(owner); w.I32(npcId); });

    public void Cast(byte[] data)
    {
        if (data == null || data.Length > ushort.MaxValue) return;
        Request(GroupOp.Cast, w => w.Blob(data));
    }

    public void ReportBots(int count)
    {
        if (count == _reportedBots) return;
        _reportedBots = count;
        Request(GroupOp.Bots, w => w.U8((byte)Math.Clamp(count, 0, byte.MaxValue)));
    }

    /// <summary>Client: after the welcome, tell the host again about bots counted before the connection existed.</summary>
    public void ResendBots()
    {
        if (_reportedBots <= 0) return;
        int bots = _reportedBots;
        _reportedBots = BotCountUnknown;
        ReportBots(bots);
    }

    /// <summary>Host: bring a joining connection up to date with the group state.</summary>
    public void SendCurrentState(int connection)
    {
        WriteState();
        _outbox.To(connection, Delivery.Reliable);
    }

    public void OnOpFromPeer(byte sender, ref NetReader reader) => HostOp(sender, (GroupOp)reader.U8(), ref reader);

    public void OnOpFromHost(ref NetReader reader)
    {
        switch ((GroupOp)reader.U8())
        {
            case GroupOp.State: _directory.ReplaceFrom(ref reader); Changed?.Invoke(); break;
            case GroupOp.InvitedBy: Invited?.Invoke(reader.U8()); break;
            case GroupOp.Notice: Notice?.Invoke(reader.Str()); break;
            case GroupOp.BotDismissed: BotDismissed?.Invoke(reader.I32()); break;
            case GroupOp.ReturnTo: RaiseReturnRequested(ref reader); break;
            case GroupOp.FromMember: RaiseMessage(ref reader); break;
        }
    }

    private void Request(GroupOp op, Action<NetWriter> writeBody)
    {
        if (!_isConnected()) return;
        if (_isHost) HandleOwnOp(op, writeBody);
        else SendOpToHost(op, writeBody);
    }

    private void HandleOwnOp(GroupOp op, Action<NetWriter> writeBody)
    {
        var body = new NetWriter();
        writeBody(body);
        var reader = new NetReader(body.Span.ToArray());
        HostOp(_localId(), op, ref reader);
    }

    private void SendOpToHost(GroupOp op, Action<NetWriter> writeBody)
    {
        var writer = _outbox.Begin(MsgType.Group);
        writer.U8((byte)op);
        writeBody(writer);
        _outbox.ToHost(Delivery.Reliable);
    }

    private void HostOp(byte from, GroupOp op, ref NetReader reader)
    {
        double now = _clock();
        switch (op)
        {
            case GroupOp.Invite: _registry.Invite(from, reader.U8(), now); break;
            case GroupOp.Answer: _registry.Answer(from, reader.Bool(), now); break;
            case GroupOp.Leave: _registry.Leave(from); break;
            case GroupOp.Kick: _registry.Kick(from, reader.Str()); break;
            case GroupOp.Promote: _registry.Promote(from, reader.U8()); break;
            case GroupOp.DismissBot: HostDismissBot(from, ref reader); break;
            case GroupOp.Bots: _registry.SetBots(from, reader.U8()); break;
            case GroupOp.Cast: RelayCast(from, reader.Blob()); break;
        }
    }

    private void HostDismissBot(byte leader, ref NetReader reader)
    {
        byte owner = reader.U8();
        _registry.DismissBot(leader, owner, reader.I32());
    }

    private void RelayCast(byte from, byte[] data)
    {
        var group = _registry.GroupOf(from);
        if (group == null) return;
        foreach (var member in group.Members)
        {
            if (!member.Online || member.Id == from) continue;
            SendToPlayer(member.Id, GroupOp.FromMember,
                w => { w.U8(from); w.Blob(data); },
                () => Message?.Invoke(from, data));
        }
    }

    /// <summary>Host: deliver a group message to one player, or run <paramref name="deliverLocally"/> when it is the host.</summary>
    private void SendToPlayer(byte player, GroupOp op, Action<NetWriter> writeBody, Action deliverLocally)
    {
        if (player == _localId())
        {
            deliverLocally();
            return;
        }
        int connection = _peers.ConnectionOf(player);
        if (connection < 0) return;
        var writer = _outbox.Begin(MsgType.Group);
        writer.U8((byte)op);
        writeBody(writer);
        _outbox.To(connection, Delivery.Reliable);
    }

    private void ListenToRegistry()
    {
        _registry.Changed += OnDecided;
        _registry.Notice += (player, text) =>
            SendToPlayer(player, GroupOp.Notice, w => w.Str(text), () => Notice?.Invoke(text));
        _registry.InviteSent += (to, from) =>
            SendToPlayer(to, GroupOp.InvitedBy, w => w.U8(from), () => Invited?.Invoke(from));
        _registry.BotDismissed += (owner, npcId) =>
            SendToPlayer(owner, GroupOp.BotDismissed, w => w.I32(npcId), () => BotDismissed?.Invoke(npcId));
        _registry.Rejoined += SendReturnPlace;
    }

    private void SendReturnPlace(byte player)
    {
        if (!_players.TryGetValue(player, out var info)) return;
        if (!_returnPlaces.TryGet(_playerKeys.Of(info), out var place)) return;
        SendToPlayer(player, GroupOp.ReturnTo, place.Write, () => { });
    }

    private void OnDecided()
    {
        WriteState();
        _outbox.ToEveryone(Delivery.Reliable);
        var reader = new NetReader(_outbox.Pending.ToArray());
        reader.U8();
        reader.U8();
        _directory.ReplaceFrom(ref reader);
        Changed?.Invoke();
    }

    private void WriteState()
    {
        var writer = _outbox.Begin(MsgType.Group);
        writer.U8((byte)GroupOp.State);
        GroupDirectory.Write(writer, _registry.All);
    }

    private void RaiseReturnRequested(ref NetReader reader)
    {
        int scene = reader.I32();
        float x = reader.F32(), y = reader.F32(), z = reader.F32();
        ReturnRequested?.Invoke(scene, x, y, z);
    }

    private void RaiseMessage(ref NetReader reader)
    {
        byte from = reader.U8();
        Message?.Invoke(from, reader.Blob());
    }
}
