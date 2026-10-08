using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using DepotVault.App;
using DepotVault.App.ViewModels;
using DepotVault.App.Views;
using DepotVault.Core.Persistence;
using Microsoft.Extensions.DependencyInjection;

var outDir = Path.GetFullPath(args.Length > 0 ? args[0] : "uisnap");
var dataDir = Path.GetFullPath(args.Length > 1 ? args[1] : Path.Combine(outDir, "data"));
Directory.CreateDirectory(outDir);

AppBuilder.Configure<App>().UseSkia().WithInterFont().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();

var scenario = args.Length > 2 ? args[2] : "pages";
Scenarios.Prepare(scenario, dataDir);
var paths = new AppPaths(dataDir);
Seed.Run(paths);
using var services = App.ConfigureServices(paths);
App.OverrideServices = services;
var shell = services.GetRequiredService<ShellViewModel>();
var window = new ShellWindow { DataContext = shell, Width = 1200, Height = 780 };
window.Show();
Pump();

switch (scenario)
{
    case "pages":
        foreach (var page in shell.Pages)
        {
            shell.CurrentPage = page;
            page.OnActivated();
            Pump();
            Thread.Sleep(300);
            Save(window, page.Title.ToLowerInvariant());
        }
        break;
    default:
        Scenarios.Run(scenario, services, shell, window, Save, Pump);
        break;
}
window.Close();
return;

void Pump()
{
    for (var i = 0; i < 20; i++)
    {
        Dispatcher.UIThread.Post(static () => { }, DispatcherPriority.Background);
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Thread.Sleep(10);
    }
}

void Save(TopLevel top, string name)
{
    Pump();
    var frame = top.CaptureRenderedFrame();
    var file = Path.Combine(outDir, name + ".png");
    frame?.Save(file, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    Console.WriteLine(file);
}
