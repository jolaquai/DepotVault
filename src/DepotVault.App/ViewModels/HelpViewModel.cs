using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DepotVault.Core.Persistence;

namespace DepotVault.App.ViewModels;

public partial class HelpViewModel(AppDialogs dialogs, AppPaths paths) : PageViewModel("Help")
{
    public string LogsDir => paths.LogsDir;

    [ObservableProperty]
    private string statusText;

    [RelayCommand]
    private Task ShowTutorialAsync() => dialogs.ShowTutorialAsync();

    [RelayCommand]
    private void OpenLogs()
    {
        try
        {
            Directory.CreateDirectory(paths.LogsDir);
            Process.Start(new ProcessStartInfo(paths.LogsDir) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
    }
}
