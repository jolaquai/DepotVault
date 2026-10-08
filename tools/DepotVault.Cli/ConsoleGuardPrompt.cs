using DepotVault.Core.Steam;

namespace DepotVault.Cli;

internal sealed class ConsoleGuardPrompt : IGuardPrompt
{
    public Task<string> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect)
    {
        Console.Write(previousCodeWasIncorrect ? "Wrong code. " : "");
        Console.Write($"Steam Guard code sent to {email}: ");
        return Task.FromResult(Console.ReadLine()?.Trim());
    }

    public Task<string> GetDeviceCodeAsync(bool previousCodeWasIncorrect)
    {
        Console.Write(previousCodeWasIncorrect ? "Wrong code. " : "");
        Console.Write("Steam Guard mobile authenticator code: ");
        return Task.FromResult(Console.ReadLine()?.Trim());
    }

    public Task<bool> AcceptDeviceConfirmationAsync()
    {
        Console.WriteLine("Approve the login in the Steam mobile app...");
        return Task.FromResult(true);
    }
}
