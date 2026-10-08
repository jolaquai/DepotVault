using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DepotVault.Core.Linking.Linux;

[SupportedOSPlatform("linux")]
internal static unsafe partial class LinuxNative
{
    internal const ulong FICLONE = 0x40049409;
    internal const int AT_FDCWD = -100;
    internal const int AT_SYMLINK_NOFOLLOW = 0x100;
    internal const uint STATX_BASIC_STATS = 0x7FF;

    [LibraryImport("libc", EntryPoint = "link", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int Link(string oldPath, string newPath);

    [LibraryImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    internal static partial int Ioctl(int fd, ulong request, int arg);

    [LibraryImport("libc", EntryPoint = "statx", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int Statx(int dirfd, string path, int flags, uint mask, byte* buffer);

    internal readonly record struct StatxResult(uint NLink, ulong Ino, ulong Size, uint DevMajor, uint DevMinor);

    internal static StatxResult Stat(string path, bool followLinks = true)
    {
        Span<byte> buf = stackalloc byte[256];
        fixed (byte* p = buf)
        {
            if (Statx(AT_FDCWD, path, followLinks ? 0 : AT_SYMLINK_NOFOLLOW, STATX_BASIC_STATS, p) != 0)
                throw new IOException($"statx({path}) failed: {Marshal.GetLastPInvokeErrorMessage()}");
        }
        return new StatxResult(
            BinaryPrimitives.ReadUInt32LittleEndian(buf[16..]),
            BinaryPrimitives.ReadUInt64LittleEndian(buf[32..]),
            BinaryPrimitives.ReadUInt64LittleEndian(buf[40..]),
            BinaryPrimitives.ReadUInt32LittleEndian(buf[136..]),
            BinaryPrimitives.ReadUInt32LittleEndian(buf[140..]));
    }
}
