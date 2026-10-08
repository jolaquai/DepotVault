using DepotVault.Core;
using DepotVault.Core.Library;
using DepotVault.Core.Linking;
using DepotVault.Core.Persistence;

namespace DepotVault.Tests.Library;

public class ReadOnlyProtectionTests
{
    private static readonly ILinkStrategy Strategy = LinkStrategy.CreateForCurrentPlatform();

    [Fact]
    public void ProtectsSharedFilesOnlyAndIsReversible()
    {
        using var dir = new TempDir();
        var paths = new AppPaths(dir.Combine("data"));
        using var lib = new LibraryIndex(paths);
        using var apps = new AppRepository(paths);
        var a = lib.CreateVersion(10, dir.Combine("root"), [(11, 1ul)]);
        var b = lib.CreateVersion(10, dir.Combine("root"), [(11, 2ul)]);
        string P(VersionRecord v, string rel) => Path.Combine(lib.GetVersionDir(v), rel);
        foreach (var name in new[] { "shared.bin", "config.ini" })
        {
            File.WriteAllText(P(a, name), name);
            Assert.True(Strategy.TryHardlink(P(a, name), P(b, name)));
        }
        File.WriteAllText(P(b, "unique.bin"), "u");
        VersionStateStore.Save(paths.VersionStateFile(b.Id), new VersionState
        {
            Files =
            [
                new FileSnapshot { RelPath = "shared.bin", Link = LinkKind.Hardlink },
                new FileSnapshot { RelPath = "config.ini", Link = LinkKind.Hardlink },
                new FileSnapshot { RelPath = "unique.bin", Link = LinkKind.None },
            ],
        });
        apps.Get(10).SetDecision("config.ini", MutableDecision.Share);

        var enabled = false;
        var ro = new ReadOnlyProtection(lib, apps, Strategy, () => enabled);
        Assert.Equal(0, ro.Apply(b));
        enabled = true;
        Assert.Equal(1, ro.Apply(b));
        Assert.True(FileUtil.IsReadOnly(P(b, "shared.bin")));
        Assert.True(FileUtil.IsReadOnly(P(a, "shared.bin")));
        Assert.False(FileUtil.IsReadOnly(P(b, "config.ini")));
        Assert.False(FileUtil.IsReadOnly(P(b, "unique.bin")));

        lib.DeleteVersion(b.Id);
        Assert.False(Directory.Exists(lib.GetVersionDir(b)));
        Assert.Equal("shared.bin", File.ReadAllText(P(a, "shared.bin")));
        Assert.Equal(0, ro.Remove(a));
    }
}
