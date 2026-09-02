using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IBTM.BoltFastening;
using IBTM.Core;
using IBTM.Device;
using IBTM.Inspection;
using IBTM.PcbPlacement;
using IBTM.PcbSupply;
using IBTM.Virtual;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IBTM.Virtual.Tests;

public sealed class MachineLifecycleTests
{
    [Theory]
    [InlineData(HeatSinkLoad.Both, false)]
    [InlineData(HeatSinkLoad.None, false)]
    [InlineData(HeatSinkLoad.HeatSink1, true)]
    public async Task OneCarrierFlowsThroughTheWholeMachine(
        HeatSinkLoad heatSinkLoad,
        bool fasteningNg)
    {
        var settings = FlowSettings();
        using var services = CreateServices(settings);
        var recipe = services.GetRequiredService<Recipe>();
        recipe.PcbSupply = new PcbSupplyRecipe
        {
            Pcb1PickPosition = new() { X = 10, Z = 10 },
            Pcb2PickPosition = new() { X = 20, Z = 10 },
        };
        recipe.PcbPlacement = new PcbPlacementRecipe
        {
            HeatSink1PcbPlacementPosition = new()
            {
                X = 20,
                Y = 100,
                Z = 10,
            },
            HeatSink2PcbPlacementPosition = new()
            {
                X = 40,
                Y = 100,
                Z = 10,
            },
        };
        recipe.BoltFastening = new BoltFasteningRecipe
        {
            BoltPoints =
            [
                new()
                {
                    Number = 1,
                    HeatSink = HeatSinkSlot.HeatSink1,
                    Head = FasteningHead.Shooting,
                    X = 12,
                    Y = 11,
                    Z = 10,
                },
                new()
                {
                    Number = 2,
                    HeatSink = HeatSinkSlot.HeatSink1,
                    Head = FasteningHead.Pickup,
                    X = 28,
                    Y = 11,
                    Z = 10,
                },
                new()
                {
                    Number = 3,
                    HeatSink = HeatSinkSlot.HeatSink2,
                    Head = FasteningHead.Shooting,
                    X = 12,
                    Y = 19,
                    Z = 10,
                },
                new()
                {
                    Number = 4,
                    HeatSink = HeatSinkSlot.HeatSink2,
                    Head = FasteningHead.Pickup,
                    X = 28,
                    Y = 19,
                    Z = 10,
                },
            ],
        };
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var inspection = services.GetRequiredService<InspectionWork>();
        var adc = Assert.IsType<VirtualAdcBus>(
            services.GetRequiredService<IAdcBus>());
        if (fasteningNg)
        {
            adc.SetNextFasteningResult(
                settings.Hantas.ShootingSlaveAddress,
                AdcEventStatus.FasteningNg);
        }

        var finished = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var reachedExit = false;
        HeatSinkAssembly[]? completedAssemblies = null;
        inspection.Changed += () =>
        {
            if (completedAssemblies is null && inspection.Completed)
            {
                completedAssemblies = inspection.Assemblies.ToArray();
            }
        };
        io.InputChanged += (input, value) =>
        {
            if (input == InputIo.PcbPlacementCarrierPresent && value)
            {
                io.SetInput(
                    InputIo.PcbPlacementHeatSink1Present,
                    (heatSinkLoad & HeatSinkLoad.HeatSink1) != 0);
                io.SetInput(
                    InputIo.PcbPlacementHeatSink2Present,
                    (heatSinkLoad & HeatSinkLoad.HeatSink2) != 0);
            }

            if (input == InputIo.MainConveyorExitCarrierDetected
                && value)
            {
                reachedExit = true;
            }

            var expectedNg = fasteningNg || heatSinkLoad == HeatSinkLoad.None;
            var okFinished = !expectedNg
                && reachedExit
                && !io.GetInput(
                    InputIo.MainConveyorExitCarrierDetected)
                && io.GetInput(InputIo.InspectionBackupPlateUp);
            var ngFinished = expectedNg
                && io.GetInput(InputIo.NgConveyorPosition1Occupied)
                && !io.GetInput(InputIo.NgConveyorPosition3Occupied)
                && io.GetInput(InputIo.NgShuttleUp)
                && !io.GetInput(InputIo.NgShuttleCarrierDetected)
                && !io.GetOutput(OutputIo.NgConveyorRun);
            if (okFinished || ngFinished)
            {
                finished.TrySetResult();
                machine.Stop();
            }
        };
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        io.SetInput(InputIo.AutoMode, true);

        var run = machine.StartAsync();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await run.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(MachineAlarm.None, state.Alarm);
        Assert.False(state.IsRunning);
        var expectedHeatSinks = Enum.GetValues<HeatSinkSlot>()
            .Where(heatSink => heatSink switch
            {
                HeatSinkSlot.HeatSink1 =>
                    (heatSinkLoad & HeatSinkLoad.HeatSink1) != 0,
                HeatSinkSlot.HeatSink2 =>
                    (heatSinkLoad & HeatSinkLoad.HeatSink2) != 0,
                _ => false,
            })
            .ToArray();
        var assemblies = Assert.IsType<HeatSinkAssembly[]>(completedAssemblies);
        Assert.Equal(expectedHeatSinks.Length, assemblies.Length);
        foreach (var assembly in assemblies)
        {
            Assert.Contains(assembly.HeatSink, expectedHeatSinks);
            var assemblyNg = fasteningNg
                && assembly.HeatSink == expectedHeatSinks[0];
            Assert.Equal(
                !assemblyNg,
                Assert.Single(assembly.PcbBoltResults).Value.Success);
            Assert.True(Assert.Single(
                assembly.IpmSeatingResults).Value.Success);
            Assert.True(Assert.Single(
                assembly.IpmFinalResults).Value.Success);
            Assert.Equal(2, assembly.BoltPresenceResults.Count);
            Assert.All(assembly.BoltPresenceResults.Values, Assert.True);
            Assert.Equal(
                assemblyNg ? AssemblyResult.Ng : AssemblyResult.Ok,
                assembly.FasteningResult);
            Assert.Equal(AssemblyResult.Ok, assembly.InspectionResult);
            Assert.Equal(
                assemblyNg ? AssemblyResult.Ng : AssemblyResult.Ok,
                assembly.Result);
        }

        var expectedNg = fasteningNg || heatSinkLoad == HeatSinkLoad.None;
        Assert.Equal(!expectedNg, reachedExit);
        Assert.Equal(
            expectedNg,
            io.GetInput(InputIo.NgConveyorPosition1Occupied));
    }

