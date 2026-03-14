using IBTM.Device;
using IBTM.Services;
using IBTM.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using System.Windows;

namespace IBTM;

public partial class App : Application
{
    private ServiceProvider? _serviceProvider;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var services = new ServiceCollection();
        ConfigureServices(services);
        _serviceProvider = services.BuildServiceProvider();

        var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
        mainWindow.Show();
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        // ── 디바이스 서비스 ──────────────────────────────────────────────────
        // Transfer 3축 (라인 선택 + PCB 이송)
        services.AddKeyedSingleton<IMotionService, VirtualMotionService>("transfer");
        // Fiducial 3축 (마크 검출 + 위치 보정)
        services.AddKeyedSingleton<IMotionService, VirtualMotionService>("fiducial");
        // IO 서비스 (컨베이어, 그리퍼 등 - IO 확정 후 AjinIOService로 교체)
        services.AddSingleton<IIOService, VirtualOService>();

        // ── 애플리케이션 서비스 ──────────────────────────────────────────────
        services.AddSingleton<IFiducialService, FiducialService>();
        // 볼트 체결기 (프로토콜 확정 후 실구현으로 교체)
        services.AddSingleton<IBoltService, StubBoltService>();
        services.AddSingleton<ProcessOrchestrator>();

        // ── ViewModels ───────────────────────────────────────────────────────
        services.AddSingleton<ProcessViewModel>();
        services.AddSingleton<MainViewModel>();

        // ── Windows ──────────────────────────────────────────────────────────
        services.AddSingleton<MainWindow>();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _serviceProvider?.Dispose();
        base.OnExit(e);
    }
}
