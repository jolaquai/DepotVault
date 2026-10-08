using System.Diagnostics;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DepotVault.Core.SteamInstall;

namespace DepotVault.App.ViewModels;

public partial class SwitchReportViewModel(SwitchReport report, DialogService dialogs) : ObservableObject, IDialogViewModel
{
    public IReadOnlyList<SwitchFailure> Failures { get; } = report.Failures;

    public string Summary { get; } = (report.Failures.Count == 1 ? "1 problem" : $"{report.Failures.Count} problems")
        + " came up. The install may be partly switched; fix the cause and switch again, or revert to the original install."
        + (report.Staged.Count > 0 ? $" {report.Staged.Count} file(s) were moved out of the way to {report.StagingDir} and can be restored from there." : "");

    public bool HasStaging => !string.IsNullOrEmpty(report.StagingDir) && Directory.Exists(report.StagingDir);

    [ObservableProperty]
    private string statusText;

    public event Action CloseRequested;

    public void OnOpened() { }

    public void OnClosed() { }

    [RelayCommand]
    private async Task CopyAsync()
    {
        var sb = new StringBuilder(Summary).AppendLine().AppendLine();
        foreach (var f in Failures)
            sb.Append(f.Path).Append(": ").AppendLine(f.Reason);
        await dialogs.CopyToClipboardAsync(sb.ToString());
        StatusText = "Copied to clipboard.";
    }

    [RelayCommand]
    private void OpenStaging()
    {
        try
        {
            Process.Start(new ProcessStartInfo(report.StagingDir) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke();
}