    [Fact]
    public async Task EachUnitCanRunByItself()
    {
        foreach (var unit in Enum.GetValues<MachineUnit>())
        {
            var settings = new MachineSettings
            {
                Units = EnableOnly(unit),
                Home = FastHome(),
            };
            using var services = CreateServices(settings);
            if (unit is MachineUnit.BoltFastening or MachineUnit.Inspection)
            {
                PrepareCarrierTeaching(
                    settings,
                    services.GetRequiredService<Recipe>());
            }

            var machine = services.GetRequiredService<MachineController>();
            var state = services.GetRequiredService<MachineState>();
            var io = services.GetRequiredService<VirtualIoService>();

            await machine.InitializeAsync();
            if (machine.CanHome)
            {
                await machine.HomeAsync(CancellationToken.None);
            }

            io.SetInput(InputIo.AutoMode, true);
            Assert.True(machine.CanStart);

            var run = machine.StartAsync();
            await WaitUntilAsync(() => state.AutomaticRunning);
            machine.Stop();
            await run.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(MachineAlarm.None, state.Alarm);
            Assert.False(state.IsRunning);
        }
    }

    [Fact]
    public async Task DoorTripStopsAndResetsFromLiveHardwareState()
    {
        var settings = new MachineSettings
        {
            Home = FastHome(),
            Units = EnableOnly(MachineUnit.BoltFastening),
        };
        using var services = CreateServices(settings);
        PrepareCarrierTeaching(
            settings,
            services.GetRequiredService<Recipe>());
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();

        await machine.InitializeAsync();

        Assert.False(machine.CanStart);
        Assert.True(machine.CanHome);
        Assert.False(machine.CanReset);

        await machine.HomeAsync(CancellationToken.None);
        Assert.True(state.Homed);

        io.SetInput(InputIo.AutoMode, true);
        Assert.True(machine.CanStart);
        var firstRun = machine.StartAsync();
        await WaitUntilAsync(() => state.AutomaticRunning);

        io.SetInput(InputIo.Door1Open, true);
        await firstRun.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(MachineAlarm.DoorOpen, state.Alarm);
        Assert.False(state.IsRunning);
        Assert.False(state.ServosOn);

        io.SetInput(InputIo.AutoMode, false);
        io.SetInput(InputIo.ResetButton, true);
        await WaitUntilAsync(() => !state.IsError);
        io.SetInput(InputIo.ResetButton, false);

        Assert.True(state.ServosOn);
        Assert.True(state.Homed);

        io.SetInput(InputIo.Door1Open, false);
        io.SetInput(InputIo.AutoMode, true);
        var secondRun = machine.StartAsync();
        await WaitUntilAsync(() => state.AutomaticRunning);

        machine.Stop();
        await secondRun.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(MachineAlarm.None, state.Alarm);
        Assert.False(state.IsRunning);
    }

