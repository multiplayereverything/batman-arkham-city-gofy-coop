#nullable enable
// Little-endian binary encoding shared by the mod, the link helper and the lab.
using System;
using System.IO;
using System.Text;

namespace BatCoop.Core;

public sealed class WireException(string message) : Exception(message);

public sealed class WireWriter
{
    readonly MemoryStream _ms = new();

    public WireWriter U8(byte v) { _ms.WriteByte(v); return this; }
    public WireWriter Bool(bool v) => U8(v ? (byte)1 : (byte)0);

    public WireWriter U16(ushort v)
    {
        Span<byte> b = stackalloc byte[2];
        BitConverter.TryWriteBytes(b, v);
        _ms.Write(b);
        return this;
    }

    public WireWriter U32(uint v)
    {
        Span<byte> b = stackalloc byte[4];
        BitConverter.TryWriteBytes(b, v);
        _ms.Write(b);
        return this;
    }

    public WireWriter U64(ulong v)
    {
        Span<byte> b = stackalloc byte[8];
        BitConverter.TryWriteBytes(b, v);
        _ms.Write(b);
        return this;
    }

    public WireWriter F32(float v)
    {
        Span<byte> b = stackalloc byte[4];
        BitConverter.TryWriteBytes(b, v);
        _ms.Write(b);
        return this;
    }

    public WireWriter Str(string? s)
    {
        var bytes = Encoding.UTF8.GetBytes(s ?? "");
        if (bytes.Length > ushort.MaxValue) throw new WireException("string too long");
        U16((ushort)bytes.Length);
        _ms.Write(bytes);
        return this;
    }

    public WireWriter Bytes(ReadOnlySpan<byte> data)
    {
        U32((uint)data.Length);
        _ms.Write(data);
        return this;
    }

    public byte[] ToArray() => _ms.ToArray();
}

public ref struct WireReader
{
    readonly ReadOnlySpan<byte> _data;
    int _pos;

    public WireReader(ReadOnlySpan<byte> data) { _data = data; _pos = 0; }

    public int Remaining => _data.Length - _pos;

    ReadOnlySpan<byte> Take(int n)
    {
        if (n < 0 || _pos + n > _data.Length) throw new WireException($"read past end ({n} at {_pos}/{_data.Length})");
        var s = _data.Slice(_pos, n);
        _pos += n;
        return s;
    }

    public byte U8() => Take(1)[0];
    public bool Bool() => U8() != 0;
    public ushort U16() => BitConverter.ToUInt16(Take(2));
    public uint U32() => BitConverter.ToUInt32(Take(4));
    public ulong U64() => BitConverter.ToUInt64(Take(8));
    public float F32() => BitConverter.ToSingle(Take(4));
    public string Str() => Encoding.UTF8.GetString(Take(U16()));
    public byte[] Bytes() => Take((int)U32()).ToArray();
}
