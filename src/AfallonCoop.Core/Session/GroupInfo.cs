using AfallonCoop.World;

namespace AfallonCoop.Session;

/// <summary>A member of a group: a player, online (with their session id) or waiting to reconnect.</summary>
public sealed class GroupMemberInfo
{
    public string Key = "";
    public string Name = "";
    public byte Id = GroupRegistry.Offline;
    public int Bots;
    public bool Online => Id != GroupRegistry.Offline;
}

/// <summary>A player group as everyone sees it (decided by the host).</summary>
public sealed class GroupInfo
{
    public byte Id;
    public string LeaderKey = "";
    /// <summary>In group order, including members who are disconnected (their place is kept).</summary>
    public readonly List<GroupMemberInfo> Members = new();
    public GroupMemberInfo LeaderMember => Members.FirstOrDefault(m => m.Key == LeaderKey);
    /// <summary>Session id of the leader, <see cref="GroupRegistry.Offline"/> while they are disconnected.</summary>
    public byte Leader => LeaderMember?.Id ?? GroupRegistry.Offline;
    public IEnumerable<byte> Players => Members.Where(m => m.Online).Select(m => m.Id);
    public GroupMemberInfo Member(byte id) => Members.FirstOrDefault(m => m.Online && m.Id == id);
    public int Size => Members.Count + Members.Sum(m => m.Bots);
}
