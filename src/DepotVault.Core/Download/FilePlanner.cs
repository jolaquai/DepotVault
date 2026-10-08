using SteamKit2;

namespace DepotVault.Core.Download;

public enum FileAction
{
    Directory,
    Symlink,
    Empty,
    Share,
    Diff,
    Fetch,
}

public readonly record struct ContentRef(string VersionId, string RelPath, string FullPath);

public interface IContentIndex
{
    bool TryFindSource(Sha1Hash hash, ulong size, string targetVersionId, out ContentRef source);
}

public sealed class NullContentIndex : IContentIndex
{
    public static readonly NullContentIndex Instance = new();

    public bool TryFindSource(Sha1Hash hash, ulong size, string targetVersionId, out ContentRef source)
    {
        source = default;
        return false;
    }
}

public readonly record struct LocalChunk(ulong Offset, uint Length, uint Checksum);

public sealed class FilePlan
{
    public required DepotManifest.FileData File { get; init; }
    public required string RelPath { get; init; }
    public FileAction Action { get; set; }
    public ContentRef ShareSource { get; set; }
    public string DiffSourcePath { get; set; }
    public Dictionary<Sha1Hash, LocalChunk> DiffChunks { get; set; }
    public Sha1Hash Hash => File.FileHash is { Length: 20 } h ? new Sha1Hash(h) : default;
    public ulong Size => File.TotalSize;
}

public sealed class PreviousVersion
{
    public required string VersionId { get; init; }
    public required string Directory { get; init; }
    public required DepotManifest Manifest { get; init; }
}

public sealed class PlannerOptions
{
    public IContentIndex Index { get; init; } = NullContentIndex.Instance;
    public PreviousVersion Previous { get; init; }
    public string TargetVersionId { get; init; }
    public Func<string, bool> NeverShare { get; init; }
}

public static class FilePlanner
{
    public static string NormalizeRelPath(string fileName)
    {
        var rel = fileName.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(rel))
            throw new InvalidDataException($"Rooted path in manifest: {fileName}");
        foreach (var seg in rel.AsSpan().Split(Path.DirectorySeparatorChar))
        {
            if (rel.AsSpan()[seg] is "..")
                throw new InvalidDataException($"Path traversal in manifest: {fileName}");
        }
        return rel;
    }

    public static List<FilePlan> Plan(DepotManifest manifest, PlannerOptions options)
    {
        Dictionary<string, DepotManifest.FileData> previous = null;
        if (options.Previous is { } prev)
        {
            previous = new Dictionary<string, DepotManifest.FileData>(prev.Manifest.Files.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var f in prev.Manifest.Files)
            {
                if ((f.Flags & EDepotFileFlag.Directory) == 0)
                    previous[NormalizeRelPath(f.FileName)] = f;
            }
        }

        var plans = new List<FilePlan>(manifest.Files.Count);
        foreach (var f in manifest.Files)
        {
            var plan = new FilePlan { File = f, RelPath = NormalizeRelPath(f.FileName) };
            plans.Add(plan);
            if ((f.Flags & EDepotFileFlag.Directory) != 0)
            {
                plan.Action = FileAction.Directory;
                continue;
            }
            if ((f.Flags & EDepotFileFlag.Symlink) != 0 && !string.IsNullOrEmpty(f.LinkTarget))
            {
                plan.Action = FileAction.Symlink;
                continue;
            }
            if (f.TotalSize == 0)
            {
                plan.Action = FileAction.Empty;
                continue;
            }
            var neverShare = options.NeverShare?.Invoke(plan.RelPath) == true;
            if (!neverShare && options.Index.TryFindSource(plan.Hash, f.TotalSize, options.TargetVersionId, out var src))
            {
                plan.Action = FileAction.Share;
                plan.ShareSource = src;
                continue;
            }
            plan.Action = FileAction.Fetch;
            if (previous is not null && previous.TryGetValue(plan.RelPath, out var old))
            {
                var oldPath = Path.Combine(options.Previous.Directory, plan.RelPath);
                if (File.Exists(oldPath) && new FileInfo(oldPath).Length == (long)old.TotalSize)
                {
                    Dictionary<Sha1Hash, LocalChunk> chunks = null;
                    var oldChunks = new Dictionary<Sha1Hash, LocalChunk>(old.Chunks.Count);
                    foreach (var c in old.Chunks)
                        oldChunks.TryAdd(new Sha1Hash(c.ChunkID), new LocalChunk(c.Offset, c.UncompressedLength, c.Checksum));
                    foreach (var c in f.Chunks)
                    {
                        if (oldChunks.TryGetValue(new Sha1Hash(c.ChunkID), out var lc))
                            (chunks ??= [])[new Sha1Hash(c.ChunkID)] = lc;
                    }
                    if (chunks is not null)
                    {
                        plan.Action = FileAction.Diff;
                        plan.DiffSourcePath = oldPath;
                        plan.DiffChunks = chunks;
                    }
                }
            }
        }
        return plans;
    }
}
