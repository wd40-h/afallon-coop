using AfallonCoop.Protocol;

namespace AfallonCoop.Session;

/// <summary>
/// Host side: each player's identity across reconnects. Steam id, else the install's own id; never the name,
/// which anyone can share.
/// </summary>
internal sealed class PlayerKeys
{
    private readonly Dictionary<byte, string> _keyByPlayer = new();

    public string Of(PlayerInfo player) =>
        _keyByPlayer.TryGetValue(player.Id, out var key) ? key : "player:" + player.Id;

    /// <summary>
    /// The identity kept across sessions (friends): the Steam account, or the install's id when that account
    /// is already here (two copies of one account) or unknown.
    /// </summary>
    public string Lasting(byte id, ulong steamId, string clientId, IEnumerable<PlayerInfo> present)
    {
        bool accountAlreadyHere = present.Any(p => p.Id != id && p.SteamId == steamId);
        if (steamId != 0 && !accountAlreadyHere) return "steam:" + steamId;
        return InstallOrSlotKey(id, clientId);
    }

    /// <summary>The same account or install twice at once (two copies on one PC): the second one is someone new.</summary>
    public string Assign(byte id, ulong steamId, string clientId)
    {
        string key = steamId != 0 ? "steam:" + steamId : InstallOrSlotKey(id, clientId);
        if (_keyByPlayer.Values.Contains(key)) key += "#" + id;
        _keyByPlayer[id] = key;
        return key;
    }

    public void Release(byte id) => _keyByPlayer.Remove(id);

    private static string InstallOrSlotKey(byte id, string clientId) =>
        string.IsNullOrEmpty(clientId) ? "player:" + id : "client:" + clientId;
}
