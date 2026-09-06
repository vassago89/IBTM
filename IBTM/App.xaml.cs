using System.Linq;
using System.Threading;
using System.Windows;
using IBTM.UI;
using Microsoft.Extensions.DependencyInjection;

namespace IBTM;

public partial class App : System.Windows.Application
{
    private Mutex? _instanceMutex;
    private ServiceProvider? _serviceProvider;

    protected override async void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Contains(DevelopmentProfile.Argument)
            && !DevelopmentProfile.IsEnabled)
        {
            MessageBox.Show(
                "Select the Virtual build configuration for the Virtual launch profile.",
                "IBTM",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            Shutdown();
            return;
        }

        var instanceMutex = new Mutex(
            initiallyOwned: true,
            DevelopmentProfile.IsEnabled
                ? @"Global\IBTM.VirtualDevelopment"
                : @"Global\IBTM.Application",
            out var createdNew);
        if (!createdNew)
        {
            instanceMutex.Dispose();
            MessageBox.Show(
                "IBTM is already running.",
                "IBTM",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            Shutdown();
            return;
        }
        _instanceMutex = instanceMutex;

        base.OnStartup(e);

        var store = new MachineStore();
        if (DevelopmentProfile.IsEnabled)
        {
            await DevelopmentProfile.PrepareAsync(store);
        }
        var settings = await store.LoadSettingsAsync();
        if (DevelopmentProfile.IsEnabled)
        {
            DevelopmentProfile.UseVirtualHardware(settings);
        }
        var recipe = settings.RecipeSelection.LastRecipeName is { } recipeName
            ? await store.LoadRecipeAsync(recipeName)
            : new Recipe();

        var services = new ServiceCollection()
            .AddSingleton(store)
            .AddIbtmApplication(settings, recipe);
        var serviceProvider = services.BuildServiceProvider(
            new ServiceProviderOptions
            {
                ValidateOnBuild = true,
            });
        _serviceProvider = serviceProvider;

        await serviceProvider
            .GetRequiredService<MachineController>()
            .InitializeAsync();
        var mainWindow = serviceProvider.GetRequiredService<MainWindow>();
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _serviceProvider?.GetService<MachineController>()?.Stop();
        _serviceProvider?.Dispose();
        _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

}
