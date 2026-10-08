using SteamKit2.Authentication;

namespace DepotVault.Core.Steam;

public interface IGuardPrompt
{
    Task<string> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect);
    Task<string> GetDeviceCodeAsync(bool previousCodeWasIncorrect);
    Task<bool> AcceptDeviceConfirmationAsync();
}

internal sealed class GuardPromptAuthenticator(IGuardPrompt prompt) : IAuthenticator
{
    public Task<string> GetDeviceCodeAsync(bool previousCodeWasIncorrect) => prompt.GetDeviceCodeAsync(previousCodeWasIncorrect);
    public Task<string> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect) => prompt.GetEmailCodeAsync(email, previousCodeWasIncorrect);
    public Task<bool> AcceptDeviceConfirmationAsync() => prompt.AcceptDeviceConfirmationAsync();
}
