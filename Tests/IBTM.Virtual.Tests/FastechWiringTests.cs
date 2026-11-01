using System;
using IBTM.Ajin;
using IBTM.Core;
using IBTM.Device;
using IBTM.Fastech;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IBTM.Virtual.Tests;

public sealed class FastechWiringTests
{
    [Fact]
    public void OnlyS2BackupUsesFastechAndConstructionDoesNotOpenHardware()
    {
        var settings = new MachineSettings();
        using var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton(settings.Options)
            .AddSingleton<OperationCancellation>()
            .AddIbtmHardware(settings)
            .BuildServiceProvider();
        foreach (var (motion, hardware) in settings.MotionSections)
            Assert.IsType<AjinMotionService>(services.GetRequiredKeyedService<IXyMotion>(hardware.Group));
        var backup = services.GetRequiredKeyedService<IAxisMotion>(MotionGroup.BoltFasteningBackup);
        Assert.IsType<FastechZMotion>(backup);
        Assert.Equal([MotionAxis.Z], backup.Axes);
        Assert.Null(((IMotionDiagnostics)backup).ReadDiagnosticState(MotionAxis.Z).State);
        Assert.Contains(settings.Fastech, settings.Sections);
        Assert.Contains(settings.BoltFasteningBackup, settings.Sections);
    }
}
