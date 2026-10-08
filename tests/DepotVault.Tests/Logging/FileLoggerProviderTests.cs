using DepotVault.Core.Logging;
using Microsoft.Extensions.Logging;

namespace DepotVault.Tests.Logging;

public class FileLoggerProviderTests
{
    [Fact]
    public void WritesFilteredLinesAndFlushesOnDispose()
    {
        using var dir = new TempDir();
        string file;
        using (var provider = new FileLoggerProvider(dir.Combine("logs")))
        {
            file = provider.CurrentFile;
            var log = provider.CreateLogger("DepotVault.Core.Vault");
            log.LogDebug("hidden");
            log.LogInformation("hello {Name}", "world");
            log.LogError(new InvalidOperationException("boom"), "failed");
        }
        var text = File.ReadAllText(file);
        Assert.DoesNotContain("hidden", text);
        Assert.Contains("[INF] Vault: hello world", text);
        Assert.Contains("[ERR] Vault: failed", text);
        Assert.Contains("System.InvalidOperationException: boom", text);
    }
}
