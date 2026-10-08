using DepotVault.Core.SteamInstall;

namespace DepotVault.Tests.SteamInstall;

public class SteamInstallTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "steam", "steamapps", name));

    private static (TempDir Dir, SteamLocator Locator, string Lib2) FakeSteam()
    {
        var dir = new TempDir();
        var steam = dir.Combine("Steam");
        var lib2 = dir.Combine("Lib2");
        Directory.CreateDirectory(Path.Combine(steam, "steamapps"));
        Directory.CreateDirectory(Path.Combine(lib2, "steamapps", "common", "Sample Game"));
        File.WriteAllText(Path.Combine(steam, "steamapps", "libraryfolders.vdf"), Fixture("libraryfolders.vdf").Replace("STEAMROOT", steam.Replace(@"\", @"\\")).Replace("LIB2", lib2.Replace(@"\", @"\\")));
        File.WriteAllText(Path.Combine(lib2, "steamapps", "appmanifest_480000.acf"), Fixture("appmanifest_480000.acf"));
        return (dir, new SteamLocator(steam), lib2);
    }

    [Fact]
    public void ParsesLibrariesAndFindsApp()
    {
        var (dir, locator, lib2) = FakeSteam();
        using var _ = dir;
        var libs = locator.GetLibraries();
        Assert.Equal(2, libs.Count);
        Assert.Contains(228980u, libs[0].Apps);
        Assert.Contains(480000u, libs[1].Apps);

        var app = locator.FindApp(480000);
        Assert.NotNull(app);
        Assert.Equal(Path.GetFullPath(lib2), app.Library.Path);
        Assert.Equal(Path.Combine(lib2, "steamapps", "common", "Sample Game"), app.InstallPath);
        Assert.Null(locator.FindApp(1));
        Assert.Single(locator.EnumerateInstalled());
    }

    [Fact]
    public void ReadsAcfFields()
    {
        var acf = AcfFile.Parse(Fixture("appmanifest_480000.acf"));
        Assert.Equal(480000u, acf.AppId);
        Assert.Equal("Sample Game", acf.Name);
        Assert.Equal("Sample Game", acf.InstallDir);
        Assert.Equal(9876543u, acf.BuildId);
        Assert.Equal(4, acf.StateFlags);
        Assert.Equal(0, acf.AutoUpdateBehavior);
        Assert.Equal([new InstalledDepot(480001, 1234567890123456789, 4294967296)], acf.InstalledDepots);
        Assert.Equal(@"C:\Program Files (x86)\Steam\steam.exe", acf.Document.GetValue("AppState", "LauncherPath"));
    }

    [Fact]
    public void UnchangedDocumentRoundTripsExactly()
    {
        var text = Fixture("appmanifest_480000.acf");
        Assert.Equal(text, AcfFile.Parse(text).Document.ToString());
    }

    [Fact]
    public void PatchesOnlyTargetKeys()
    {
        var text = Fixture("appmanifest_480000.acf");
        var acf = AcfFile.Parse(text);
        acf.SetBuildId(1111);
        acf.SetAutoUpdateBehavior(1);
        acf.SetInstalledDepot(480001, 42, 0);
        var patched = acf.Document.ToString();

        var expected = text
            .Replace("\"buildid\"\t\t\"9876543\"", "\"buildid\"\t\t\"1111\"")
            .Replace("\"TargetBuildID\"\t\t\"9876543\"", "\"TargetBuildID\"\t\t\"1111\"")
            .Replace("\"AutoUpdateBehavior\"\t\t\"0\"", "\"AutoUpdateBehavior\"\t\t\"1\"")
            .Replace("\"manifest\"\t\t\"1234567890123456789\"", "\"manifest\"\t\t\"42\"");
        Assert.Equal(expected, patched);

        var reparsed = AcfFile.Parse(patched);
        Assert.Equal(1111u, reparsed.BuildId);
        Assert.Equal(1111u, reparsed.TargetBuildId);
        Assert.Equal(1, reparsed.AutoUpdateBehavior);
        Assert.Equal(42ul, reparsed.InstalledDepots[0].ManifestId);
    }

    [Fact]
    public void InsertsMissingDepotWithMatchingIndentation()
    {
        var text = Fixture("appmanifest_480000.acf");
        var acf = AcfFile.Parse(text);
        acf.SetInstalledDepot(480002, 77, 1000);
        var patched = acf.Document.ToString();
        Assert.Contains("\t\t\"480002\"\r\n\t\t{\r\n\t\t\t\"manifest\"\t\t\"77\"\r\n\t\t\t\"size\"\t\t\"1000\"\r\n\t\t}\r\n\t}", patched);
        var depots = AcfFile.Parse(patched).InstalledDepots;
        Assert.Equal(2, depots.Count);
        Assert.Equal(new InstalledDepot(480002, 77, 1000), depots[1]);
        Assert.StartsWith(text[..text.IndexOf("\t\"InstalledDepots\"", StringComparison.Ordinal)], patched);
    }

    [Fact]
    public void SavePreservesReadOnly()
    {
        using var dir = new TempDir();
        var path = dir.Combine("a.acf");
        File.WriteAllText(path, Fixture("appmanifest_480000.acf"));
        Core.FileUtil.SetReadOnly(path, true);
        var acf = AcfFile.Load(path);
        acf.SetBuildId(5);
        acf.Save();
        Assert.True(Core.FileUtil.IsReadOnly(path));
        Assert.Equal(5u, AcfFile.Load(path).BuildId);
        Core.FileUtil.SetReadOnly(path, false);
    }
}
