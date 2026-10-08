using System.Runtime.Versioning;
using static DepotVault.Core.Linking.Linux.LinuxNative;

namespace DepotVault.Core.Linking.Linux;

[SupportedOSPlatform("linux")]
public sealed class LinuxLinkStrategy : ILinkStrategy
{
    public int DefaultMaxHardlinks => 65000;

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

    public bool TryReflink(string source, string target)
    {
        var created = false;
        try
        {
            using var src = File.OpenHandle(source, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            using var dst = File.OpenHandle(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            created = true;
            if (Ioctl((int)dst.DangerousGetHandle(), FICLONE, (int)src.DangerousGetHandle()) != 0)
            {
                dst.Dispose();
                File.Delete(target);
                return false;
            }
            created = false;
            dst.Dispose();
            File.SetUnixFileMode(target, File.GetUnixFileMode(source));
            File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(source));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (created)
            {
                try { File.Delete(target); }
                catch (IOException) { }
            }
            return false;
        }
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

    public ulong GetVolumeId(string path)
    {
        var st = Stat(path);
        return (ulong)st.DevMajor << 32 | st.DevMinor;
    }

    public FileIdentity GetFileIdentity(string path)
    {
        var st = Stat(path, followLinks: false);
        return new FileIdentity((ulong)st.DevMajor << 32 | st.DevMinor, st.Ino, st.NLink);
    }
}
