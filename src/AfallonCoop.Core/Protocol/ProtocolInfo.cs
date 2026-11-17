namespace AfallonCoop.Protocol;

public static class ProtocolInfo
{
    /// <summary>Bump on any wire-format change. Peers with a different value are rejected.</summary>
    public const ushort Version = 28;
    public const int MaxPlayers = 5;
    public const byte HostId = 0;
}
