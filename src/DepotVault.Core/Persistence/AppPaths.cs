namespace DepotVault.Core.Persistence;

public sealed class AppPaths
{
    public AppPaths(string root)
    {
        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(Root);
    }

    public static AppPaths CreateDefault() => new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create), "DepotVault"));

    public string Root { get; }
    public string Settings => Path.Combine(Root, "settings.json");
    public string Library => Path.Combine(Root, "library.json");
    public string Queue => Path.Combine(Root, "queue.json");
    public string Auth => Path.Combine(Root, "auth.bin");
    public string AppsDir => Path.Combine(Root, "apps");
    public string VersionsDir => Path.Combine(Root, "versions");
    public string LogsDir => Path.Combine(Root, "logs");

    public string AppFile(uint appId) => Path.Combine(AppsDir, $"{appId}.json");
    public string VersionDir(string versionId) => Path.Combine(VersionsDir, versionId);
    public string ManifestFile(string versionId, uint depotId) => Path.Combine(VersionsDir, versionId, $"{depotId}.manifest.bin");
    public string VersionStateFile(string versionId) => Path.Combine(VersionsDir, versionId, "state.json");
}
