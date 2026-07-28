using System.Windows;
using IBTM.Sequence;
using IBTM.UI;
using Microsoft.Extensions.DependencyInjection;

namespace IBTM;

public partial class App : System.Windows.Application
{
    private ServiceProvider _serviceProvider = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var store = new MachineStore();
        var settings = store.LoadSettingsAsync().GetAwaiter().GetResult();

        var services = new ServiceCollection()
            .AddSingleton(settings)
            .AddSingleton(store)
            .AddIbtmApplication(settings);
        _serviceProvider = services.BuildServiceProvider(
            new ServiceProviderOptions
            {
                ValidateOnBuild = true,
            });

        _serviceProvider.GetRequiredService<AutoSequence>().Initialize();
        var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _serviceProvider.Dispose();
        base.OnExit(e);
    }
}
