using AfallonCoop.Protocol;
using AfallonCoop.World;

namespace AfallonCoop.Session;

/// <summary>The groups as the host last announced them; the same list on every machine.</summary>
internal sealed class GroupDirectory
{
    private readonly List<GroupInfo> _groups = new();

    public IReadOnlyList<GroupInfo> All => _groups;

    public GroupInfo GroupOf(byte player) => _groups.FirstOrDefault(g => g.Member(player) != null);

    public bool AreInSameGroup(byte first, byte second)
    {
        var group = GroupOf(first);
        return group != null && group.Member(second) != null;
    }

    public void ReplaceFrom(ref NetReader reader)
    {
        _groups.Clear();
        int groupCount = reader.U8();
        for (int i = 0; i < groupCount; i++) _groups.Add(ReadGroup(ref reader));
    }

    public static void Write(NetWriter writer, IReadOnlyList<GroupRegistry.Group> groups)
    {
        writer.U8((byte)groups.Count);
        foreach (var group in groups) WriteGroup(writer, group);
    }

    private static void WriteGroup(NetWriter writer, GroupRegistry.Group group)
    {
        writer.U8(group.Id);
        writer.Str(group.LeaderKey);
        writer.U8((byte)group.Members.Count);
        foreach (var member in group.Members)
        {
            writer.Str(member.Key);
            writer.Str(member.Name);
            writer.U8(member.Id);
            writer.U8((byte)Math.Clamp(member.Bots, 0, byte.MaxValue));
        }
    }

    private static GroupInfo ReadGroup(ref NetReader reader)
    {
        var group = new GroupInfo { Id = reader.U8(), LeaderKey = reader.Str() };
        int memberCount = reader.U8();
        for (int i = 0; i < memberCount; i++) group.Members.Add(ReadMember(ref reader));
        return group;
    }

    private static GroupMemberInfo ReadMember(ref NetReader reader) =>
        new() { Key = reader.Str(), Name = reader.Str(), Id = reader.U8(), Bots = reader.U8() };
}
