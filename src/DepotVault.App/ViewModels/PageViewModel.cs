using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DepotVault.App.ViewModels;

public abstract class PageViewModel(string title) : ObservableObject
{
    public string Title { get; } = title;

    public virtual void OnActivated() { }

    protected static void Ui(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            action();
        else
            Dispatcher.UIThread.Post(action);
    }
}
