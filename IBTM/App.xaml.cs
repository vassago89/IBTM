using Microsoft.Extensions.DependencyInjection;
using System.Windows;

namespace IBTM;

public partial class App : System.Windows.Application
{
    private ServiceProvider? _serviceProvider;

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
        _serviceProvider?.Dispose();
        base.OnExit(e);
    }

    private static void LoadMachineConfig(IServiceProvider services)
    {
        try
        {
            var recipeService = services.GetRequiredService<RecipeService>();
            var loaded = recipeService.LoadConfigAsync().GetAwaiter().GetResult();
            services.GetRequiredService<MachineConfig>().CopyFrom(loaded);
            Loc.Instance.SetLanguage(loaded.Language);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"Machine configuration could not be loaded. Defaults will be used.\n\n{exception.Message}",
                "IBTM configuration warning",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }
}
