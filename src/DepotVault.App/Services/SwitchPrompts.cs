using DepotVault.Core.SteamInstall;

namespace DepotVault.App;

public interface ISwitchPromptsFactory
{
    ISwitchPrompts Create();
}

public sealed class DeclineSwitchPrompts : ISwitchPromptsFactory, ISwitchPrompts
{
    public ISwitchPrompts Create() => this;
    public Task<bool> WaitForSteamExitAsync(CancellationToken ct) => Task.FromResult(false);
    public Task<CopyConsent> AskCopyAsync(int fileCount, long bytes, CancellationToken ct) => Task.FromResult(new CopyConsent(false, false));
}
