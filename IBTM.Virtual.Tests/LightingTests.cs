using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;
using IBTM.Inspection;
using IBTM.Storage;
using IBTM.Virtual;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IBTM.Virtual.Tests;

public sealed class LightingTests
{
    [Fact]
    public async Task ResetRecoversMotionAndBoltHeadsEvenWhenVisionIsStillFaulted()
    {
        var camera = new TestCamera();
        using var services = new ServiceCollection()
            .AddSingleton(VirtualTest.OpenMachineStore())
            .AddIbtmApplication(new MachineSettings
            {
                Drivers = new() { Inspection = InspectionAlgorithm.Virtual, Light = LightDriver.Virtual }
            })
            .AddSingleton<ICamera>(camera)
            .BuildServiceProvider();
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var motion = (VirtualMotionService)services.GetRequiredKeyedService<IXyMotion>(MotionGroup.InspectionGantry);
        var bus = (VirtualAdcBus)services.GetRequiredService<IAdcBus>();
        var head = services.GetRequiredKeyedService<IBoltHead>(FasteningHead.Pickup);
        await machine.InitializeAsync();
        try
        {
            var slave = services.GetRequiredService<MachineSettings>().Hantas.PickupSlaveAddress;
            bus.SetNextFasteningResult(slave, AdcEventStatus.Error);
            await Assert.ThrowsAsync<InvalidOperationException>(() => head.TightenAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(() => head.CheckReadyAsync());
            motion.SetAlarm(MotionAxis.X, true);
            camera.FailInitialize = true;
            state.SetError(MachineAlarm.Inspection, camera.Failure);

            await machine.ResetAsync();

            Assert.False(motion.GetAxisState(MotionAxis.X).Alarm);
            await head.CheckReadyAsync();
            Assert.Equal(MachineAlarm.Inspection, state.Alarm);
            camera.FailInitialize = false;
            await machine.ResetAsync();
            Assert.Equal(MachineAlarm.None, state.Alarm);
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }

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
                return inspector.StartLiveViewAsync();
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
        await inspector.StartLiveViewAsync();
        Assert.True(light.IsOn);
        await inspector.StopLiveViewAsync();
        Assert.False(light.IsOn);
        Assert.Equal(liveChannel, light.LastOffChannel);
        await inspector.StopLiveViewAsync();
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

        light.FailOn = true;
        light.FailOff = true;
        var failure = await Assert.ThrowsAsync<AggregateException>(() => inspector.CaptureCurrentAsync());
        Assert.Contains(light.Failure, failure.InnerExceptions);
        Assert.Contains(light.OffFailure, failure.InnerExceptions);
        light.FailOff = false;
        light.TurnOffAll();
    }

    [Fact]
    public async Task VisionRecoveryAndLiveFailureDoNotDependOnATeachingView()
    {
        var light = new RecordingLight { FailOn = false, Connected = false };
        var camera = new TestCamera();
        using var services = new ServiceCollection()
            .AddSingleton(VirtualTest.OpenMachineStore())
            .AddIbtmApplication(new MachineSettings
            {
                Drivers = new() { Inspection = InspectionAlgorithm.Virtual }
            })
            .AddSingleton<ILightController>(light)
            .AddSingleton<ICamera>(camera)
            .BuildServiceProvider();
        var inspector = services.GetRequiredService<BoltInspector>();

        await Assert.ThrowsAsync<AggregateException>(() => inspector.StartLiveViewAsync());
        Assert.False(inspector.IsLiveView);
        Assert.NotNull(inspector.LiveViewError);
        await inspector.InitializeVisionAsync();
        Assert.True(light.Connected);
        Assert.False(light.IsOn);
        Assert.Null(inspector.LiveViewError);

        await inspector.StartLiveViewAsync();
        Assert.True(light.IsOn);
        await Task.Run(camera.FailLiveView);
        Assert.False(light.IsOn);
        Assert.False(inspector.IsLiveView);
        Assert.Same(camera.Failure, inspector.LiveViewError);

        light.Connected = false;
        camera.FailInitialize = true;
        Assert.Same(camera.Failure, await Assert.ThrowsAsync<IOException>(
            () => inspector.InitializeVisionAsync()));
        Assert.True(light.Connected);
        Assert.False(light.IsOn);

        camera.FailInitialize = false;
        await inspector.InitializeVisionAsync();
        Assert.Null(inspector.LiveViewError);

        using var cancellation = new CancellationTokenSource();
        using var release = new ManualResetEventSlim();
        var starting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        light.OnStarted = () =>
        {
            starting.TrySetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(2)));
        };
        var live = inspector.StartLiveViewAsync(cancellation.Token);
        try
        {
            await starting.Task.WaitAsync(TimeSpan.FromSeconds(2));
            cancellation.Cancel();
        }
        finally
        {
            release.Set();
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => live);
        Assert.False(light.IsOn);
        Assert.False(inspector.IsLiveView);
        light.OnStarted = null;

        await inspector.StartLiveViewAsync();
        await services.GetRequiredService<MachineController>().ShutdownAsync();
        Assert.False(light.IsOn);
        Assert.False(inspector.IsLiveView);
    }

    private sealed class TestCamera : ICamera
    {
        public event Action<ImageFrame>? FrameReady;
        public event Action<Exception>? LiveViewFailed;
        public (int Width, int Height) FrameSize { get; } = (1, 1);
        public bool FailInitialize { get; set; }
        public IOException Failure { get; } = new("Camera disconnected.");

        public void Initialize()
        {
            if (FailInitialize)
                throw Failure;
        }

        public ImageFrame Capture(double exposureMicroseconds, double gain)
        {
            return new(1, 1, 3, [0, 0, 0]);
        }

        public void StartLiveView(double exposureMicroseconds, double gain)
        {
            FrameReady?.Invoke(Capture(exposureMicroseconds, gain));
        }

        public void StopLiveView() { }

        public void FailLiveView()
        {
            LiveViewFailed?.Invoke(Failure);
        }
    }

    private sealed class RecordingLight : ILightController
    {
        public IOException Failure { get; } = new("ON failed after the output was sent.");
        public bool IsOn { get; private set; }
        public int OffCalls { get; private set; }
        public int LastOffChannel { get; private set; }
        public bool FailOn { get; set; } = true;
        public bool FailOff { get; set; }
        public IOException OffFailure { get; } = new("OFF failed.");
        public Action? OnStarted { get; set; }
        public bool Connected { get; set; } = true;

        public void Initialize()
        {
            Connected = true;
        }

        public void SetLevel(int channel, int level)
        {
        }

        public void TurnOn(int channel)
        {
            if (!Connected)
                throw Failure;
            IsOn = true;
            OnStarted?.Invoke();
            if (FailOn)
                throw Failure;
        }

        public void TurnOff(int channel)
        {
            if (!Connected || FailOff)
                throw OffFailure;
            IsOn = false;
            LastOffChannel = channel;
            OffCalls++;
        }

        public void TurnOffAll()
        {
            if (!Connected || FailOff)
                throw OffFailure;
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
    public void ConnectionEditsRequireANewDriverAndDisconnectedOffIsNotReportedAsSuccess()
    {
        var settings = new LightingSettings();
        using var controller = new MovsLightController(settings);
        settings.Connection = "COM9";
        Assert.Contains(
            "COM port is empty",
            Assert.Throws<InvalidOperationException>(controller.Initialize).Message);
        Assert.Throws<InvalidOperationException>(controller.TurnOffAll);
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
