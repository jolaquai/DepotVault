using DepotVault.Core.Persistence;

namespace DepotVault.Core.Library;

public sealed class DepotInfo
{
    public uint DepotId { get; set; }
    public string Name { get; set; }
    public string OsList { get; set; }
    public string OsArch { get; set; }
    public string Language { get; set; }
    public bool LowViolence { get; set; }
    public uint DepotFromApp { get; set; }
    public bool SharedInstall { get; set; }
    public uint DlcAppId { get; set; }
    public ulong MaxSize { get; set; }
    public ulong CurrentManifestId { get; set; }
    public ulong CurrentManifestSize { get; set; }

    public bool IsDownloadable => DepotFromApp == 0 && !SharedInstall && CurrentManifestId != 0;
}

public sealed class ManifestHistoryEntry
{
    public uint DepotId { get; set; }
    public ulong ManifestId { get; set; }
    public DateTime DateUtc { get; set; }
    public string Label { get; set; }
    public bool Unavailable { get; set; }
}

public sealed class AppRecord : ISchemaVersioned
{
    public int SchemaVersion { get; set; }
    public uint AppId { get; set; }
    public string Name { get; set; }
    public string InstallDir { get; set; }
    public uint PublicBuildId { get; set; }
    public DateTime MetadataFetchedUtc { get; set; }
    public List<DepotInfo> Depots { get; set; } = [];
    public List<uint> SelectedDepots { get; set; }
    public List<ManifestHistoryEntry> History { get; set; } = [];
    public string Notes { get; set; }
}
