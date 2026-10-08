using System.Security.Cryptography;
using DepotVault.Core;
using DepotVault.Core.Library;
using DepotVault.Core.Linking;
using DepotVault.Core.Persistence;
using DepotVault.Tests.Download;

namespace DepotVault.Tests.Library;

public class ContentIndexTests
{
    private static readonly ILinkStrategy Strategy = LinkStrategy.CreateForCurrentPlatform();

    private static (LibraryIndex Lib, VersionRecord A, VersionRecord B, byte[] Data) Setup(TempDir dir, string rootB = "root")
    {
        var paths = new AppPaths(dir.Combine("data"));
        var lib = new LibraryIndex(paths);
        var depot = new FakeDepot();
        var data = FakeDepot.Bytes("shared-content", 10);
        var a = lib.CreateVersion(10, dir.Combine("root"), [(11, 1ul)]);
        var b = lib.CreateVersion(10, dir.Combine(rootB), [(11, 2ul)]);
        var m = depot.Build(11, 1, 64, (@"bin\x.dll", data), ("dir", null), ("empty", []));
        m.SaveToFile(paths.ManifestFile(a.Id, 11));
        lib.MarkDepotComplete(a.Id, 11, 1);
        Directory.CreateDirectory(Path.Combine(lib.GetVersionDir(a), "bin"));
        File.WriteAllBytes(Path.Combine(lib.GetVersionDir(a), "bin", "x.dll"), data);
        return (lib, a, b, data);
    }

    [Fact]
    public void FindsSourceInOtherVersionOnSameRoot()
    {
        using var dir = new TempDir();
        var (lib, a, b, data) = Setup(dir);
        using var libScope = lib;
        var index = new ContentIndex(lib, Strategy);
        index.Rebuild();
        Assert.Equal(1, index.Count);

        var hash = new Sha1Hash(SHA1.HashData(data));
        Assert.True(index.TryFindSource(hash, (ulong)data.Length, b.Id, out var src));
        Assert.Equal(a.Id, src.VersionId);
        Assert.Equal(Path.Combine("bin", "x.dll"), src.RelPath);
        Assert.False(index.TryFindSource(hash, (ulong)data.Length, a.Id, out _));
        Assert.False(index.TryFindSource(hash, (ulong)data.Length + 1, b.Id, out _));

        index.RemoveVersion(a.Id);
        Assert.Equal(0, index.Count);
        Assert.False(index.TryFindSource(hash, (ulong)data.Length, b.Id, out _));
    }

    [Fact]
    public void IgnoresOtherRootsMissingFilesAndHardlinkLimit()
    {
        using var dir = new TempDir();
        var (lib, a, b, data) = Setup(dir, rootB: "otherroot");
        using var libScope = lib;
        var hash = new Sha1Hash(SHA1.HashData(data));
        var index = new ContentIndex(lib, Strategy);
        index.Rebuild();
        Assert.False(index.TryFindSource(hash, (ulong)data.Length, b.Id, out _));

        var c = lib.CreateVersion(10, dir.Combine("root"), [(11, 3ul)]);
        var limited = new ContentIndex(lib, Strategy, _ => 1);
        limited.Rebuild();
        Assert.False(limited.TryFindSource(hash, (ulong)data.Length, c.Id, out _));

        File.Delete(Path.Combine(lib.GetVersionDir(a), "bin", "x.dll"));
        Assert.False(index.TryFindSource(hash, (ulong)data.Length, c.Id, out _));
    }
}
