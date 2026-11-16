namespace AfallonCoop.World;

/// <summary>
/// Host-side registry of who simulates which spawner (MODDING_PLAN §4.4). A spawner is active on exactly one machine,
/// so mobs are never duplicated. Policy:
///  - the first player to come near a free spawner gets it; others nearby only observe;
///  - ownership is sticky; it never moves while the owner is fighting there (except on disconnect);
///  - when the owner leaves, a remaining observer takes over (host preferred), or the spawner goes dormant;
///  - a calm spawner that the host is also near migrates to the host (1 hop to everyone in a star).
/// Every ownership change bumps the epoch, so stale messages from a former owner can be ignored.
/// </summary>
public sealed class LeaseRegistry
{
    public const byte NoOwner = 255;

    public double MigrateToHostAfter = 3.0;
    public byte HostId = 0;

    public sealed class Lease
    {
        public string Key = "";
        public byte Owner = NoOwner;
        public uint Epoch;
        public readonly HashSet<byte> Subscribers = new();
        public bool InCombat;
        public double CalmSince;

        public override string ToString() =>
            $"{Key}: owner={Owner} epoch={Epoch} subs=[{string.Join(",", Subscribers)}] combat={InCombat}";
    }

    private readonly Dictionary<string, Lease> _leases = new();

    /// <summary>The lease whose owner changed (NoOwner = dormant) and why.</summary>
    public event Action<Lease, string> OwnerChanged;

    public IReadOnlyDictionary<string, Lease> All => _leases;

    public Lease Get(string key) => _leases.TryGetValue(key, out var lease) ? lease : null;

    public byte OwnerOf(string key) => Get(key)?.Owner ?? NoOwner;

    public void Subscribe(byte player, string key, double now)
    {
        var lease = GetOrCreate(key, now);
        lease.Subscribers.Add(player);
        if (lease.Owner == NoOwner) SetOwner(lease, player, now, "first nearby");
    }

    /// <param name="leftPlace">The owner left the scene (or dungeon instance): its mobs are gone there, so even a fight
    /// moves to someone still nearby (who takes the mobs over), as on a disconnect.</param>
    public void Unsubscribe(byte player, string key, double now, bool leftPlace = false)
    {
        var lease = Get(key);
        if (lease == null) return;
        lease.Subscribers.Remove(player);
        if (lease.Owner != player) return;
        bool fightFollowsOwner = lease.InCombat && !leftPlace;
        if (fightFollowsOwner) return;
        lease.InCombat = false;
        Reassign(lease, now, leftPlace ? "owner left the area" : "owner left");
    }

    /// <summary>Owner reports whether anything from this spawner is fighting.</summary>
    public void SetCombat(byte player, string key, bool inCombat, double now)
    {
        var lease = Get(key);
        if (lease == null || lease.Owner != player) return;
        if (lease.InCombat && !inCombat) lease.CalmSince = now;
        lease.InCombat = inCombat;
    }

    public void PlayerLeft(byte player, double now)
    {
        foreach (var lease in _leases.Values)
        {
            lease.Subscribers.Remove(player);
            if (lease.Owner != player) continue;
            lease.InCombat = false;
            Reassign(lease, now, "owner disconnected");
        }
    }

    public void Tick(double now)
    {
        foreach (var lease in _leases.Values)
        {
            if (lease.Owner == NoOwner || lease.InCombat) continue;
            if (!lease.Subscribers.Contains(lease.Owner)) Reassign(lease, now, "owner left after combat");
            else if (ShouldMigrateToHost(lease, now)) SetOwner(lease, HostId, now, "migrated to host");
        }
    }

    private Lease GetOrCreate(string key, double now)
    {
        if (_leases.TryGetValue(key, out var lease)) return lease;
        lease = new Lease { Key = key, CalmSince = now };
        _leases[key] = lease;
        return lease;
    }

    private bool ShouldMigrateToHost(Lease lease, double now) =>
        lease.Owner != HostId
        && lease.Subscribers.Contains(HostId)
        && now - lease.CalmSince >= MigrateToHostAfter;

    private void Reassign(Lease lease, double now, string reason)
    {
        byte next = NextOwner(lease);
        SetOwner(lease, next, now, next == NoOwner ? "dormant" : reason);
    }

    private byte NextOwner(Lease lease)
    {
        if (lease.Subscribers.Contains(HostId)) return HostId;
        return lease.Subscribers.Count > 0 ? lease.Subscribers.Min() : NoOwner;
    }

    private void SetOwner(Lease lease, byte owner, double now, string reason)
    {
        if (lease.Owner == owner) return;
        lease.Owner = owner;
        lease.Epoch++;
        lease.InCombat = false;
        lease.CalmSince = now;
        OwnerChanged?.Invoke(lease, reason);
    }
}
