using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DepotVault.Core;

public readonly struct Sha1Hash : IEquatable<Sha1Hash>
{
    private readonly ulong _a;
    private readonly ulong _b;
    private readonly uint _c;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Sha1Hash(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 20)
            throw new ArgumentException("SHA-1 needs 20 bytes.", nameof(bytes));
        _a = MemoryMarshal.Read<ulong>(bytes);
        _b = MemoryMarshal.Read<ulong>(bytes[8..]);
        _c = MemoryMarshal.Read<uint>(bytes[16..]);
    }

    public bool IsEmpty => _a == 0 && _b == 0 && _c == 0;

    public void CopyTo(Span<byte> destination)
    {
        MemoryMarshal.Write(destination, in _a);
        MemoryMarshal.Write(destination[8..], in _b);
        MemoryMarshal.Write(destination[16..], in _c);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(Sha1Hash other) => _a == other._a && _b == other._b && _c == other._c;

    public override bool Equals(object obj) => obj is Sha1Hash h && Equals(h);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override int GetHashCode() => (int)_a ^ (int)(_a >> 32) ^ (int)_b ^ (int)_c;

    public override string ToString()
    {
        Span<byte> b = stackalloc byte[20];
        CopyTo(b);
        return Convert.ToHexStringLower(b);
    }

    public static Sha1Hash Parse(string hex) => new(Convert.FromHexString(hex));

    public static bool operator ==(Sha1Hash l, Sha1Hash r) => l.Equals(r);
    public static bool operator !=(Sha1Hash l, Sha1Hash r) => !l.Equals(r);
}
