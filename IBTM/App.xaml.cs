using System.Threading;
using System.Threading.Tasks;
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
        var instanceMutex = new Mutex(
            initiallyOwned: true,
            @"Global\IBTM.Application",
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
        var settings = await store.LoadSettingsAsync();

        var services = new ServiceCollection()
            .AddSingleton(settings)
            .AddSingleton(store)
            .AddIbtmApplication(settings);
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