    [Fact]
    public async Task UnitTimeoutStopsWithItsOwnAlarmAndCanRestart()
    {
        var settings = new MachineSettings
        {
            Units = EnableOnly(MachineUnit.ShootingBoltFeeder),
        };
        settings.BoltFeeder.ShootingTimeoutMilliseconds = 50;
        using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();

        await machine.InitializeAsync();
        io.SetInput(InputIo.AutoMode, true);
        await machine.StartAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(MachineAlarm.ShootingBoltFeeder, state.Alarm);
        Assert.False(io.GetOutput(OutputIo.ShootingFeederRunSignal));
        Assert.True(machine.CanReset);

        await machine.ResetAsync();
        Assert.Equal(MachineAlarm.None, state.Alarm);

        settings.BoltFeeder.ShootingTimeoutMilliseconds = 500;
        var resumed = machine.StartAsync();
        await ((IIoService)io).WaitForInputAsync(
            InputIo.ShootingFeederBoltDetected,
            true);

        machine.Stop();
        await resumed.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(MachineAlarm.None, state.Alarm);
        Assert.False(state.IsRunning);
    }

    [Fact]
    public async Task IoCommunicationFailureStopsAndCanReset()
    {
        var settings = new MachineSettings
        {
            Units = EnableOnly(MachineUnit.MainConveyor),
        };
        using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();

        await machine.InitializeAsync();
        io.SetInput(InputIo.AutoMode, true);
        var run = machine.StartAsync();
        await WaitUntilAsync(() => state.AutomaticRunning);

        io.SetConnected(false);
        await run.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(MachineAlarm.IoCommunication, state.Alarm);
        Assert.False(state.IsRunning);

        io.SetConnected(true);
        Assert.True(machine.CanReset);
        await machine.ResetAsync();

        Assert.Equal(MachineAlarm.None, state.Alarm);
        var resumed = machine.StartAsync();
        await WaitUntilAsync(() => state.AutomaticRunning);
        machine.Stop();
        await resumed.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(state.IsRunning);
    }

    [Fact]
    public async Task MotionAlarmStopsAndCanReset()
    {
        var settings = new MachineSettings
        {
            Home = FastHome(),
            Units = EnableOnly(MachineUnit.BoltFastening),
        };
        using var services = CreateServices(settings);
        PrepareCarrierTeaching(
            settings,
            services.GetRequiredService<Recipe>());
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var motion = (VirtualMotionService)services
            .GetRequiredKeyedService<IXyMotion>(MotionGroup.BoltFastening);

        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        io.SetInput(InputIo.AutoMode, true);
        var run = machine.StartAsync();
        await WaitUntilAsync(() => state.AutomaticRunning);

        motion.SetAlarm(MotionAxis.X, true);
        await run.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(MachineAlarm.MotionUnavailable, state.Alarm);
        Assert.False(state.IsRunning);

        await machine.ResetAsync();

        Assert.Equal(MachineAlarm.None, state.Alarm);
        Assert.False(state.Faulted);
        Assert.True(state.ServosOn);
        var resumed = machine.StartAsync();
        await WaitUntilAsync(() => state.AutomaticRunning);
        machine.Stop();
        await resumed.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(state.IsRunning);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(2));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private static ServiceProvider CreateServices(MachineSettings settings)
        => new ServiceCollection()
            .AddIbtmApplication(settings)
            .BuildServiceProvider();

