namespace DepotVault.Core;

public static class FileUtil
{
    public static void ForceDelete(string path)
    {
        if (!File.Exists(path))
            return;
        if (OperatingSystem.IsWindows())
        {
            var attrs = File.GetAttributes(path);
            if ((attrs & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
        }
        File.Delete(path);
    }

    public static bool IsReadOnly(string path)
    {
        if (OperatingSystem.IsWindows())
            return (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0;
        return (File.GetUnixFileMode(path) & (UnixFileMode.UserWrite | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) == 0;
    }

    public static void SetReadOnly(string path, bool readOnly)
    {
        if (OperatingSystem.IsWindows())
        {
            var attrs = File.GetAttributes(path);
            var next = readOnly ? attrs | FileAttributes.ReadOnly : attrs & ~FileAttributes.ReadOnly;
            if (next != attrs)
                File.SetAttributes(path, next);
            return;
        }
        var mode = File.GetUnixFileMode(path);
        var nextMode = readOnly ? mode & ~(UnixFileMode.UserWrite | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite) : mode | UnixFileMode.UserWrite;
        if (nextMode != mode)
            File.SetUnixFileMode(path, nextMode);
    }
}
