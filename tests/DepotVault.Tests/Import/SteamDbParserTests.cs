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
}
