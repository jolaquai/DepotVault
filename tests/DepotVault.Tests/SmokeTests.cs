namespace DepotVault.Tests;

public class SmokeTests
{
    [Fact]
    public void CoreAssemblyLoads() => Assert.NotNull(typeof(SteamKit2.SteamClient).Assembly);
}
