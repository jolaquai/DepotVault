using System.Runtime.Versioning;
using static DepotVault.Core.Linking.Mac.MacNative;

namespace DepotVault.Core.Linking.Mac;

[SupportedOSPlatform("macos")]
public sealed class MacLinkStrategy : ILinkStrategy
{
    public int DefaultMaxHardlinks => 32000;

    public bool TryReflink(string source, string target)
    {
        if (CloneFile(source, target, CLONE_NOFOLLOW) != 0)
            return false;
        try
        {
            File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(source));
        }
        catch (IOException) { }
        return true;
    }

    public bool TryHardlink(string source, string target) => Link(source, target) == 0;

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

    public void CreateDirectoryLink(string linkPath, string targetDir)
    {
        if (Directory.Exists(linkPath))
        {
            if (Directory.EnumerateFileSystemEntries(linkPath).Any())
                throw new IOException($"Link location {linkPath} is not empty.");
            Directory.Delete(linkPath);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(linkPath)));
        Directory.CreateSymbolicLink(linkPath, Path.GetFullPath(targetDir));
    }

    public string GetDirectoryLinkTarget(string path)
    {
        var info = new DirectoryInfo(path);
        if (info.LinkTarget is not { } target)
            return null;
        return Path.GetFullPath(target, Path.GetDirectoryName(Path.GetFullPath(path))).TrimEnd('/');
    }

    public ulong GetVolumeId(string path) => Stat(path).Dev;

    public FileIdentity GetFileIdentity(string path)
    {
        var st = Stat(path, followLinks: false);
        return new FileIdentity(st.Dev, st.Ino, st.NLink);
    }
}
