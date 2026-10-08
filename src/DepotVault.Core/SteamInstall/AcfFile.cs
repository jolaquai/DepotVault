using System.Globalization;

namespace DepotVault.Core.SteamInstall;

public readonly record struct InstalledDepot(uint DepotId, ulong ManifestId, ulong Size);

public sealed class AcfFile
{
    private AcfFile(string path, VdfDocument doc)
    {
        Path = path;
        Document = doc;
    }

    public string Path { get; }
    public VdfDocument Document { get; }

    public static AcfFile Load(string path) => new(path, VdfDocument.Load(path));
    public static AcfFile Parse(string text, string path = null) => new(path, VdfDocument.Parse(text));

    public uint AppId => U32("appid");
    public string Name => Document.GetValue("AppState", "name");
    public string InstallDir => Document.GetValue("AppState", "installdir");
    public uint BuildId => U32("buildid");
    public uint TargetBuildId => U32("TargetBuildID");
    public int StateFlags => (int)U32("StateFlags");
    public int AutoUpdateBehavior => (int)U32("AutoUpdateBehavior");

    public IReadOnlyList<InstalledDepot> InstalledDepots
    {
        get
        {
            var node = Document.Get("AppState", "InstalledDepots");
            if (node is null)
                return [];
            var list = new List<InstalledDepot>(node.Children.Count);
            foreach (var d in node.Children)
            {
                if (!uint.TryParse(d.Key, out var id))
                    continue;
                ulong.TryParse(d["manifest"]?.Value, out var manifest);
                ulong.TryParse(d["size"]?.Value, out var size);
                list.Add(new InstalledDepot(id, manifest, size));
            }
            return list;
        }
    }

    public void SetBuildId(uint buildId)
    {
        Document.Set(buildId.ToString(CultureInfo.InvariantCulture), "AppState", "buildid");
        if (Document.Get("AppState", "TargetBuildID") is not null)
            Document.Set(buildId.ToString(CultureInfo.InvariantCulture), "AppState", "TargetBuildID");
    }

    public void SetAutoUpdateBehavior(int value) => Document.Set(value.ToString(CultureInfo.InvariantCulture), "AppState", "AutoUpdateBehavior");

    public void SetInstalledDepot(uint depotId, ulong manifestId, ulong size)
    {
        var id = depotId.ToString(CultureInfo.InvariantCulture);
        Document.Set(manifestId.ToString(CultureInfo.InvariantCulture), "AppState", "InstalledDepots", id, "manifest");
        if (size != 0)
            Document.Set(size.ToString(CultureInfo.InvariantCulture), "AppState", "InstalledDepots", id, "size");
    }

    public string InstallPath(string libraryPath) => System.IO.Path.Combine(libraryPath, "steamapps", "common", InstallDir);

    public void Save() => Document.Save(Path);
    public void Save(string path) => Document.Save(path);

    private uint U32(string key) => uint.TryParse(Document.GetValue("AppState", key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
}
