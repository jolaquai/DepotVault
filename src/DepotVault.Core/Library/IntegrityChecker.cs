using System.IO.Enumeration;

namespace DepotVault.Core.Library;

public enum IssueKind
{
    Missing,
    Changed,
}

public sealed record IntegrityIssue(string VersionId, string RelPath, IssueKind Kind);

public sealed class IntegrityChecker(LibraryIndex library)
{
    private readonly record struct DiskEntry(long Length, DateTime LastWriteUtc);

    public List<IntegrityIssue> Scan(VersionRecord version)
    {
        var issues = new List<IntegrityIssue>();
        var dir = library.GetVersionDir(version);
        var state = VersionStateStore.Load(library.Paths.VersionStateFile(version.Id));
        if (state.Files.Count == 0)
            return issues;
        var disk = new Dictionary<string, DiskEntry>(state.Files.Count, StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(dir))
        {
            var root = dir.Length;
            var enumerable = new FileSystemEnumerable<(string Rel, DiskEntry Entry)>(dir,
                (ref FileSystemEntry e) => (Path.Join(e.Directory[root..].TrimStart(Path.DirectorySeparatorChar), e.FileName), new DiskEntry(e.Length, e.LastWriteTimeUtc.UtcDateTime)),
                new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true })
            {
                ShouldIncludePredicate = (ref FileSystemEntry e) => !e.IsDirectory,
            };
            foreach (var (rel, entry) in enumerable)
                disk[rel] = entry;
        }
        foreach (var f in state.Files)
        {
            if (!disk.TryGetValue(f.RelPath, out var d))
            {
                issues.Add(new IntegrityIssue(version.Id, f.RelPath, IssueKind.Missing));
                continue;
            }
            // NTFS directory entries of other hardlink names keep stale size/mtime.
            if (OperatingSystem.IsWindows())
            {
                var info = new FileInfo(Path.Combine(dir, f.RelPath));
                d = new DiskEntry(info.Length, info.LastWriteTimeUtc);
            }
            if (d.Length != f.Size || d.LastWriteUtc != f.LastWriteUtc)
                issues.Add(new IntegrityIssue(version.Id, f.RelPath, IssueKind.Changed));
        }
        return issues;
    }

    public List<IntegrityIssue> ScanApp(uint appId)
    {
        var all = new List<IntegrityIssue>();
        foreach (var v in library.VersionsFor(appId))
            all.AddRange(Scan(v));
        return all;
    }
}
