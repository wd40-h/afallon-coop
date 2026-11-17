using System.Buffers.Binary;
using System.Text;

namespace AfallonCoop.Protocol;

/// <summary>Growable little-endian writer. Reuse one instance per sender and call Reset() per message.</summary>
public sealed class NetWriter
{
    private const int InitialCapacity = 256;

    private byte[] _buffer = new byte[InitialCapacity];
    private int _length;

    public int Length => _length;
    public ReadOnlySpan<byte> Span => new(_buffer, 0, _length);

    public byte[] ToArray() => Span.ToArray();

    public void Reset() => _length = 0;

    public void U8(byte value) => Reserve(sizeof(byte))[0] = value;

    public void Bool(bool value) => U8(value ? (byte)1 : (byte)0);

    public void U16(ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(Reserve(sizeof(ushort)), value);

    public void I32(int value) => BinaryPrimitives.WriteInt32LittleEndian(Reserve(sizeof(int)), value);

    public void U32(uint value) => BinaryPrimitives.WriteUInt32LittleEndian(Reserve(sizeof(uint)), value);

    public void U64(ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(Reserve(sizeof(ulong)), value);

    public void I64(long value) => BinaryPrimitives.WriteInt64LittleEndian(Reserve(sizeof(long)), value);

    public void F32(float value) => BinaryPrimitives.WriteSingleLittleEndian(Reserve(sizeof(float)), value);

    public void F64(double value) => BinaryPrimitives.WriteDoubleLittleEndian(Reserve(sizeof(double)), value);

    public void Bytes(ReadOnlySpan<byte> data) => data.CopyTo(Reserve(data.Length));

    /// <summary>Length-prefixed bytes (up to 65535), read back with <see cref="NetReader.Blob"/>.</summary>
    public void Blob(ReadOnlySpan<byte> data)
    {
        U16((ushort)data.Length);
        Bytes(data);
    }

    public void Str(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text ?? "");
        if (bytes.Length > ushort.MaxValue) throw new ArgumentException("string too long");
        U16((ushort)bytes.Length);
        bytes.CopyTo(Reserve(bytes.Length));
    }

    /// <summary>Overwrite a U16 written earlier (e.g. a count known only after the items).</summary>
    public void PatchU16(int at, ushort value)
    {
        if (at < 0 || at + sizeof(ushort) > _length) throw new ArgumentOutOfRangeException(nameof(at));
        BinaryPrimitives.WriteUInt16LittleEndian(new Span<byte>(_buffer, at, sizeof(ushort)), value);
    }

    private Span<byte> Reserve(int byteCount)
    {
        if (_length + byteCount > _buffer.Length)
            Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _length + byteCount));
        var reserved = new Span<byte>(_buffer, _length, byteCount);
        _length += byteCount;
        return reserved;
    }
}
