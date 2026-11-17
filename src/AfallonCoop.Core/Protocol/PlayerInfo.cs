namespace AfallonCoop.Protocol;

/// <summary>Who a player is. Character fields are empty until they enter the world.</summary>
public sealed class PlayerInfo
{
    private const int MaxVisibleItems = 64;
    private const int MaxFxPrewarmIds = 1000;

    public byte Id;
    public string Name = "";
    public ulong SteamId;
    /// <summary>Who this player is across sessions (set by the host): their Steam account, else their install.</summary>
    public string Key = "";
    public string CharacterName = "";
    public int ClassId = -1;
    public int RaceId = -1;
    public string Gender = "";
    /// <summary>Group role the player plays (game's AdventurerRole: 0 damage, 1 tank, 2 healer).</summary>
    public byte Role;
    public int Level;
    public string Scene = "";
    /// <summary>CharacterAppearanceModifier.Serialize(): hair, face, colours...</summary>
    public string Appearance = "";
    /// <summary>Item ids of visible armor pieces.</summary>
    public List<int> Armor = new();
    /// <summary>Item ids of equipped weapons by weapon slot (0 = empty).</summary>
    public List<int> Weapons = new();
    /// <summary>Owner's renderer materials ("path=matA+matB;..."): makes the copy look exactly like the original.</summary>
    public string Materials = "";
    /// <summary>Database IDs of the abilities this character knows: others preload their visual effects.</summary>
    public List<int> FxPrewarm = new();

    /// <summary>Everything that changes how the character looks; a change means the puppet must be rebuilt.</summary>
    public string VisualSignature =>
        $"{RaceId}|{Gender}|{Appearance}|{string.Join(",", Armor)}|{string.Join(",", Weapons)}";

    public void Write(NetWriter writer)
    {
        writer.U8(Id);
        writer.Str(Name);
        writer.U64(SteamId);
        writer.Str(CharacterName);
        writer.I32(ClassId);
        writer.I32(RaceId);
        writer.Str(Gender);
        writer.I32(Level);
        writer.Str(Scene);
        writer.Str(Appearance);
        WriteIdsWithByteCount(writer, Armor);
        WriteIdsWithByteCount(writer, Weapons);
        writer.Str(Materials);
        WriteIdsWithShortCount(writer, FxPrewarm);
        writer.U8(Role);
        writer.Str(Key);
    }

    public static PlayerInfo Read(ref NetReader reader) => new()
    {
        Id = reader.U8(),
        Name = reader.Str(),
        SteamId = reader.U64(),
        CharacterName = reader.Str(),
        ClassId = reader.I32(),
        RaceId = reader.I32(),
        Gender = reader.Str(),
        Level = reader.I32(),
        Scene = reader.Str(),
        Appearance = reader.Str(),
        Armor = ReadIdsWithByteCount(ref reader),
        Weapons = ReadIdsWithByteCount(ref reader),
        Materials = reader.Str(),
        FxPrewarm = ReadIdsWithShortCount(ref reader),
        Role = reader.U8(),
        Key = reader.Str(),
    };

    public PlayerInfo Clone()
    {
        var copy = (PlayerInfo)MemberwiseClone();
        copy.Armor = new List<int>(Armor);
        copy.Weapons = new List<int>(Weapons);
        copy.FxPrewarm = new List<int>(FxPrewarm);
        return copy;
    }

    private static void WriteIdsWithByteCount(NetWriter writer, List<int> ids)
    {
        int count = Math.Min(ids?.Count ?? 0, MaxVisibleItems);
        writer.U8((byte)count);
        for (int i = 0; i < count; i++) writer.I32(ids[i]);
    }

    private static void WriteIdsWithShortCount(NetWriter writer, List<int> ids)
    {
        int count = Math.Min(ids?.Count ?? 0, MaxFxPrewarmIds);
        writer.U16((ushort)count);
        for (int i = 0; i < count; i++) writer.I32(ids[i]);
    }

    private static List<int> ReadIdsWithByteCount(ref NetReader reader) => ReadIds(ref reader, reader.U8());

    private static List<int> ReadIdsWithShortCount(ref NetReader reader) => ReadIds(ref reader, reader.U16());

    private static List<int> ReadIds(ref NetReader reader, int count)
    {
        var ids = new List<int>(count);
        for (int i = 0; i < count; i++) ids.Add(reader.I32());
        return ids;
    }
}
