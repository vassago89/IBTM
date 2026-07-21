using System.Windows;
using Microsoft.Extensions.DependencyInjection;

namespace IBTM;

public partial class App : System.Windows.Application
{
    private ServiceProvider _serviceProvider = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var services = new ServiceCollection().AddIbtmApplication();
        _serviceProvider = services.BuildServiceProvider(
            new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true,
            });

        LoadMachineConfig(_serviceProvider);
        _serviceProvider.GetRequiredService<ProcessOrchestrator>().Initialize();
        var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _serviceProvider.Dispose();
        base.OnExit(e);
    }

    private static void LoadMachineConfig(IServiceProvider services)
    {
        var recipeService = services.GetRequiredService<RecipeService>();
        var loaded = recipeService.LoadConfigAsync().GetAwaiter().GetResult();
        services.GetRequiredService<MachineConfig>().CopyFrom(loaded);
        Loc.Instance.SetLanguage(loaded.System.Language);
    }
}
