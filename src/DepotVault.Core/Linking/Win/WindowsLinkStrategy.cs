using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using static DepotVault.Core.Linking.Win.WinNative;

namespace DepotVault.Core.Linking.Win;

[SupportedOSPlatform("windows")]
public sealed unsafe class WindowsLinkStrategy : ILinkStrategy
{
    private const long MaxCloneChunk = 1L << 30;

    public int DefaultMaxHardlinks => 1023;

    public bool TryHardlink(string source, string target) => CreateHardLink(target, source, 0);

    public bool TrySymlink(string source, string target)
    {
        try
        {
            File.CreateSymbolicLink(target, source);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public bool TryReflink(string source, string target)
    {
        var created = false;
        try
        {
            using var src = File.OpenHandle(source, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            GetIntegrityInformationBuffer integrity;
            if (!DeviceIoControl(src, FSCTL_GET_INTEGRITY_INFORMATION, null, 0, &integrity, (uint)sizeof(GetIntegrityInformationBuffer), out _, 0))
                return false;
            var cluster = Math.Max(4096u, integrity.ClusterSizeInBytes);
            var length = RandomAccess.GetLength(src);

            using var dst = File.OpenHandle(target, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            created = true;
            if ((File.GetAttributes(source) & FileAttributes.SparseFile) != 0 && !DeviceIoControl(dst, FSCTL_SET_SPARSE, null, 0, null, 0, out _, 0))
                return Fail(target);
            var set = new SetIntegrityInformationBuffer { ChecksumAlgorithm = integrity.ChecksumAlgorithm, Flags = integrity.Flags };
            if (!DeviceIoControl(dst, FSCTL_SET_INTEGRITY_INFORMATION, &set, (uint)sizeof(SetIntegrityInformationBuffer), null, 0, out _, 0))
                return Fail(target);
            RandomAccess.SetLength(dst, length);

            var aligned = (length + cluster - 1) / cluster * cluster;
            for (long offset = 0; offset < aligned; offset += MaxCloneChunk)
            {
                var data = new DuplicateExtentsData
                {
                    FileHandle = src.DangerousGetHandle(),
                    SourceFileOffset = offset,
                    TargetFileOffset = offset,
                    ByteCount = Math.Min(MaxCloneChunk, aligned - offset),
                };
                if (!DeviceIoControl(dst, FSCTL_DUPLICATE_EXTENTS_TO_FILE, &data, (uint)sizeof(DuplicateExtentsData), null, 0, out _, 0))
                    return Fail(target);
            }
            created = false;
            File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(source));
            return true;
        }
        catch (IOException)
        {
            return created ? Fail(target) : false;
        }
        catch (UnauthorizedAccessException)
        {
            return created ? Fail(target) : false;
        }
    }

    private static bool Fail(string target)
    {
        try { File.Delete(target); }
        catch (IOException) { }
        return false;
    }

    public void CreateDirectoryLink(string linkPath, string targetDir)
    {
        var full = Path.GetFullPath(targetDir).TrimEnd('\\');
        var substitute = @"\??\" + full + "\\";
        var print = full;
        var pathBytes = (substitute.Length + 1 + print.Length + 1) * 2;
        var dataLength = 8 + pathBytes;
        var total = 8 + dataLength;
        if (dataLength > ushort.MaxValue)
            throw new PathTooLongException(targetDir);

        Directory.CreateDirectory(linkPath);
        if (Directory.EnumerateFileSystemEntries(linkPath).Any())
            throw new IOException($"Junction location {linkPath} is not empty.");

        Span<byte> buf = total <= 2048 ? stackalloc byte[total] : new byte[total];
        buf.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(buf, IO_REPARSE_TAG_MOUNT_POINT);
        BinaryPrimitives.WriteUInt16LittleEndian(buf[4..], (ushort)dataLength);
        BinaryPrimitives.WriteUInt16LittleEndian(buf[8..], 0);
        BinaryPrimitives.WriteUInt16LittleEndian(buf[10..], (ushort)(substitute.Length * 2));
        BinaryPrimitives.WriteUInt16LittleEndian(buf[12..], (ushort)((substitute.Length + 1) * 2));
        BinaryPrimitives.WriteUInt16LittleEndian(buf[14..], (ushort)(print.Length * 2));
        MemoryMarshal.AsBytes(substitute.AsSpan()).CopyTo(buf[16..]);
        MemoryMarshal.AsBytes(print.AsSpan()).CopyTo(buf[(16 + (substitute.Length + 1) * 2)..]);

        using var h = CreateFile(linkPath, GENERIC_WRITE, 0, 0, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, 0);
        if (h.IsInvalid)
            throw new IOException($"Cannot open {linkPath}: {Marshal.GetLastPInvokeErrorMessage()}");
        fixed (byte* p = buf)
        {
            if (!DeviceIoControl(h, FSCTL_SET_REPARSE_POINT, p, (uint)total, null, 0, out _, 0))
            {
                var msg = Marshal.GetLastPInvokeErrorMessage();
                h.Dispose();
                Directory.Delete(linkPath);
                throw new IOException($"Cannot create junction {linkPath} -> {targetDir}: {msg}");
            }
        }
    }

    public string GetDirectoryLinkTarget(string path)
    {
        var info = new DirectoryInfo(path);
        if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) == 0)
            return null;
        var target = info.LinkTarget;
        if (target is null)
            return null;
        if (target.StartsWith(@"\??\", StringComparison.Ordinal))
            target = target[4..];
        return Path.GetFullPath(target).TrimEnd('\\');
    }

    public ulong GetVolumeId(string path)
    {
        using var h = OpenForMetadata(path);
        if (!GetVolumeInformationByHandle(h, null, 0, out var serial, out _, out _, null, 0))
            throw new IOException($"Cannot query volume of {path}: {Marshal.GetLastPInvokeErrorMessage()}");
        return serial;
    }

    public uint GetFileSystemFlags(string path)
    {
        using var h = OpenForMetadata(path);
        return GetVolumeInformationByHandle(h, null, 0, out _, out _, out var flags, null, 0) ? flags : 0;
    }

    public FileIdentity GetFileIdentity(string path)
    {
        using var h = OpenForMetadata(path);
        if (!GetFileInformationByHandle(h, out var info))
            throw new IOException($"Cannot query {path}: {Marshal.GetLastPInvokeErrorMessage()}");
        return new FileIdentity(info.VolumeSerialNumber, (ulong)info.FileIndexHigh << 32 | info.FileIndexLow, info.NumberOfLinks);
    }
}
