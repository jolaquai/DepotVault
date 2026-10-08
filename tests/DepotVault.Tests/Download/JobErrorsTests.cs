using System.Net.Sockets;
using DepotVault.Core.Download;
using DepotVault.Core.Steam;
using SteamKit2;

namespace DepotVault.Tests.Download;

public class JobErrorsTests
{
    [Fact]
    public void ClassifiesKnownFailures()
    {
        Assert.Equal(JobErrorKind.ManifestUnavailable, JobErrors.Classify(new ManifestUnavailableException(1, 2, "gone")).Kind);
        Assert.Equal(JobErrorKind.AccessDenied, JobErrors.Classify(new DepotAccessException(1, EResult.AccessDenied)).Kind);
        Assert.Equal(JobErrorKind.DiskFull, JobErrors.Classify(new IOException("No space left on device", 28)).Kind);
        Assert.Equal(JobErrorKind.DiskFull, JobErrors.Classify(new IOException("disk full", unchecked((int)0x80070070))).Kind);
        Assert.Equal(JobErrorKind.Network, JobErrors.Classify(new IOException("chunk failed", new HttpRequestException("503"))).Kind);
        Assert.Equal(JobErrorKind.Network, JobErrors.Classify(new SocketException()).Kind);
        Assert.Equal(JobErrorKind.SteamTimeout, JobErrors.Classify(new TaskCanceledException()).Kind);
        Assert.Equal(JobErrorKind.SteamTimeout, JobErrors.Classify(new AggregateException(new TimeoutException())).Kind);

        var other = JobErrors.Classify(new IOException("locked"));
        Assert.Equal(JobErrorKind.Other, other.Kind);
        Assert.Equal("locked", other.Message);
    }

    [Fact]
    public void OnlyNetworkAndTimeoutsAreTransient()
    {
        foreach (var k in Enum.GetValues<JobErrorKind>())
            Assert.Equal(k is JobErrorKind.Network or JobErrorKind.SteamTimeout, JobErrors.IsTransient(k));
    }
}
