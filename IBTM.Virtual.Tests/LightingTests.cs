using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;
using IBTM.Inspection;
using IBTM.Storage;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IBTM.Virtual.Tests;

public sealed class LightingTests
{
    [Fact]
    public async Task InspectionCleansUpTheOriginalLightChannelBeforeCompletionOrCancellation()
    {
        var light = new RecordingLight();
        var settings = new MachineSettings();
        light.OnStarted = () => settings.Lighting.InspectionChannel++;
        settings.Drivers.Inspection = InspectionAlgorithm.Virtual;
        using var services = new ServiceCollection().AddSingleton(
            VirtualTest.OpenMachineStore(
                Path.Combine(Path.GetTempPath(), $"IBTM-light-cleanup-{Guid.NewGuid():N}.db")))
            .AddIbtmApplication(settings)
            .AddSingleton<ILightController>(light)
            .BuildServiceProvider();
        var inspector = services.GetRequiredService<BoltInspector>();
        var reference = services.GetRequiredService<CarrierReferenceSettings>();
        reference.UpperLeftLocatingPin = new() { X = 0, Y = 0 };
        reference.LowerRightLocatingPin = new() { X = 20, Y = 20 };
        foreach (var action in new Func<Task>[]
        {
            async () =>
            {
                await inspector.CaptureCurrentAsync();
            },
            async () =>
            {
                await inspector.CaptureCarrierImagesAsync();
            },
            () =>
            {
                inspector.StartLiveView();
                return Task.CompletedTask;
            },
        })
        {
            var channel = settings.Lighting.InspectionChannel;
            var error = await Assert.ThrowsAsync<IOException>(action);
            Assert.Same(light.Failure, error);
            Assert.False(light.IsOn);
            Assert.Equal(channel, light.LastOffChannel);
        }

        Assert.Equal(3, light.OffCalls);

        light.FailOn = false;
        var liveChannel = settings.Lighting.InspectionChannel;
        inspector.StartLiveView();
        Assert.True(light.IsOn);
        inspector.StopLiveView();
        Assert.False(light.IsOn);
        Assert.Equal(liveChannel, light.LastOffChannel);
        inspector.StopLiveView();
        Assert.Equal(4, light.OffCalls);

        using var cancellation = new CancellationTokenSource();
        light.OnStarted = cancellation.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => inspector.CaptureCurrentAsync(cancellation.Token));
        Assert.False(light.IsOn);
        Assert.Equal(5, light.OffCalls);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => inspector.CaptureCarrierImagesAsync(cancellation.Token));
        Assert.Equal(5, light.OffCalls); // An already-cancelled scan must not touch the light.

        light.OnStarted = null;
        Assert.NotEmpty((await inspector.CaptureCurrentAsync()).Pixels);
        Assert.Equal(6, light.OffCalls);
    }

    private sealed class RecordingLight : ILightController
    {
        public IOException Failure { get; } = new("ON failed after the output was sent.");
        public bool IsOn { get; private set; }
        public int OffCalls { get; private set; }
        public int LastOffChannel { get; private set; }
        public bool FailOn { get; set; } = true;
        public Action? OnStarted { get; set; }

        public void Initialize()
        {
        }

        public void SetLevel(int channel, int level)
        {
        }

        public void TurnOn(int channel)
        {
            IsOn = true;
            OnStarted?.Invoke();
            if (FailOn)
                throw Failure;
        }

        public void TurnOff(int channel)
        {
            IsOn = false;
            LastOffChannel = channel;
            OffCalls++;
        }

        public void TurnOffAll()
        {
            IsOn = false;
        }
    }

    [Fact]
    public void VirtualDevelopmentAlsoForcesLightingToVirtual()
    {
        var settings = new MachineSettings();
        settings.Drivers.Light = LightDriver.Movs;
        DevelopmentProfile.UseVirtualHardware(settings);
        Assert.Equal(LightDriver.Virtual, settings.Drivers.Light);
    }

    [Theory]
    [InlineData(ControlDriver.Physical, LightDriver.Virtual)]
    [InlineData(ControlDriver.Virtual, LightDriver.Movs)]
    public void LightSelectionIsIndependentAndMissingComDoesNotBreakConstruction(
        ControlDriver motion,
        LightDriver light)
    {
        var settings = new MachineSettings();
        settings.Drivers.Control = motion;
        settings.Drivers.Light = light;
        using var services = new ServiceCollection().AddIbtmApplication(settings).BuildServiceProvider();
        var controller = services.GetRequiredService<ILightController>();
        if (light == LightDriver.Virtual)
            Assert.IsType<VirtualLightController>(controller);
        else
        {
            Assert.IsType<MovsLightController>(controller);
            // Empty COM is rejected before any OS port is opened.
            var error = Assert.Throws<InvalidOperationException>(controller.Initialize);
            Assert.Contains("COM port is empty", error.Message);
            Assert.Contains("Settings > Devices & Safety > Lighting", error.Message);
            Assert.Throws<InvalidOperationException>(() => controller.SetLevel(2, 80));
        }
    }

    [Fact]
    public void ConnectionEditsRequireANewDriverAndUninitializedShutdownIsSafe()
    {
        var settings = new LightingSettings();
        using var controller = new MovsLightController(settings);
        settings.Connection = "COM9";
        Assert.Contains(
            "COM port is empty",
            Assert.Throws<InvalidOperationException>(controller.Initialize).Message);
        controller.TurnOffAll();
    }

    [Fact]
    public void MovsRejectsValuesThatDoNotFitTheCommandBeforeWriting()
    {
        using var controller = new MovsLightController(new LightingSettings());
        Assert.Throws<ArgumentOutOfRangeException>(() => controller.SetLevel(2, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => controller.SetLevel(2, 256));
        Assert.Throws<ArgumentOutOfRangeException>(() => controller.TurnOn(10));
        Assert.Throws<ArgumentOutOfRangeException>(() => controller.TurnOff(-1));
        Assert.Throws<InvalidOperationException>(() => controller.TurnOff(0));
    }

    [Theory]
    [InlineData(0, 1000)]
    [InlineData(19200, 0)]
    public void InvalidSerialSettingsReportLightingErrorAndCanBeRetried(int baudRate, int timeout)
    {
        using var controller = new MovsLightController(
            new LightingSettings { Connection = "COM9", BaudRate = baudRate, WriteTimeoutMilliseconds = timeout, });
        for (var attempt = 0; attempt < 2; attempt++)
        {
            // Invalid constructor/setter values fail before SerialPort.Open.
            var error = Assert.Throws<InvalidOperationException>(controller.Initialize);
            Assert.Contains("MOVS light connection failed (COM9)", error.Message);
            Assert.IsAssignableFrom<ArgumentException>(error.InnerException);
        }
    }
}
