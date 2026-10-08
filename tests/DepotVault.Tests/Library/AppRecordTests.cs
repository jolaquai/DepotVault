using DepotVault.Core.Library;

namespace DepotVault.Tests.Library;

public class AppRecordTests
{
    [Fact]
    public void MarkUnavailableFlagsOnlyTheMatchingEntry()
    {
        var app = new AppRecord
        {
            History =
            [
                new ManifestHistoryEntry { DepotId = 1, ManifestId = 10 },
                new ManifestHistoryEntry { DepotId = 2, ManifestId = 10 },
                new ManifestHistoryEntry { DepotId = 1, ManifestId = 11 },
            ],
        };

        Assert.True(app.MarkUnavailable(1, 10));
        Assert.False(app.MarkUnavailable(1, 10));
        Assert.False(app.MarkUnavailable(3, 10));
        Assert.Equal([true, false, false], app.History.Select(h => h.Unavailable));
    }
}
