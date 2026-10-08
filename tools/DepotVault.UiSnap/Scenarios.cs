using Avalonia.Controls;
using DepotVault.App.ViewModels;

internal static class Scenarios
{
    public static Task RunAsync(string name, IServiceProvider services, ShellViewModel shell, Window window, Action<TopLevel, string> save, Action pump) =>
        throw new ArgumentException($"Unknown scenario {name}");
}
