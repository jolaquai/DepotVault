using System.Collections.Concurrent;
using DepotVault.Core.Library;

namespace DepotVault.Core.Linking;

public sealed record VolumeCapabilities(ulong VolumeId, bool Reflink, bool Hardlink, bool Symlink, bool DirectoryLink, int MaxHardlinks);

public sealed class CapabilityCache(ILinkStrategy strategy)
{
    private readonly ConcurrentDictionary<ulong, VolumeCapabilities> _cache = new();

    public ILinkStrategy Strategy => strategy;

    public VolumeCapabilities Get(string directory)
    {
        Directory.CreateDirectory(directory);
        var volume = strategy.GetVolumeId(directory);
        return _cache.GetOrAdd(volume, static (v, s) => Probe(s.Strategy, v, s.Dir), (Strategy: strategy, Dir: directory));
    }

    public bool SameVolume(string a, string b) => strategy.GetVolumeId(a) == strategy.GetVolumeId(b);

    private static VolumeCapabilities Probe(ILinkStrategy strategy, ulong volume, string directory)
    {
        var probe = Path.Combine(directory, $".dvprobe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(probe);
        try
        {
            var src = Path.Combine(probe, "src.bin");
            var data = new byte[70000];
            Random.Shared.NextBytes(data);
            File.WriteAllBytes(src, data);
            var reflink = strategy.TryReflink(src, Path.Combine(probe, "reflink.bin"));
            var hardlink = strategy.TryHardlink(src, Path.Combine(probe, "hard.bin"));
            var symlink = strategy.TrySymlink(src, Path.Combine(probe, "sym.bin"));
            bool dirLink;
            try
            {
                strategy.CreateDirectoryLink(Path.Combine(probe, "dirlink"), Path.Combine(probe, "target"));
                dirLink = true;
            }
            catch (IOException) { dirLink = false; }
            catch (UnauthorizedAccessException) { dirLink = false; }
            return new VolumeCapabilities(volume, reflink, hardlink, symlink, dirLink, strategy.DefaultMaxHardlinks);
        }
        finally
        {
            try
            {
                var link = Path.Combine(probe, "dirlink");
                if (LinkStrategy.IsDirectoryLink(link))
                    LinkStrategy.RemoveDirectoryLink(link);
                Directory.Delete(probe, true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}

public readonly record struct LinkRules(bool AllowHardlink = true, bool AllowSymlink = false, bool AllowCopy = false)
{
    public LinkRules() : this(true) { }
}

public sealed class Linker(CapabilityCache capabilities)
{
    public ILinkStrategy Strategy => capabilities.Strategy;
    public CapabilityCache Capabilities => capabilities;

    public LinkKind Link(string source, string target, LinkRules rules)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(target));
        Directory.CreateDirectory(dir);
        var caps = capabilities.Get(dir);
        var sameVolume = capabilities.SameVolume(source, dir);
        if (sameVolume && caps.Reflink && Strategy.TryReflink(source, target))
            return LinkKind.Reflink;
        if (sameVolume && rules.AllowHardlink && caps.Hardlink && Strategy.GetFileIdentity(source).LinkCount < caps.MaxHardlinks && Strategy.TryHardlink(source, target))
            return LinkKind.Hardlink;
        if (rules.AllowSymlink && caps.Symlink && Strategy.TrySymlink(Path.GetFullPath(source), target))
            return LinkKind.Symlink;
        if (rules.AllowCopy)
        {
            File.Copy(source, target, false);
            return LinkKind.Copy;
        }
        return LinkKind.None;
    }
}
