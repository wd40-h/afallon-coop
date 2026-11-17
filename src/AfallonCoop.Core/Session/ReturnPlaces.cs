using AfallonCoop.Protocol;

namespace AfallonCoop.Session;

internal readonly record struct Place(int Scene, float X, float Y, float Z)
{
    public void Write(NetWriter writer)
    {
        writer.I32(Scene);
        writer.F32(X);
        writer.F32(Y);
        writer.F32(Z);
    }
}

/// <summary>Host side: where each player last stood, kept by identity so a reconnecting player can be sent back there.</summary>
internal sealed class ReturnPlaces
{
    private readonly Dictionary<byte, Place> _lastByPlayer = new();
    private readonly Dictionary<string, Place> _savedByKey = new();

    public void Observe(byte player, PlayerStateMsg state)
    {
        if (state.InWorld) _lastByPlayer[player] = new Place(state.SceneHash, state.PosX, state.PosY, state.PosZ);
    }

    public void SaveForReturn(byte player, string key)
    {
        if (_lastByPlayer.Remove(player, out var place)) _savedByKey[key] = place;
    }

    public bool TryGet(string key, out Place place) => _savedByKey.TryGetValue(key, out place);
}
