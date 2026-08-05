using System.Threading.Tasks;
using System.Windows;
using IBTM.UI;
using Microsoft.Extensions.DependencyInjection;

namespace IBTM;

public partial class App : System.Windows.Application
{
    private SingleInstanceGuard? _singleInstanceGuard;
    private ServiceProvider? _serviceProvider;

    protected override async void OnStartup(StartupEventArgs e)
    {
        _singleInstanceGuard = SingleInstanceGuard.TryAcquire();
        if (_singleInstanceGuard is null)
        {
            MessageBox.Show(
                "IBTM is already running.",
                "IBTM",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            Shutdown();
            return;
        }

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
            .GetRequiredService<EquipmentService>()
            .InitializeAsync();
        var mainWindow = serviceProvider.GetRequiredService<MainWindow>();
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _serviceProvider?.Dispose();
        _singleInstanceGuard?.Dispose();
        base.OnExit(e);
    }

}
