using System.Buffers.Binary;
using System.Text;

namespace AfallonCoop.Protocol;

/// <summary>Bounds-checked reader; throws <see cref="ProtocolException"/> on malformed input.</summary>
public ref struct NetReader
{
    private readonly ReadOnlySpan<byte> _buffer;
    private int _position;

    public NetReader(ReadOnlySpan<byte> buffer)
    {
        _buffer = buffer;
        _position = 0;
    }

    public int Remaining => _buffer.Length - _position;

    public byte U8() => Consume(sizeof(byte))[0];

    public bool Bool() => U8() != 0;

    public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Consume(sizeof(ushort)));

    public int I32() => BinaryPrimitives.ReadInt32LittleEndian(Consume(sizeof(int)));

    public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Consume(sizeof(uint)));

    public ulong U64() => BinaryPrimitives.ReadUInt64LittleEndian(Consume(sizeof(ulong)));

    public long I64() => BinaryPrimitives.ReadInt64LittleEndian(Consume(sizeof(long)));

    public float F32() => BinaryPrimitives.ReadSingleLittleEndian(Consume(sizeof(float)));

    public double F64() => BinaryPrimitives.ReadDoubleLittleEndian(Consume(sizeof(double)));

    public string Str() => Encoding.UTF8.GetString(Consume(U16()));

    public byte[] Blob() => Consume(U16()).ToArray();

    private ReadOnlySpan<byte> Consume(int byteCount)
    {
        if (byteCount < 0 || _position + byteCount > _buffer.Length) throw new ProtocolException("read past end");
        var consumed = _buffer.Slice(_position, byteCount);
        _position += byteCount;
        return consumed;
    }
}
