using System;
using IBTM.Ajin;
using IBTM.Core;
using IBTM.Device;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IBTM.Virtual.Tests;

public sealed class FastechWiringTests
{
    [Fact]
    public void DefaultSettingsKeepEveryGroupOnAjinWithoutOpeningFastech()
    {
        var settings = new MachineSettings();
        using var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton(settings.Options)
            .AddSingleton<OperationCancellation>()
            .AddIbtmHardware(settings)
            .BuildServiceProvider();
        foreach (var group in Enum.GetValues<MotionGroup>())
            Assert.IsType<AjinMotionService>(services.GetRequiredKeyedService<IXyMotion>(group));
        Assert.Contains(settings.Fastech, settings.Sections);
    }
}
