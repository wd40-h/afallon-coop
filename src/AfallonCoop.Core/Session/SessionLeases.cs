using AfallonCoop.Protocol;
using AfallonCoop.Transport;
using AfallonCoop.World;

namespace AfallonCoop.Session;

/// <summary>
/// Spawner ownership for one session: the host decides through <see cref="LeaseRegistry"/> and announces,
/// everyone keeps a <see cref="LeaseView"/>, clients report their interest and combat state to the host.
/// </summary>
internal sealed class SessionLeases
{
    private readonly Outbox _outbox;
    private readonly Func<double> _clock;
    private readonly bool _isHost;
    private readonly Func<byte> _localId;
    private readonly Func<bool> _isConnected;
    private readonly Action<string> _log;

    private readonly LeaseRegistry _registry = new();
    private readonly LeaseView _view = new();
    private readonly HashSet<uint> _interest = new();
    private readonly Dictionary<uint, bool> _combat = new();

    public SessionLeases(Outbox outbox, Func<double> clock, bool isHost, Func<byte> localId, Func<bool> isConnected,
        Action<string> log)
    {
        _outbox = outbox;
        _clock = clock;
        _isHost = isHost;
        _localId = localId;
        _isConnected = isConnected;
        _log = log;
        _registry.HostId = ProtocolInfo.HostId;
        _registry.OwnerChanged += OnOwnerChanged;
    }

    public event Action<uint, byte, uint> Changed;

    public int Count => _view.Count;

    public byte OwnerOf(uint key) => _view.OwnerOf(key);

    public void Tick() => _registry.Tick(_clock());

    public void PlayerLeft(byte id) => _registry.PlayerLeft(id, _clock());

    public void SetInterest(uint key, bool near, bool leftPlace)
    {
        bool changed = near ? _interest.Add(key) : _interest.Remove(key);
        if (!changed) return;
        if (_isHost) SubscribeHost(key, near, leftPlace);
        else if (_isConnected()) SendInterest(key, near, leftPlace);
    }

    public void ReportCombat(uint key, bool inCombat)
    {
        if (_combat.TryGetValue(key, out var wasInCombat) && wasInCombat == inCombat) return;
        _combat[key] = inCombat;
        if (_isHost) _registry.SetCombat(_localId(), FormatKey(key), inCombat, _clock());
        else if (_isConnected()) SendCombat(key, inCombat);
    }

    /// <summary>Client: after the welcome, tell the host again what the local player is near.</summary>
    public void ResendInterest()
    {
        foreach (var key in _interest) SendInterest(key, near: true, leftPlace: false);
    }

    /// <summary>Host: bring a joining connection up to date with every known owner.</summary>
    public void SendCurrentOwners(int connection)
    {
        foreach (var entry in _view.Entries)
        {
            WriteOwner(entry.Key, entry.Value.owner, entry.Value.epoch);
            _outbox.To(connection, Delivery.Reliable);
        }
    }

    public void OnInterestFromPeer(byte sender, ref NetReader reader)
    {
        string key = FormatKey(reader.U32());
        bool near = reader.Bool();
        bool leftPlace = reader.Remaining > 0 && reader.Bool();
        if (near) _registry.Subscribe(sender, key, _clock());
        else _registry.Unsubscribe(sender, key, _clock(), leftPlace);
    }

    public void OnCombatFromPeer(byte sender, ref NetReader reader)
    {
        string key = FormatKey(reader.U32());
        _registry.SetCombat(sender, key, reader.Bool(), _clock());
    }

    public void OnOwnerAnnounced(ref NetReader reader)
    {
        uint key = reader.U32();
        byte owner = reader.U8();
        ApplyOwner(key, owner, reader.U32());
    }

    private void SubscribeHost(uint key, bool near, bool leftPlace)
    {
        if (near) _registry.Subscribe(_localId(), FormatKey(key), _clock());
        else _registry.Unsubscribe(_localId(), FormatKey(key), _clock(), leftPlace);
    }

    private void SendInterest(uint key, bool near, bool leftPlace)
    {
        var writer = _outbox.Begin(MsgType.Interest);
        writer.U32(key);
        writer.Bool(near);
        writer.Bool(leftPlace);
        _outbox.ToHost(Delivery.Reliable);
    }

    private void SendCombat(uint key, bool inCombat)
    {
        var writer = _outbox.Begin(MsgType.LeaseCombat);
        writer.U32(key);
        writer.Bool(inCombat);
        _outbox.ToHost(Delivery.Reliable);
    }

    private void OnOwnerChanged(LeaseRegistry.Lease lease, string reason)
    {
        uint key = Convert.ToUInt32(lease.Key, 16);
        string owner = lease.Owner == LeaseRegistry.NoOwner ? "none" : "#" + lease.Owner;
        _log($"Spawner {lease.Key}: owner {owner} ({reason})");
        WriteOwner(key, lease.Owner, lease.Epoch);
        _outbox.ToEveryone(Delivery.Reliable);
        ApplyOwner(key, lease.Owner, lease.Epoch);
    }

    private void WriteOwner(uint key, byte owner, uint epoch)
    {
        var writer = _outbox.Begin(MsgType.LeaseOwner);
        writer.U32(key);
        writer.U8(owner);
        writer.U32(epoch);
    }

    private void ApplyOwner(uint key, byte owner, uint epoch)
    {
        if (!_view.TryUpdate(key, owner, epoch)) return;
        if (owner != _localId()) _combat.Remove(key);
        Changed?.Invoke(key, owner, epoch);
    }

    private static string FormatKey(uint key) => key.ToString("x8");
}
