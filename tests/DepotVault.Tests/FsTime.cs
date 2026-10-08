namespace DepotVault.Tests;

public static class FsTime
{
    public static async Task WaitUntilNewerAsync(string path, CancellationToken ct)
    {
        var before = File.GetLastWriteTimeUtc(path);
        var probe = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(path))), $".mtime-probe-{Guid.NewGuid():N}");
        try
        {
            while (true)
            {
                File.WriteAllBytes(probe, []);
                if (File.GetLastWriteTimeUtc(probe) > before)
                    return;
                await Task.Delay(50, ct);
            }
        }
        finally
        {
            File.Delete(probe);
        }
    }
}
