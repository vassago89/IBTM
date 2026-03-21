using IBTM.Device;
using IBTM.Models;
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
        // ── 디바이스 서비스 (구간별 3축 모션) ────────────────────────────────
        services.AddKeyedSingleton<IMotionService, VirtualMotionService>("zone1"); // 구간1: 픽업 XYZ
        services.AddKeyedSingleton<IMotionService, VirtualMotionService>("zone2"); // 구간2: 볼트 체결 XYZ
        services.AddKeyedSingleton<IMotionService, VirtualMotionService>("zone3"); // 구간3: 검사 XYZ
        // IO 서비스 (컨베이어, 스토퍼, 얼라인, 리프트, 그리퍼 등)
        services.AddSingleton<IIOService, VirtualOService>();

        // ── 애플리케이션 서비스 ──────────────────────────────────────────────
        services.AddSingleton<IFiducialService, FiducialService>();
        services.AddSingleton<IBoltService, StubBoltService>();
        services.AddSingleton<ProcessOrchestrator>();
        services.AddSingleton<RecipeService>();
        services.AddKeyedSingleton<ICameraStreamService, VirtualCameraStreamService>("zone2"); // 구간2: 피듀셜 카메라
        services.AddKeyedSingleton<ICameraStreamService, VirtualCameraStreamService>("zone3"); // 구간3: 검사 카메라
        services.AddSingleton<MachineConfig>();

        // ── ViewModels ───────────────────────────────────────────────────────
        services.AddSingleton<ProcessViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<TeachingViewModel>();
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
