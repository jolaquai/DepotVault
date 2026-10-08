using DepotVault.Core.Library;
using DepotVault.Tests.Download;
using SteamKit2;

namespace DepotVault.Tests.Library;

public class MutableFileScannerTests
{
    private static string P(string s) => s.Replace('\\', Path.DirectorySeparatorChar);

    [Fact]
    public void DetectsPlausiblyMutableFiles()
    {
        var depot = new FakeDepot();
        var m = depot.Build(1, 1, 1024,
            (@"bin\game.exe", FakeDepot.Bytes("x", 100)),
            (@"game.ini", FakeDepot.Bytes("a=1")),
            (@"data\levels.json", FakeDepot.Bytes("{}", 50000)),
            (@"config\keys.json", FakeDepot.Bytes("{}", 50000)),
            (@"saves\slot1.bin", FakeDepot.Bytes("s")),
            (@"readme.txt", FakeDepot.Bytes("hi")),
            (@"assets\big.pak", FakeDepot.Bytes("p", 3000)));
        m.Files.Single(f => f.FileName == @"bin\game.exe").Flags |= EDepotFileFlag.Executable;
        m.Files.Single(f => f.FileName == @"assets\big.pak").Flags |= EDepotFileFlag.UserConfig;

        var c = MutableFileScanner.Scan(m.Files, modifiedSinceLink: rel => rel == P(@"bin\game.exe")).ToDictionary(x => x.RelPath);

        Assert.Equal(MutableReason.ConfigExtension, c["game.ini"].Reasons);
        Assert.False(c.ContainsKey(P(@"data\levels.json")));
        Assert.Equal(MutableReason.ConfigExtension | MutableReason.MutableDirectory, c[P(@"config\keys.json")].Reasons);
        Assert.Equal(MutableReason.MutableDirectory, c[P(@"saves\slot1.bin")].Reasons);
        Assert.True(c.ContainsKey("readme.txt"));
        Assert.Equal(MutableReason.SteamUserConfig, c[P(@"assets\big.pak")].Reasons);
        Assert.Equal(MutableReason.ModifiedSinceLink, c[P(@"bin\game.exe")].Reasons);
    }

    [Fact]
    public void DecisionsAndExclusions()
    {
        var app = new AppRecord { AppId = 1 };
        app.SetDecision(@"cfg\game.cfg", MutableDecision.Share);
        app.SetDecision("saves/*", MutableDecision.Isolate);
        app.Exclusions.Add(@"cfg\game.cfg");
        app.Exclusions.Add("logs/*");
        app.ReviewCandidates.Add("other.ini");

        Assert.Equal(MutableDecision.Share, app.GetDecision("cfg/game.cfg"));
        Assert.Equal(MutableDecision.Isolate, app.GetDecision(@"saves\a.sav"));
        Assert.Equal(MutableDecision.Unreviewed, app.GetDecision("bin/game.exe"));

        Assert.False(app.IsExcluded(@"cfg\game.cfg", []));
        Assert.True(app.IsExcluded(@"logs\x.log", []));
        Assert.True(app.IsExcluded("foo.tmp", ["*.tmp"]));
        Assert.False(app.IsExcluded("foo.bin", ["*.tmp"]));

        var depot = new FakeDepot();
        var m = depot.Build(1, 1, 1024, ("bin.dat", FakeDepot.Bytes("x", 200000)));
        var scan = MutableFileScanner.Scan(m.Files, app);
        Assert.Contains(scan, c => c.RelPath == "other.ini" && c.Reasons == MutableReason.ReportedBySelfHeal);

        app.SetDecision("other.ini", MutableDecision.Isolate);
        Assert.Empty(app.ReviewCandidates);

        var policy = MutableFileScanner.BuildPolicy(_ => app, () => ["*.tmp"], () => true);
        Assert.True(policy.Isolate(1)(@"saves\x"));
        Assert.True(policy.NeverShare(1)("a.tmp"));
        Assert.False(policy.NeverShare(1)(@"cfg\game.cfg"));
    }
}
