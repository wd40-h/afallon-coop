namespace AfallonCoop.Protocol;

[Flags]
public enum StateFlags : byte
{
    None = 0,
    Armed = 1,
    InMenu = 2,
    Dead = 4,
    /// <summary>In stealth: allies still see them (translucent), enemies do not.</summary>
    Stealth = 8,
}

public enum AnimParamType : byte { Float = 1, Int = 3, Bool = 4 }

public struct AnimParam
{
    public int NameHash;
    public AnimParamType Type;
    /// <summary>Ints and bools are carried as float.</summary>
    public float Value;

    public AnimParam(int hash, AnimParamType type, float value)
    {
        NameHash = hash;
        Type = type;
        Value = value;
    }
}

public struct AnimLayer
{
    public int StateHash;
    public float NormalizedTime;
    /// <summary>Layer weight: the game drives combat layers (AllBody/UpperBody) from scripts, not from the controller.</summary>
    public float Weight;

    public AnimLayer(int stateHash, float normalizedTime, float weight = 1f)
    {
        StateHash = stateHash;
        NormalizedTime = normalizedTime;
        Weight = weight;
    }
}

/// <summary>A vitality stat of a player's character (game stat id, current, max).</summary>
public readonly record struct Vital(int Stat, float Current, float Max);

/// <summary>One movement + animation sample of a player character.</summary>
public sealed class PlayerStateMsg
{
    private const int MaxListCount = byte.MaxValue;

    public byte Id;
    public uint Seq;
    /// <summary>Seconds, on the sender's local clock.</summary>
    public double SenderTime;
    public int SceneHash;
    public float PosX, PosY, PosZ;
    /// <summary>Yaw in degrees.</summary>
    public float RotY;
    public float VelX, VelY, VelZ;
    public bool InWorld;
    public StateFlags Flags;
    /// <summary>StableHash of the animator controller name (changes with weapon type, armed/rest, forms).</summary>
    public int ControllerHash;
    /// <summary>Params holds every parameter (otherwise only recent changes, see <see cref="AnimDeltaEncoder"/>).</summary>
    public bool AnimKeyframe;
    public List<AnimParam> Params = new();
    public List<AnimLayer> Layers = new();
    /// <summary>Model parts the owner's game switched on by its own rules (stealth aura etc.): StableHash of their path.</summary>
    public List<int> ActiveParts = new();
    /// <summary>Health, mana and other vitality stats (sent when they change and once a second; empty = unchanged).</summary>
    public List<Vital> Vitals = new();

    public void Write(NetWriter writer)
    {
        writer.U8((byte)MsgType.PlayerState);
        WriteMotion(writer);
        writer.I32(ControllerHash);
        writer.Bool(AnimKeyframe);
        WriteList(writer, Params, WriteParam);
        WriteList(writer, Layers, WriteLayer);
        WriteList(writer, ActiveParts, (w, hash) => w.I32(hash));
        WriteList(writer, Vitals, WriteVital);
    }

    public static PlayerStateMsg Read(ref NetReader reader)
    {
        var message = ReadMotion(ref reader);
        message.ControllerHash = reader.I32();
        message.AnimKeyframe = reader.Bool();
        int paramCount = reader.U8();
        for (int i = 0; i < paramCount; i++)
            message.Params.Add(new AnimParam(reader.I32(), (AnimParamType)reader.U8(), reader.F32()));
        int layerCount = reader.U8();
        for (int i = 0; i < layerCount; i++)
            message.Layers.Add(new AnimLayer(reader.I32(), reader.F32(), reader.F32()));
        int partCount = reader.U8();
        for (int i = 0; i < partCount; i++) message.ActiveParts.Add(reader.I32());
        int vitalCount = reader.U8();
        for (int i = 0; i < vitalCount; i++) message.Vitals.Add(new Vital(reader.I32(), reader.F32(), reader.F32()));
        return message;
    }

    private void WriteMotion(NetWriter writer)
    {
        writer.U8(Id);
        writer.U32(Seq);
        writer.F64(SenderTime);
        writer.I32(SceneHash);
        writer.F32(PosX);
        writer.F32(PosY);
        writer.F32(PosZ);
        writer.F32(RotY);
        writer.F32(VelX);
        writer.F32(VelY);
        writer.F32(VelZ);
        writer.Bool(InWorld);
        writer.U8((byte)Flags);
    }

    private static PlayerStateMsg ReadMotion(ref NetReader reader) => new()
    {
        Id = reader.U8(),
        Seq = reader.U32(),
        SenderTime = reader.F64(),
        SceneHash = reader.I32(),
        PosX = reader.F32(),
        PosY = reader.F32(),
        PosZ = reader.F32(),
        RotY = reader.F32(),
        VelX = reader.F32(),
        VelY = reader.F32(),
        VelZ = reader.F32(),
        InWorld = reader.Bool(),
        Flags = (StateFlags)reader.U8(),
    };

    private static void WriteList<T>(NetWriter writer, List<T> items, Action<NetWriter, T> writeItem)
    {
        int count = Math.Min(items.Count, MaxListCount);
        writer.U8((byte)count);
        for (int i = 0; i < count; i++) writeItem(writer, items[i]);
    }

    private static void WriteParam(NetWriter writer, AnimParam param)
    {
        writer.I32(param.NameHash);
        writer.U8((byte)param.Type);
        writer.F32(param.Value);
    }

    private static void WriteLayer(NetWriter writer, AnimLayer layer)
    {
        writer.I32(layer.StateHash);
        writer.F32(layer.NormalizedTime);
        writer.F32(layer.Weight);
    }

    private static void WriteVital(NetWriter writer, Vital vital)
    {
        writer.I32(vital.Stat);
        writer.F32(vital.Current);
        writer.F32(vital.Max);
    }
}
