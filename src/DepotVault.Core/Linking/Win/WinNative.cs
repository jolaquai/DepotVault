using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace DepotVault.Core.Linking.Win;

[SupportedOSPlatform("windows")]
internal static unsafe partial class WinNative
{
    internal const uint GENERIC_READ = 0x80000000;
    internal const uint GENERIC_WRITE = 0x40000000;
    internal const uint FILE_READ_ATTRIBUTES = 0x80;
    internal const uint FILE_SHARE_ALL = 0x7;
    internal const uint OPEN_EXISTING = 3;
    internal const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    internal const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
    internal const uint FSCTL_SET_REPARSE_POINT = 0x000900A4;
    internal const uint FSCTL_SET_SPARSE = 0x000900C4;
    internal const uint FSCTL_DUPLICATE_EXTENTS_TO_FILE = 0x00098344;
    internal const uint FSCTL_GET_INTEGRITY_INFORMATION = 0x0009027C;
    internal const uint FSCTL_SET_INTEGRITY_INFORMATION = 0x0009C280;
    internal const uint IO_REPARSE_TAG_MOUNT_POINT = 0xA0000003;
    internal const uint FILE_SUPPORTS_BLOCK_REFCOUNTING = 0x08000000;
    internal const uint FILE_SUPPORTS_HARD_LINKS = 0x00400000;

    [StructLayout(LayoutKind.Sequential)]
    internal struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationLow, CreationHigh;
        public uint AccessLow, AccessHigh;
        public uint WriteLow, WriteHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DuplicateExtentsData
    {
        public nint FileHandle;
        public long SourceFileOffset;
        public long TargetFileOffset;
        public long ByteCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct GetIntegrityInformationBuffer
    {
        public ushort ChecksumAlgorithm;
        public ushort Reserved;
        public uint Flags;
        public uint ChecksumChunkSizeInBytes;
        public uint ClusterSizeInBytes;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SetIntegrityInformationBuffer
    {
        public ushort ChecksumAlgorithm;
        public ushort Reserved;
        public uint Flags;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateHardLinkW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CreateHardLink(string fileName, string existingFileName, nint securityAttributes);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial SafeFileHandle CreateFile(string fileName, uint access, uint share, nint securityAttributes, uint creationDisposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeviceIoControl(SafeFileHandle device, uint code, void* inBuffer, uint inSize, void* outBuffer, uint outSize, out uint bytesReturned, nint overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation info);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumeInformationByHandleW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetVolumeInformationByHandle(SafeFileHandle file, char* volumeName, uint volumeNameSize, out uint serial, out uint maxComponentLength, out uint fileSystemFlags, char* fileSystemName, uint fileSystemNameSize);

    internal static SafeFileHandle OpenForMetadata(string path)
    {
        var h = CreateFile(path, FILE_READ_ATTRIBUTES, FILE_SHARE_ALL, 0, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, 0);
        if (h.IsInvalid)
            throw new IOException($"Cannot open {path}: {Marshal.GetLastPInvokeErrorMessage()}", Marshal.GetHRForLastWin32Error());
        return h;
    }
}
