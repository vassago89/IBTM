using System.Windows;
using IBTM.Composition;
using IBTM.Infrastructure.Persistence;
using IBTM.Orchestration;
using IBTM.Presentation.Shell;
using Microsoft.Extensions.DependencyInjection;

namespace IBTM;

public partial class App : System.Windows.Application
{
    private ServiceProvider _serviceProvider = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var recipeService = new RecipeService();
        var config = recipeService.LoadConfigAsync().GetAwaiter().GetResult();

        var services = new ServiceCollection()
            .AddSingleton(config)
            .AddSingleton(recipeService)
            .AddIbtmApplication();
        _serviceProvider = services.BuildServiceProvider(
            new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true,
            });

        _serviceProvider.GetRequiredService<ProcessOrchestrator>().Initialize();
        var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _serviceProvider.Dispose();
        base.OnExit(e);
    }
}
