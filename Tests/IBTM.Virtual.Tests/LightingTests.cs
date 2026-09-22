using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
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
        await using var services = new ServiceCollection()
            .AddSingleton(VirtualTest.OpenMachineStore())
            .AddIbtmApplication(new MachineSettings
            {
                Drivers = new() { Light = LightDriver.Virtual }
            })
            .AddSingleton<ICamera>(camera)
            .BuildServiceProvider();
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var motion = (VirtualMotionService)services.GetRequiredKeyedService<IXyMotion>(MotionGroup.InspectionGantry);
        var bus = (VirtualAdcBus)services.GetRequiredKeyedService<IAdcBus>(FasteningHead.Pickup);
        var head = services.GetRequiredKeyedService<IBoltHead>(FasteningHead.Pickup);
        await machine.InitializeAsync();
        try
        {
            var slave = services.GetRequiredService<MachineSettings>().Hantas.PickupSlaveAddress;
            await head.SelectPresetAsync(1);
            bus.SetNextFasteningResult(slave, AdcEventStatus.Error);
            Assert.False((await head.TightenAsync()).Success);
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
        var camera = new TestCamera();
        var settings = new MachineSettings();
        Assert.Equal(100, settings.Lighting.StabilizationDelayMilliseconds);
        Assert.Throws<ArgumentOutOfRangeException>(() => settings.Lighting.StabilizationDelayMilliseconds = -1);
        settings.Lighting.StabilizationDelayMilliseconds = 250;
        light.OnStarted = () => settings.Lighting.InspectionChannel++;
        await using var services = new ServiceCollection().AddSingleton(
            VirtualTest.OpenMachineStore(
                Path.Combine(Path.GetTempPath(), $"IBTM-light-cleanup-{Guid.NewGuid():N}.db")))
            .AddIbtmApplication(settings)
            .AddSingleton<ILightController>(light)
            .AddSingleton<ICamera>(camera)
            .BuildServiceProvider();
        var inspector = services.GetRequiredService<InspectionStation>();
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
                await inspector.CaptureCarrierImageAsync();
            },
            () =>
            {
                return inspector.StartLiveViewAsync();
            },
        })
        {
            light.Connected = false;
            var channel = settings.Lighting.InspectionChannel;
            var error = await Assert.ThrowsAsync<IOException>(action);
            Assert.Same(light.Failure, error);
            Assert.True(light.Connected);
            Assert.False(light.IsOn);
            Assert.Equal(channel, light.LastOffChannel);
        }

        Assert.Equal(3, light.OffCalls);

        light.FailOn = false;
        var liveChannel = settings.Lighting.InspectionChannel;
        await inspector.StartLiveViewAsync();
        Assert.True(light.IsOn);
        var offCalls = light.OffCalls;
        Assert.NotEmpty((await inspector.CaptureCurrentAsync(keepLiveView: true)).Pixels);
        Assert.True(inspector.IsLiveView);
        Assert.True(light.IsOn);
        Assert.Equal(offCalls, light.OffCalls);
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
            () => inspector.CaptureCarrierImageAsync(cancellation.Token));
        Assert.Equal(5, light.OffCalls); // An already-cancelled scan must not touch the light.

        var stabilization = new Stopwatch();
        light.OnStarted = stabilization.Restart;
        camera.OnCapture = () =>
        {
            Assert.True(light.IsOn);
            // Task.Delay uses the Windows system clock, whose tick can precede Stopwatch by one tick.
            Assert.True(stabilization.ElapsedMilliseconds >= settings.Lighting.StabilizationDelayMilliseconds - 20);
        };
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
    public async Task EachInspectionTargetUsesItsOwnLightForAutomaticCaptureAndLiveGrab()
    {
        var light = new RecordingLight { FailOn = false };
        var settings = new MachineSettings();
        settings.Lighting.StabilizationDelayMilliseconds = 0;
        await using var services = new ServiceCollection()
            .AddSingleton(VirtualTest.OpenMachineStore())
            .AddIbtmApplication(settings)
            .AddSingleton<ILightController>(light)
            .BuildServiceProvider();
        var inspector = services.GetRequiredService<InspectionStation>();
        var recipe = services.GetRequiredService<RecipeManager>().Current;
        var motion = services.GetRequiredKeyedService<IXyMotion>(MotionGroup.InspectionGantry);
        motion.Initialize();
        Assert.True(await inspector.HomeHorizontalAsync());
        recipe.BoltInspection.LightLevel = 91;
        recipe.BoltInspection.DataMatrix1.LightLevel = 31;
        recipe.BoltInspection.DataMatrix2.LightLevel = 62;
        var bolt = new BoltPoint { Number = 1, LightLevel = 123 };
        recipe.CarrierImages = [
            new() { IsBarcode = true, HeatSink = HeatSinkSlot.HeatSink1, Region = new(0, 0, 20, 20) },
            new() { IsBarcode = true, HeatSink = HeatSinkSlot.HeatSink2, Region = new(0, 0, 20, 20) },
            new() { BoltNumber = 1, Region = new(0, 0, 20, 20) },
        ];

        await inspector.CaptureBarcodeAsync(HeatSinkSlot.HeatSink1);
        Assert.Equal(31, light.LastLevel);
        await inspector.CaptureBarcodeAsync(HeatSinkSlot.HeatSink2);
        Assert.Equal(62, light.LastLevel);
        await inspector.CaptureAsync(bolt);
        Assert.Equal(123, light.LastLevel);
        bolt.LightLevel = null;
        await inspector.CaptureAsync(bolt);
        Assert.Equal(91, light.LastLevel);

        await inspector.StartLiveViewAsync(lightLevel: 45);
        Assert.Equal(45, light.LastLevel);
        var offCalls = light.OffCalls;
        await inspector.CaptureCurrentAsync(keepLiveView: true, lightLevel: 67);
        Assert.True(inspector.IsLiveView);
        Assert.Equal(67, light.LastLevel);
        await inspector.CaptureCarrierImageAsync(lightLevel: 89);
        Assert.Equal(89, light.LastLevel);
        Assert.Equal(offCalls, light.OffCalls);
        await inspector.StopLiveViewAsync();
        Assert.False(light.IsOn);
    }

    [Fact]
    public async Task InspectionSettingsChangedDuringCaptureApplyStartingWithTheNextPoint()
    {
        var light = new RecordingLight { FailOn = false };
        var camera = new TestCamera();
        var settings = new MachineSettings();
        settings.Lighting.StabilizationDelayMilliseconds = 0;
        await using var services = new ServiceCollection()
            .AddSingleton(VirtualTest.OpenMachineStore())
            .AddIbtmApplication(settings)
            .AddSingleton<ILightController>(light)
            .AddSingleton<ICamera>(camera)
            .BuildServiceProvider();
        var inspector = services.GetRequiredService<InspectionStation>();
        var recipes = services.GetRequiredService<RecipeManager>();
        var motion = services.GetRequiredKeyedService<IXyMotion>(MotionGroup.InspectionGantry);
        motion.Initialize();
        Assert.True(await inspector.HomeHorizontalAsync());
        var bolt = new BoltPoint { Number = 1, X = 0, Y = 0, LightLevel = 23, BrightnessThreshold = 128, MinimumBrightRatio = 0.5 };
        recipes.Current.Pcb.BoltPoints.Add(bolt);
        recipes.Current.CarrierImages = [new() { BoltNumber = 1, Region = new(0, 0, 1, 1) }];
        var edited = new Recipe();
        edited.Pcb.BoltPoints.Add(new() { Number = 1, LightLevel = 87, BrightnessThreshold = 0, MinimumBrightRatio = 0 });
        edited.CarrierImages = [new() { BoltNumber = 1, Region = new(0, 0, 1, 1) }];
        camera.OnCapture = () =>
        {
            lock (recipes.InspectionSync)
                recipes.Current.ApplyInspectionSettings(edited);
        };

        var inspect = typeof(InspectionStation).GetMethod("InspectAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var first = await (Task<InspectionCapture>)inspect.Invoke(inspector, [bolt, CancellationToken.None])!;
        Assert.Equal(23, light.LastLevel);
        Assert.False(first.Success);
        Assert.Equal(0.5, first.MinimumBrightRatio);
        camera.OnCapture = null;
        var second = await (Task<InspectionCapture>)inspect.Invoke(inspector, [bolt, CancellationToken.None])!;
        Assert.Equal(87, light.LastLevel);
        Assert.True(second.Success);
        Assert.Equal(0, second.MinimumBrightRatio);
    }

    [Fact]
    public async Task VisionRecoveryAndLiveFailureDoNotDependOnATeachingView()
    {
        var light = new RecordingLight { FailOn = false, Connected = false };
        var camera = new TestCamera();
        await using var services = new ServiceCollection()
            .AddSingleton(VirtualTest.OpenMachineStore())
            .AddIbtmApplication(new MachineSettings())
            .AddSingleton<ILightController>(light)
            .AddSingleton<ICamera>(camera)
            .BuildServiceProvider();
        var inspector = services.GetRequiredService<InspectionStation>();

        // Teaching can start while automatic inspection is disabled and startup skipped vision.
        await inspector.StartLiveViewAsync();
        Assert.True(light.Connected);
        Assert.True(light.IsOn);
        Assert.True(inspector.IsLiveView);
        await inspector.StopLiveViewAsync();
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
            Assert.False(inspector.IsLiveView);
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
    public async Task LightSelectionIsIndependentAndMissingComDoesNotBreakConstruction(
        ControlDriver motion,
        LightDriver light)
    {
        var settings = new MachineSettings();
        settings.Drivers.Control = motion;
        settings.Drivers.Light = light;
        await using var services = new ServiceCollection().AddIbtmApplication(settings).BuildServiceProvider();
        var controller = services.GetRequiredService<ILightController>();
        if (light == LightDriver.Virtual)
            Assert.IsType<VirtualLightController>(controller);
        else
        {
            Assert.IsType<MovsLightController>(controller);
            // AnyWave leaves an empty COM disconnected. No physical port is opened.
            controller.Initialize();
            controller.SetLevel(2, 80);
            controller.TurnOn(2);
            controller.TurnOff(2);
            controller.TurnOffAll();
        }
    }

    private sealed class TestCamera : ICamera
    {
        public TestCamera()
        {
            Failure = new("Camera disconnected.");
        }

        public event Action<ImageFrame>? FrameReady;
        public event Action<Exception>? LiveViewFailed;

        public bool IsLiveView { get; private set; }
        public (int Width, int Height) FrameSize { get; } = (1, 1);
        public bool FailInitialize { get; set; }
        public IOException Failure { get; }
        public Action? OnCapture { get; set; }

        public void Initialize()
        {
            if (FailInitialize)
                throw Failure;
            IsLiveView = false;
        }

        public Task<ImageFrame> CaptureAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OnCapture?.Invoke();
            return Task.FromResult(new ImageFrame(1, 1, 3, [0, 0, 0]));
        }

        public void StartLiveView()
        {
            IsLiveView = true;
            FrameReady?.Invoke(new(1, 1, 3, [0, 0, 0]));
        }

        public void StopLiveView()
        {
            IsLiveView = false;
        }

        public void FailLiveView()
        {
            IsLiveView = false;
            LiveViewFailed?.Invoke(Failure);
        }
    }

    private sealed class RecordingLight : ILightController
    {
        public RecordingLight()
        {
            Failure = new("ON failed after the output was sent.");
            OffFailure = new("OFF failed.");
        }

        public IOException Failure { get; }
        public bool IsOn { get; private set; }
        public int OffCalls { get; private set; }
        public int LastOffChannel { get; private set; }
        public int LastLevel { get; private set; }
        public bool FailOn { get; set; } = true;
        public bool FailOff { get; set; }
        public IOException OffFailure { get; }
        public Action? OnStarted { get; set; }
        public bool Connected { get; set; } = true;

        public void Initialize()
        {
            Connected = true;
        }

        public void SetLevel(int channel, int level)
        {
            LastLevel = level;
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
}
