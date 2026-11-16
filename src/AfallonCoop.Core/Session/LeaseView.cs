using AfallonCoop.World;

namespace AfallonCoop.Session;

/// <summary>Who simulates each spawner, as the host last announced it.</summary>
internal sealed class LeaseView
{
    private readonly Dictionary<uint, (byte owner, uint epoch)> _entries = new();

    public int Count => _entries.Count;

    public IEnumerable<KeyValuePair<uint, (byte owner, uint epoch)>> Entries => _entries;

    public byte OwnerOf(uint key) => _entries.TryGetValue(key, out var entry) ? entry.owner : LeaseRegistry.NoOwner;

    public bool TryUpdate(uint key, byte owner, uint epoch)
    {
        bool isNewer = !_entries.TryGetValue(key, out var known) || known.epoch < epoch;
        if (isNewer) _entries[key] = (owner, epoch);
        return isNewer;
    }
}
