using AfallonCoop.Protocol;

namespace AfallonCoop.Session;

/// <summary>The pause rule: the host paused, or two or more players and everyone has a menu open.</summary>
internal sealed class PauseController
{
    private const int MinPlayersForMenuPause = 2;

    private readonly Dictionary<byte, bool> _menuOpenByPlayer = new();

    public bool HostRequested { get; set; }
    public bool Paused { get; private set; }
    public PauseReason Reason { get; private set; }

    public void SetMenuOpen(byte player, bool open) => _menuOpenByPlayer[player] = open;

    public void Forget(byte player) => _menuOpenByPlayer.Remove(player);

    public PauseReason Decide(IReadOnlyCollection<byte> players)
    {
        if (HostRequested) return PauseReason.Host;
        return EveryoneInMenu(players) ? PauseReason.AllInMenu : PauseReason.None;
    }

    public bool Apply(bool paused, PauseReason reason)
    {
        if (paused == Paused && reason == Reason) return false;
        Paused = paused;
        Reason = reason;
        return true;
    }

    private bool EveryoneInMenu(IReadOnlyCollection<byte> players) =>
        players.Count >= MinPlayersForMenuPause
        && players.All(id => _menuOpenByPlayer.TryGetValue(id, out var open) && open);
}
