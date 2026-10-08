using DepotVault.Core.Import;
using DepotVault.Core.Library;

namespace DepotVault.Tests.Import;

public class SteamDbParserTests
{
    private const string Dash = "–";

    [Fact]
    public void ParsesSteamDbTablePaste()
    {
        var paste = $"""
            https://steamdb.info/depot/480001/manifests/
            Date	Relative	ManifestID
            3 October 2024 {Dash} 17:23:45 UTC	5 days ago	1234567890123456789
            11 September 2024 {Dash} 09:01:02 UTC	4 weeks ago	987654321098765432
            1 January 2020 {Dash} 00:00:00 UTC	4 years ago	1234567890123456789
            """;
        var r = SteamDbParser.Parse(paste);
        Assert.Equal(480001u, r.DepotId);
        Assert.Equal(3, r.Rows.Count);
        Assert.Equal(1234567890123456789ul, r.Rows[0].ManifestId);
        Assert.Equal(new DateTime(2024, 10, 3, 17, 23, 45, DateTimeKind.Utc), r.Rows[0].DateUtc);
        Assert.Equal(DateTimeKind.Utc, r.Rows[0].DateUtc.Kind);
        Assert.Equal(new DateTime(2024, 9, 11, 9, 1, 2, DateTimeKind.Utc), r.Rows[1].DateUtc);
        Assert.Equal(ImportStatus.Duplicate, r.Rows[2].Status);
        Assert.Equal(2, r.NewCount);
    }

    [Fact]
    public void PlainIdListsAndOtherFormats()
    {
        var paste = "1111111111111111111\n2222222222222222222\r\n2023-06-01 12:34:56  3333333333333333333\nJune 5, 2022\t4444444444444444444\nbuild 12345678901 broken\n\nheader only";
        var r = SteamDbParser.Parse(paste, existing: [2222222222222222222]);
        Assert.Equal(0u, r.DepotId);
        Assert.Equal(5, r.Rows.Count);
        Assert.Equal(ImportStatus.New, r.Rows[0].Status);
        Assert.Equal(default, r.Rows[0].DateUtc);
        Assert.Equal(ImportStatus.Duplicate, r.Rows[1].Status);
        Assert.Equal(new DateTime(2023, 6, 1, 12, 34, 56, DateTimeKind.Utc), r.Rows[2].DateUtc);
        Assert.Equal(new DateTime(2022, 6, 5, 0, 0, 0, DateTimeKind.Utc), r.Rows[3].DateUtc);
        Assert.Equal(ImportStatus.Invalid, r.Rows[4].Status);
    }

    [Fact]
    public void CommitAddsOnlyNewRowsSortedByDate()
    {
        var app = new AppRecord();
        app.History.Add(new ManifestHistoryEntry { DepotId = 5, ManifestId = 111111111111111111, DateUtc = new DateTime(2021, 1, 1) });
        var r = SteamDbParser.Parse($"1 March 2022 {Dash} 10:00:00 UTC\t222222222222222222\n1 March 2019 {Dash} 10:00:00 UTC\t111111111111111111\n1 March 2020 {Dash} 10:00:00 UTC\t333333333333333333", app.History.Select(h => h.ManifestId));
        Assert.Equal(2, SteamDbParser.Commit(app, 5, r.Rows));
        Assert.Equal([222222222222222222ul, 111111111111111111, 333333333333333333], app.History.Select(h => h.ManifestId));
        Assert.Equal(0, SteamDbParser.Commit(app, 5, r.Rows));
    }

    [Fact]
    public void ReadsBranchesFromSteamDbRows()
    {
        var paste = "8 October 2026 – 06:56:13 UTC\t13 hours ago\t7858560538903071442\t\n"
            + "2 October 2026 – 20:59:16 UTC\t6 days ago\t4414572937642724539 local\t\n"
            + "1 October 2026 – 11:49:07 UTC\t7 days ago\t426889136175453125 local\t\n"
            + "22 September 2026 – 03:00:17 UTC\t17 days ago\t6087551716114811127 local\t\n"
            + "22 September 2026 – 03:00:17 UTC\t17 days ago\t5846939894323649224\n"
            + "3333333333333333333\n"
            + "2023-06-01 12:34:56\t4444444444444444444\tbeta_2\n";
        var r = SteamDbParser.Parse(paste);
        Assert.Equal([7858560538903071442ul, 4414572937642724539, 426889136175453125, 6087551716114811127, 5846939894323649224, 3333333333333333333, 4444444444444444444], r.Rows.Select(x => x.ManifestId));
        Assert.Equal(["public", "local", "local", "local", "public", null, "beta_2"], r.Rows.Select(x => x.Branch));
        Assert.Equal(new DateTime(2026, 10, 8, 6, 56, 13, DateTimeKind.Utc), r.Rows[0].DateUtc);

        var app = new AppRecord();
        app.History.Add(new ManifestHistoryEntry { DepotId = 1, ManifestId = 7858560538903071442 });
        Assert.Equal(6, SteamDbParser.Commit(app, 1, SteamDbParser.Parse(paste, app.History.Select(h => h.ManifestId)).Rows));
        Assert.Equal("public", app.History.Single(h => h.ManifestId == 7858560538903071442).Branch);
        Assert.Equal("local", app.History.Single(h => h.ManifestId == 4414572937642724539).Branch);
    }
}
