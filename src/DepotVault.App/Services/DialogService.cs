using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;

namespace DepotVault.App;

public sealed class DialogService
{
    public static Window MainWindow => (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    public Task<T> ShowAsync<T>(Window dialog)
    {
        var owner = MainWindow;
        if (owner is null || !owner.IsVisible)
        {
            var tcs = new TaskCompletionSource<T>();
            dialog.Closed += (_, _) => tcs.TrySetResult(default);
            dialog.Show();
            return tcs.Task;
        }
        return dialog.ShowDialog<T>(owner);
    }

    public async Task<string> PickFolderAsync(string title)
    {
        var owner = MainWindow;
        if (owner is null)
            return null;
        var result = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return result.Count > 0 ? result[0].TryGetLocalPath() : null;
    }

    public Task CopyToClipboardAsync(string text) => MainWindow?.Clipboard?.SetTextAsync(text) ?? Task.CompletedTask;

    public Task<string> GetClipboardTextAsync() => MainWindow?.Clipboard?.TryGetTextAsync() ?? Task.FromResult<string>(null);
}
