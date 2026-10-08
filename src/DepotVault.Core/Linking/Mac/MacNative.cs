using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DepotVault.Core.Linking.Mac;

[SupportedOSPlatform("macos")]
internal static unsafe partial class MacNative
{
    private const string LibSystem = "/usr/lib/libSystem.dylib";
    internal const uint CLONE_NOFOLLOW = 1;

    [LibraryImport(LibSystem, EntryPoint = "clonefile", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int CloneFile(string source, string target, uint flags);

    [LibraryImport(LibSystem, EntryPoint = "link", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int Link(string oldPath, string newPath);

    [LibraryImport(LibSystem, EntryPoint = "stat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int StatArm64(string path, byte* buffer);

    [LibraryImport(LibSystem, EntryPoint = "lstat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int LStatArm64(string path, byte* buffer);

    [LibraryImport(LibSystem, EntryPoint = "stat$INODE64", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int StatX64(string path, byte* buffer);

    [LibraryImport(LibSystem, EntryPoint = "lstat$INODE64", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int LStatX64(string path, byte* buffer);

    internal readonly record struct StatResult(uint Dev, ulong Ino, uint NLink);

    internal static StatResult Stat(string path, bool followLinks = true)
    {
        Span<byte> buf = stackalloc byte[256];
        int rc;
        fixed (byte* p = buf)
        {
            rc = RuntimeInformation.ProcessArchitecture == Architecture.X64
                ? followLinks ? StatX64(path, p) : LStatX64(path, p)
                : followLinks ? StatArm64(path, p) : LStatArm64(path, p);
        }
        if (rc != 0)
            throw new IOException($"stat({path}) failed: {Marshal.GetLastPInvokeErrorMessage()}");
        return new StatResult(
            BinaryPrimitives.ReadUInt32LittleEndian(buf),
            BinaryPrimitives.ReadUInt64LittleEndian(buf[8..]),
            BinaryPrimitives.ReadUInt16LittleEndian(buf[6..]));
    }
}
