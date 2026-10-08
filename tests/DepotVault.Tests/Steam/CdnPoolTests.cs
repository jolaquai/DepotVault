using DepotVault.Core.Steam;
using NSubstitute;

namespace DepotVault.Tests.Steam;

public class CdnPoolTests
{
    private static ICdnServerSource Source(params CdnServer[] servers)
    {
        var src = Substitute.For<ICdnServerSource>();
        src.GetServersAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<CdnServer>>(servers));
        return src;
    }

    private static CdnServer S(string host, string type = "CDN", float load = 0, uint[] allowed = null) => new(null, host, type, load, allowed ?? [], false, false);

    [Fact]
    public async Task PrefersSteamCacheThenLowLoad()
    {
        var pool = new CdnPool(Source(S("a", load: 50), S("b", load: 10), S("c", "SteamCache", 90), S("d", "CS")));
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal("c", (await pool.RentAsync(1, ct)).Host);
        Assert.Equal(3, pool.Count);
    }

    [Fact]
    public async Task FailingServerIsPenalized()
    {
        var pool = new CdnPool(Source(S("a", load: 1), S("b", load: 2)));
        var ct = TestContext.Current.CancellationToken;
        var first = await pool.RentAsync(1, ct);
        Assert.Equal("a", first.Host);
        pool.Return(first, false);
        var second = await pool.RentAsync(1, ct);
        Assert.Equal("b", second.Host);
        pool.Return(second, true);
    }

    [Fact]
    public async Task RespectsAllowedAppIds()
    {
        var pool = new CdnPool(Source(S("restricted", load: 0, allowed: [42]), S("open", load: 5)));
        var ct = TestContext.Current.CancellationToken;
        var s = await pool.RentAsync(7, ct);
        Assert.Equal("open", s.Host);
        pool.Return(s, true);
        Assert.Equal("restricted", (await pool.RentAsync(42, ct)).Host);
    }
}