    private static UnitSettings EnableOnly(MachineUnit unit) => new()
    {
        MainConveyor = unit == MachineUnit.MainConveyor,
        PcbSupply = unit == MachineUnit.PcbSupply,
        PcbPlacement = unit == MachineUnit.PcbPlacement,
        PickupBoltFeeder = unit == MachineUnit.PickupBoltFeeder,
        ShootingBoltFeeder = unit == MachineUnit.ShootingBoltFeeder,
        BoltFastening = unit == MachineUnit.BoltFastening,
        Inspection = unit == MachineUnit.Inspection,
        NgConveyor = unit == MachineUnit.NgConveyor,
    };

    private static HomeSettings FastHome() => new()
    {
        HorizontalSpeed = 10_000,
        ZSpeed = 10_000,
    };

    private static MachineSettings FlowSettings()
    {
        var settings = new MachineSettings
        {
            Home = FastHome(),
        };
        settings.PcbSupply.Motion = FastMotion();
        settings.PcbSupply.RotationZ = 0;
        settings.PcbSupply.CarrierY = 10;
        settings.PcbSupply.BufferHandoffPosition = new()
        {
            X = 80,
            Y = 30,
            Z = 10,
        };
        settings.PcbSupply.BufferClearZ = 20;
        settings.PcbBuffer.SupplyBoundary1 = 60;
        settings.PcbBuffer.SupplyBoundary2 = 100;
        settings.PcbBuffer.PlacementBoundary1 = new() { X = 60, Y = 20 };
        settings.PcbBuffer.PlacementBoundary2 = new() { X = 100, Y = 40 };
        settings.PcbPlacementHandler.Motion = FastMotion();
        settings.PcbPlacementHandler.BufferEntryZ = 0;
        settings.PcbPlacementHandler.BufferHandoffPosition = new()
        {
            X = 80,
            Y = 30,
            Z = 10,
        };
        settings.BoltFastening.Motion = FastMotion();
        settings.BoltFastening.SafeZ = 0;
        settings.BoltFastening.PickupPosition = new()
        {
            X = 100,
            Y = 50,
            Z = 10,
        };
        settings.BoltFastening.PickupHead = HeadSettings();
        settings.BoltFastening.ShootingHead = HeadSettings();
        settings.InspectionGantry.Motion = FastMotion();
        settings.CarrierReference.UpperLeftLocatingPin = new() { X = 0, Y = 0 };
        settings.CarrierReference.LowerRightLocatingPin = new() { X = 100, Y = 0 };
        settings.NgConveyor.TransferSpeed = 10_000;
        settings.NgConveyor.CarrierPickupPosition = new() { X = 20, Y = 20 };
        settings.NgConveyor.ShuttlePlacePosition = new() { X = 150, Y = 20 };
        return settings;
    }

    private static MotionSettings FastMotion() => new()
    {
        HorizontalSpeed = 10_000,
        ZSpeed = 10_000,
    };

    private static BoltHeadSettings HeadSettings() => new()
    {
        UpperLeftLocatingPin = new() { X = 0, Y = 0 },
        LowerRightLocatingPin = new() { X = 100, Y = 0 },
    };

    private static void PrepareCarrierTeaching(
        MachineSettings settings,
        Recipe recipe)
    {
        settings.CarrierReference.UpperLeftLocatingPin = new() { X = 0, Y = 0 };
        settings.CarrierReference.LowerRightLocatingPin = new() { X = 100, Y = 0 };
        settings.BoltFastening.PickupHead = HeadSettings();
        settings.BoltFastening.ShootingHead = HeadSettings();
        recipe.BoltFastening.BoltPoints.Add(new BoltPoint
        {
            Number = 1,
            HeatSink = HeatSinkSlot.HeatSink1,
            Head = FasteningHead.Shooting,
            X = 10,
            Y = 10,
            Z = 10,
        });
    }

    public enum MachineUnit
    {
        MainConveyor,
        PcbSupply,
        PcbPlacement,
        PickupBoltFeeder,
        ShootingBoltFeeder,
        BoltFastening,
        Inspection,
        NgConveyor,
    }

    [Flags]
    public enum HeatSinkLoad
    {
        None = 0,
        HeatSink1 = 1,
        HeatSink2 = 2,
        Both = HeatSink1 | HeatSink2,
    }
}
