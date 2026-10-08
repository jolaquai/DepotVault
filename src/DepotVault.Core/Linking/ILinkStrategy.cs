using DepotVault.Core.Linking.Linux;
using DepotVault.Core.Linking.Win;

namespace DepotVault.Core.Linking;

public readonly record struct FileIdentity(ulong VolumeId, ulong FileId, uint LinkCount);

public interface ILinkStrategy
{
    bool TryReflink(string source, string target);
    bool TryHardlink(string source, string target);
    bool TrySymlink(string source, string target);
    void CreateDirectoryLink(string linkPath, string targetDir);
    string GetDirectoryLinkTarget(string path);
    ulong GetVolumeId(string path);
    FileIdentity GetFileIdentity(string path);
    int DefaultMaxHardlinks { get; }
}

public static class LinkStrategy
{
    public static ILinkStrategy CreateForCurrentPlatform() =>
        OperatingSystem.IsWindows() ? new WindowsLinkStrategy()
        : OperatingSystem.IsLinux() ? new LinuxLinkStrategy()
        : throw new PlatformNotSupportedException();

    public static void RemoveDirectoryLink(string path)
    {
        var info = new DirectoryInfo(path);
        if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) == 0)
            throw new IOException($"{path} is not a directory link.");
        info.Delete(false);
    }

    public static bool IsDirectoryLink(string path)
    {
        var info = new DirectoryInfo(path);
        return info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0;
    }
}
