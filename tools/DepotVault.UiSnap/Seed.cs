using DepotVault.Core.Library;
using DepotVault.Core.Persistence;

internal static class Seed
{
    public static void Run(AppPaths paths)
    {
        if (File.Exists(paths.Library))
            return;
        using var apps = new AppRepository(paths);
        using var lib = new LibraryIndex(paths);
        var root = Path.Combine(paths.Root, "vault");
        var app = apps.Get(480000);
        app.Name = "Sample Game";
        app.InstallDir = "Sample Game";
        app.PublicBuildId = 9876543;
        app.MetadataFetchedUtc = DateTime.UtcNow;
        app.Depots =
        [
            new DepotInfo { DepotId = 480001, Name = "Sample Game Content", CurrentManifestId = 1234567890123456789, CurrentManifestSize = 4294967296 },
            new DepotInfo { DepotId = 480002, Name = "Sample Game Windows Binaries", OsList = "windows", CurrentManifestId = 2345678901234567890 },
            new DepotInfo { DepotId = 480003, Name = "Sample Game Linux Binaries", OsList = "linux", CurrentManifestId = 3456789012345678901 },
        ];
        app.History =
        [
            new ManifestHistoryEntry { DepotId = 480001, ManifestId = 1234567890123456789, DateUtc = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc) },
            new ManifestHistoryEntry { DepotId = 480001, ManifestId = 1111111111111111111, DateUtc = new DateTime(2025, 3, 14, 8, 30, 0, DateTimeKind.Utc), Label = "Pre-patch" },
            new ManifestHistoryEntry { DepotId = 480001, ManifestId = 999999999999999999, DateUtc = new DateTime(2024, 1, 2, 9, 0, 0, DateTimeKind.Utc), Unavailable = true },
        ];
        apps.Save(app, debounced: false);
        lib.EnsureApp(480000, app.Name);
        var v1 = lib.CreateVersion(480000, root, [(480001, 1111111111111111111ul)], "Pre-patch", new DateTime(2025, 3, 14, 8, 30, 0, DateTimeKind.Utc));
        lib.MarkDepotComplete(v1.Id, 480001, 1111111111111111111);
        File.WriteAllBytes(Path.Combine(lib.GetVersionDir(v1), "data.pak"), new byte[1 << 20]);
        var v2 = lib.CreateVersion(480000, root, [(480001, 1234567890123456789ul)], null, new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc));
        lib.MarkDepotComplete(v2.Id, 480001, 1234567890123456789);
        File.WriteAllBytes(Path.Combine(lib.GetVersionDir(v2), "data.pak"), new byte[3 << 20]);
        lib.SetActive(480000, v2.Id);
        lib.Flush();
        using var settings = new SettingsStore(paths);
        settings.Current.LibraryRoots.Add(LibraryRoots.Normalize(root));
        settings.Save();
        settings.Flush();
    }
}
