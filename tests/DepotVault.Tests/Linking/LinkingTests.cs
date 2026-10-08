using DepotVault.Core.Library;
using DepotVault.Core.Linking;

namespace DepotVault.Tests.Linking;

public class LinkingTests
{
    private static readonly ILinkStrategy Strategy = LinkStrategy.CreateForCurrentPlatform();

    [Fact]
    public void HardlinkSharesIdentity()
    {
        using var dir = new TempDir();
        var src = dir.Combine("a.txt");
        File.WriteAllText(src, "data");
        Assert.True(Strategy.TryHardlink(src, dir.Combine("b.txt")));
        var a = Strategy.GetFileIdentity(src);
        var b = Strategy.GetFileIdentity(dir.Combine("b.txt"));
        Assert.Equal(a.FileId, b.FileId);
        Assert.Equal(a.VolumeId, b.VolumeId);
        Assert.Equal(2u, b.LinkCount);
        Assert.Equal("data", File.ReadAllText(dir.Combine("b.txt")));
    }

    [Fact]
    public void DirectoryLinkRoundTrip()
    {
        using var dir = new TempDir();
        var target = dir.Combine("target");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "f.txt"), "x");
        var link = dir.Combine("link");
        Strategy.CreateDirectoryLink(link, target);
        Assert.True(LinkStrategy.IsDirectoryLink(link));
        Assert.Equal(Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar), Strategy.GetDirectoryLinkTarget(link), ignoreCase: OperatingSystem.IsWindows());
        Assert.Equal("x", File.ReadAllText(Path.Combine(link, "f.txt")));
        LinkStrategy.RemoveDirectoryLink(link);
        Assert.False(Directory.Exists(link));
        Assert.True(File.Exists(Path.Combine(target, "f.txt")));
    }

    [Fact]
    public void ProbeIsCachedPerVolume()
    {
        using var dir = new TempDir();
        var caps = new CapabilityCache(Strategy);
        var a = caps.Get(dir.Combine("x"));
        var b = caps.Get(dir.Combine("y"));
        Assert.Same(a, b);
        Assert.True(a.Hardlink);
        Assert.True(a.DirectoryLink);
        Assert.Empty(Directory.EnumerateDirectories(dir.Combine("x"), ".dvprobe-*"));
    }

    [Fact]
    public void ReflinkIsIndependentCopy()
    {
        using var dir = new TempDir();
        var caps = new CapabilityCache(Strategy).Get(dir.Path);
        if (Environment.GetEnvironmentVariable("DV_EXPECT_REFLINK") == "1")
            Assert.True(caps.Reflink, $"Expected reflink support at {dir.Path}");
        Assert.SkipUnless(caps.Reflink, "Volume does not support reflinks");
        var src = dir.Combine("a.bin");
        File.WriteAllBytes(src, Enumerable.Range(0, 100000).Select(i => (byte)i).ToArray());
        Assert.True(Strategy.TryReflink(src, dir.Combine("b.bin")));
        Assert.Equal(File.ReadAllBytes(src), File.ReadAllBytes(dir.Combine("b.bin")));
        File.WriteAllBytes(dir.Combine("b.bin"), [1, 2, 3]);
        Assert.Equal(100000, new FileInfo(src).Length);
    }

    [Fact]
    public void LinkerFallsBackInOrder()
    {
        using var dir = new TempDir();
        var linker = new Linker(new CapabilityCache(Strategy));
        var caps = linker.Capabilities.Get(dir.Path);
        var src = dir.Combine("a.bin");
        File.WriteAllText(src, "payload");

        var kind = linker.Link(src, dir.Combine("1.bin"), new LinkRules());
        Assert.Equal(caps.Reflink ? LinkKind.Reflink : LinkKind.Hardlink, kind);

        var isolated = linker.Link(src, dir.Combine("2.bin"), new LinkRules(AllowHardlink: false));
        Assert.Equal(caps.Reflink ? LinkKind.Reflink : LinkKind.None, isolated);

        var copied = linker.Link(src, dir.Combine("3.bin"), new LinkRules(AllowHardlink: false, AllowCopy: true));
        Assert.Equal(caps.Reflink ? LinkKind.Reflink : LinkKind.Copy, copied);
        Assert.Equal("payload", File.ReadAllText(dir.Combine("3.bin")));
    }
}
