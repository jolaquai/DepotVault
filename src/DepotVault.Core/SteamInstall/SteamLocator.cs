using System.Diagnostics;
using Microsoft.Win32;
using SteamKit2;

namespace DepotVault.Core.SteamInstall;

public sealed record SteamLibrary(string Path, IReadOnlySet<uint> Apps)
{
    public string SteamApps => System.IO.Path.Combine(Path, "steamapps");
    public string AcfPath(uint appId) => System.IO.Path.Combine(SteamApps, $"appmanifest_{appId}.acf");
}

public sealed record InstalledApp(uint AppId, SteamLibrary Library, string AcfPath, string InstallPath, AcfFile Acf);

public sealed class SteamLocator(string steamPath)
{
    public string SteamPath { get; } = steamPath;

    public static SteamLocator Detect()
    {
        var path = FindSteamPath();
        return path is null ? null : new SteamLocator(path);
    }

    public static string FindSteamPath()
    {
        if (OperatingSystem.IsWindows())
        {
            var reg = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
            return !string.IsNullOrEmpty(reg) && Directory.Exists(reg) ? System.IO.Path.GetFullPath(reg) : null;
        }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] candidates =
        [
            System.IO.Path.Combine(home, ".steam", "steam"),
            System.IO.Path.Combine(home, ".local", "share", "Steam"),
            System.IO.Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam"),
        ];
        foreach (var c in candidates)
        {
            if (Directory.Exists(System.IO.Path.Combine(c, "steamapps")))
                return new DirectoryInfo(c).ResolveLinkTarget(true)?.FullName ?? System.IO.Path.GetFullPath(c);
        }
        return null;
    }

    public IReadOnlyList<SteamLibrary> GetLibraries()
    {
        var file = System.IO.Path.Combine(SteamPath, "steamapps", "libraryfolders.vdf");
        var result = new List<SteamLibrary>();
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        if (File.Exists(file))
        {
            var kv = KeyValue.LoadAsText(file);
            foreach (var entry in kv?.Children ?? [])
            {
                if (!int.TryParse(entry.Name, out _))
                    continue;
                var path = entry.Value ?? entry["path"].AsString();
                if (string.IsNullOrEmpty(path))
                    continue;
                var apps = new HashSet<uint>();
                foreach (var a in entry["apps"].Children)
                {
                    if (uint.TryParse(a.Name, out var id))
                        apps.Add(id);
                }
                var full = System.IO.Path.GetFullPath(path);
                if (seen.Add(full))
                    result.Add(new SteamLibrary(full, apps));
            }
        }
        if (seen.Add(System.IO.Path.GetFullPath(SteamPath)))
            result.Insert(0, new SteamLibrary(System.IO.Path.GetFullPath(SteamPath), new HashSet<uint>()));
        return result;
    }

    public InstalledApp FindApp(uint appId)
    {
        var libs = GetLibraries();
        foreach (var lib in libs.OrderByDescending(l => l.Apps.Contains(appId)))
        {
            var acfPath = lib.AcfPath(appId);
            if (!File.Exists(acfPath))
                continue;
            var acf = AcfFile.Load(acfPath);
            return new InstalledApp(appId, lib, acfPath, acf.InstallPath(lib.Path), acf);
        }
        return null;
    }

    public IEnumerable<InstalledApp> EnumerateInstalled()
    {
        foreach (var lib in GetLibraries())
        {
            if (!Directory.Exists(lib.SteamApps))
                continue;
            foreach (var acfPath in Directory.EnumerateFiles(lib.SteamApps, "appmanifest_*.acf"))
            {
                AcfFile acf;
                try { acf = AcfFile.Load(acfPath); }
                catch (FormatException) { continue; }
                if (acf.AppId != 0)
                    yield return new InstalledApp(acf.AppId, lib, acfPath, acf.InstallPath(lib.Path), acf);
            }
        }
    }

    public static bool IsSteamRunning()
    {
        string[] names = OperatingSystem.IsWindows() ? ["steam"] : ["steam", "steamwebhelper", "steam-runtime-launcher-service"];
        foreach (var name in names)
        {
            var procs = Process.GetProcessesByName(name);
            var running = procs.Length > 0;
            foreach (var p in procs)
                p.Dispose();
            if (running)
                return true;
        }
        return false;
    }
}
