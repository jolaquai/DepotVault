using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using DepotVault.App.ViewModels;
using DepotVault.App.Views;
using DepotVault.Core;
using DepotVault.Core.Logging;
using DepotVault.Core.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DepotVault.App;

public partial class App : Application
{
    private ServiceProvider _services;

    public static IServiceProvider Services => ((App)Current)._services ?? OverrideServices;

    internal static IServiceProvider OverrideServices { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _services = ConfigureServices(AppPaths.CreateDefault());
            HookUnhandledExceptions(_services.GetRequiredService<ILoggerFactory>().CreateLogger("Unhandled"));
            var vault = _services.GetRequiredService<Vault>();
            ApplyTheme(vault.Settings.Current.Theme);
            vault.Settings.Changed += s => Dispatcher.UIThread.Post(() => ApplyTheme(s.Theme));
            vault.Start();
            var shell = _services.GetRequiredService<ShellViewModel>();
            desktop.MainWindow = new ShellWindow { DataContext = shell };
            desktop.ShutdownRequested += (_, _) => Shutdown();
            shell.SignInRequested += () => _ = ShowLoginAsync();
            shell.Initialize();
        }
        base.OnFrameworkInitializationCompleted();
    }

    internal static ServiceProvider ConfigureServices(AppPaths paths)
    {
        var services = new ServiceCollection();
        services.AddSingleton(paths);
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Information).AddProvider(new FileLoggerProvider(paths.LogsDir)));
        services.AddSingleton(sp => new Vault(paths, sp.GetRequiredService<ILoggerFactory>()));
        services.AddSingleton<DialogService>();
        services.AddSingleton<AppDialogs>();
        services.AddSingleton<ISwitchPromptsFactory, SwitchPrompts>();
        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<LibraryViewModel>();
        services.AddSingleton<DownloadsViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<HelpViewModel>();
        return services.BuildServiceProvider();
    }

    private static void HookUnhandledExceptions(ILogger log)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => log.LogCritical(e.ExceptionObject as Exception, "Unhandled exception (terminating: {Terminating})", e.IsTerminating);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            log.LogError(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };
        Dispatcher.UIThread.UnhandledException += (_, e) => log.LogError(e.Exception, "Unhandled UI exception");
    }

    private bool _loginOpen;

    private async Task ShowLoginAsync()
    {
        if (_loginOpen)
            return;
        _loginOpen = true;
        try
        {
            await Services.GetRequiredService<DialogService>().ShowAsync<bool>(new LoginDialog { DataContext = new LoginViewModel(Services.GetRequiredService<Vault>()) });
        }
        finally
        {
            _loginOpen = false;
        }
    }

    private void ApplyTheme(AppTheme theme) => RequestedThemeVariant = theme switch
    {
        AppTheme.Light => ThemeVariant.Light,
        AppTheme.Dark => ThemeVariant.Dark,
        _ => ThemeVariant.Default,
    };

    private void Shutdown()
    {
        if (_services is null)
            return;
        _services.Dispose();
        _services = null;
    }
}
