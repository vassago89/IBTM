using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using IBTM.BoltFastening;
using IBTM.Core;
using IBTM.Conveyor;
using IBTM.Device;
using IBTM.Inspection;
using IBTM.NgConveyor;
using IBTM.PcbPlacement;
using IBTM.PcbSupply;
using IBTM.UI;
using IBTM.Virtual;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IBTM.Virtual.Tests;

public sealed class MachineLifecycleTests
{
    [Fact]
    public async Task BufferSetupRequiresIdleManualControl()
    {
        using var services = CreateServices(FlowSettings());
        var machine = services.GetRequiredService<MachineController>();
        var teaching = services.GetRequiredService<SupplyTeachingViewModel>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        Assert.True(teaching.SaveBufferSetupCommand.CanExecute(null));

        using (services.GetRequiredService<OperationCancellation>().Link())
        {
            Assert.False(teaching.SaveBufferSetupCommand.CanExecute(null));
        }
        Assert.True(teaching.SaveBufferSetupCommand.CanExecute(null));

        io.SetInput(InputIo.AutoMode, true);
        Assert.False(teaching.SaveBufferSetupCommand.CanExecute(null));
    }

    [Fact]
    public async Task NgTransferResumesCarryingWithoutReturningToPickup()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.NgCarrierTransfer);
        using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var gantry = services.GetRequiredService<InspectionGantry>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        io.SetInput(InputIo.InspectionCarrierPresent, true);
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.InspectionBackupPlateUp, true);
        io.SetInput(InputIo.AutoMode, true);
        void StopDuringTransfer(double x, double y, double z)
        {
            if (x > 40 && io.GetInput(InputIo.NgCarrierDetected))
            {
                machine.Stop();
            }
        }

        gantry.Feedback.PositionChanged += StopDuringTransfer;
        await machine.StartAsync().WaitAsync(TimeSpan.FromSeconds(5));
        gantry.Feedback.PositionChanged -= StopDuringTransfer;
        var stoppedX = gantry.Feedback.GetPosition().X;
        Assert.InRange(stoppedX, 40, 149);
        Assert.False(gantry.Feedback.IsMoving);
        Assert.True(io.GetInput(InputIo.NgCarrierGripperClosed));
        Assert.True(io.GetInput(InputIo.NgCarrierDetected));
        Assert.Equal(MachineAlarm.None, state.Alarm);

        var minimumX = stoppedX;
        gantry.Feedback.PositionChanged += (x, _, _) => minimumX = Math.Min(minimumX, x);
        var resumed = machine.StartAsync();
        try
        {
            Assert.True(await VirtualTest.WaitUntilAsync(
                () => io.GetInput(InputIo.NgShuttleCarrierDetected)
                    && io.GetInput(InputIo.NgCarrierPickupUp)
                    && !io.GetInput(InputIo.NgCarrierDetected),
                TimeSpan.FromSeconds(5)));
        }
        finally
        {
            machine.Stop();
            await resumed;
        }

        Assert.True(minimumX >= stoppedX);
        Assert.Equal(MachineAlarm.None, state.Alarm);
    }

    [Theory]
    [InlineData(NgTransferLiftState.Down)]
    [InlineData(NgTransferLiftState.Between)]
    [InlineData(NgTransferLiftState.Up)]
    public async Task ReleasedNgCarrierIsNotGrippedAgainOnRestart(NgTransferLiftState lift)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.NgCarrierTransfer);
        using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<VirtualIoService>();
        var gantry = services.GetRequiredService<InspectionGantry>();
        var station = services.GetRequiredService<InspectionStation>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        await gantry.MoveToAsync(settings.NgCarrierTransfer.ShuttlePlacePosition, 10_000);
        io.AutoResponseEnabled = false;
        io.SetInput(InputIo.NgCarrierDetected, true);
        io.SetInput(InputIo.NgShuttleCarrierDetected, true);
        io.SetInput(InputIo.NgCarrierPickupDown, lift == NgTransferLiftState.Down);
        io.SetInput(InputIo.NgCarrierPickupUp, lift == NgTransferLiftState.Up);

        Assert.Equal(lift == NgTransferLiftState.Up
            ? InspectionStationState.Waiting
            : InspectionStationState.RaisingCarrierTransfer, station.State([]));

        using var stop = new CancellationTokenSource();
        var run = station.RunAsync([], stop.Token);
        Assert.False(io.GetOutput(OutputIo.NgCarrierGripperClose));
        stop.Cancel();
        await run;

        // The carrier may leave the pickup sensor before the gripper reaches Open.
        io.SetInput(InputIo.NgCarrierDetected, false);
        io.SetInput(InputIo.NgCarrierPickupUp, false);
        io.SetInput(InputIo.NgCarrierPickupDown, true);
        io.SetInput(InputIo.NgCarrierGripperOpen, false);
        io.SetInput(InputIo.NgCarrierGripperClosed, false);
        Assert.Equal(InspectionStationState.OpeningTransferGripper, station.State([]));
    }

    [Fact]
    public async Task MissingInspectionModelAllowsSetupButBlocksAutomaticStart()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.Inspection);
        settings.Drivers.Inspection = InspectionAlgorithm.TinyUnet;
        settings.BoltInspection.ModelFile = Path.Combine(
            Path.GetTempPath(), $"IBTM-missing-{Guid.NewGuid():N}.dat");
        using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        services.GetRequiredService<Recipe>().BoltFastening.BoltPoints.Add(
            new BoltPoint { Number = 1, X = 10, Y = 10 });

        await machine.InitializeAsync();
        Assert.Equal(MachineAlarm.None, state.Alarm);
        Assert.True(machine.CanHome);
        await machine.HomeAsync(CancellationToken.None);
        Assert.True(state.ManualControlsEnabled);

        io.SetInput(InputIo.AutoMode, true);
        Assert.True(machine.CanStart);
        await machine.StartAsync();
        Assert.Equal(MachineAlarm.Inspection, state.Alarm);
        Assert.False(state.IsRunning);

        io.SetInput(InputIo.AutoMode, false);
        await machine.ResetAsync();
        Assert.Equal(MachineAlarm.None, state.Alarm);
        Assert.True(state.ManualControlsEnabled);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    public async Task InspectionAndConveyorAgreeOnBypassRoute(
        bool inspectionEnabled, bool transferEnabled, bool expectNg)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.MainConveyor);
        settings.Units.Inspection = inspectionEnabled;
        settings.Units.NgCarrierTransfer = transferEnabled;
        using var services = CreateServices(settings);
        var io = services.GetRequiredService<VirtualIoService>();
        var work = services.GetRequiredService<InspectionWork>();
        var conveyor = services.GetRequiredService<MainConveyor>();
        var inspection = services.GetRequiredService<InspectionStation>();
        await services.GetRequiredService<MachineController>().InitializeAsync();
        io.SetInput(InputIo.InspectionCarrierPresent, true);
        io.SetInput(InputIo.InspectionHeatSink1Present, true);
        io.SetInput(InputIo.MainConveyorReadyFromRear, true);
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.InspectionBackupPlateUp, true);
        var assembly = work.Assembly(HeatSinkSlot.HeatSink1);
        assembly.RecordBoltPresence(1, true);
        assembly.CompleteInspection();
        work.Complete();

        Assert.Equal(expectNg, inspection.State([])
            == InspectionStationState.MovingTransferToCarrier);
        Assert.Equal(!expectNg, conveyor.State
            == MainConveyorState.DischargingInspectionCarrier);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedMotionStartFeedbackDoesNotBlockShutdown(bool jog)
    {
        var operations = new OperationCancellation();
        using var motion = new VirtualMotionService(
            new MotionSettings(),
            operations,
            hasY: false,
            hasZ: false);
        var failure = new InvalidOperationException("Motion feedback unavailable.");
        motion.Initialize();
        motion.StateChanged += () =>
        {
            if (motion.IsMoving)
            {
                throw failure;
            }
        };

        var actual = jog
            ? Assert.Throws<InvalidOperationException>(() => motion.JogX(1))
            : await Assert.ThrowsAsync<InvalidOperationException>(() =>
                motion.MoveXAsync(100, 1));

        Assert.Same(failure, actual);
        await operations.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(motion.IsMoving);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShutdownWaitsForFinalMotionFeedback(bool jog)
    {
        var operations = new OperationCancellation();
        using var motion = new VirtualMotionService(
            new MotionSettings(),
            operations,
            hasY: false,
            hasZ: false);
        using var releaseFeedback = new ManualResetEventSlim();
        var feedbackEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        motion.Initialize();
        motion.PositionChanged += (_, _, _) =>
        {
            if (!motion.IsMoving)
            {
                feedbackEntered.TrySetResult();
                releaseFeedback.Wait(TimeSpan.FromSeconds(5));
            }
        };

        Task? moving = null;
        if (jog)
        {
            motion.JogX(1);
        }
        else
        {
            moving = motion.MoveXAsync(100, 1);
        }

        var shutdown = Task.Run(() => operations.ShutdownAsync());
        try
        {
            await feedbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(motion.IsMoving);
            Assert.False(shutdown.IsCompleted);
        }
        finally
        {
            releaseFeedback.Set();
            await shutdown.WaitAsync(TimeSpan.FromSeconds(2));
            if (moving is not null)
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => moving);
            }
        }
    }

    [Fact]
    public async Task AdcOperationKeepsMachineLockedUntilStopFinishes()
    {
        var settings = new MachineSettings
        {
            Units = EnableOnly(MachineUnit.NgConveyor),
        };
        using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var head = new StoppingBoltHead();
        await machine.InitializeAsync();

        var testing = machine.RunAdcProtocolAsync(
            token => head.TightenAsync(token),
            CancellationToken.None);
        Assert.True(state.IsRunning);
        machine.Stop();
        await head.Stopping.Task.WaitAsync(TimeSpan.FromSeconds(2));
        io.SetInput(InputIo.AutoMode, true);
        Assert.True(state.IsRunning);
        Assert.False(machine.CanStart);
        Assert.False(machine.CanHome);
        Assert.False(machine.CanReset);
        await machine.StartAsync();
        Assert.False(state.AutomaticRunning);

        head.Stopped.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => testing);
        Assert.False(state.IsRunning);
        Assert.True(machine.CanStart);
    }

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
        var fastening = services.GetRequiredService<BoltFasteningWork>();
        var boltMotion = (VirtualMotionService)services
            .GetRequiredKeyedService<IXyMotion>(MotionGroup.BoltFastening);
        var fasteningCompletedAtSafeZ = false;
        fastening.Changed += () =>
        {
            if (fastening.Completed)
            {
                fasteningCompletedAtSafeZ |= boltMotion.IsAtHorizontalZ;
            }
        };
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
        await boltMotion.MoveZAsync(10, 20_000);
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
        Assert.True(fasteningCompletedAtSafeZ);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Station3ConfigurationRunsWithoutUpstreamHardware(bool missingBolts)
    {
        var settings = new MachineSettings
        {
            Units = new()
            {
                MainConveyor = true, Inspection = true, NgCarrierTransfer = true,
                NgShuttle = true, NgConveyor = true,
                PcbSupply = false, PcbPlacement = false, BoltFastening = false,
                PickupBoltFeeder = false, ShootingBoltFeeder = false,
            },
            Home = FastHome(),
            Drivers = new() { Inspection = InspectionAlgorithm.Virtual },
        };
        settings.InspectionGantry.Motion = FastMotion();
        settings.CarrierReference.UpperLeftLocatingPin = new() { X = 0, Y = 0 };
        settings.CarrierReference.LowerRightLocatingPin = new() { X = 100, Y = 0 };
        settings.NgCarrierTransfer.CarrierPickupPosition = new() { X = 20, Y = 20 };
        settings.NgCarrierTransfer.ShuttlePlacePosition = new() { X = 100, Y = 20 };
        settings.NgCarrierTransfer.Speed = 10_000;
        using var services = CreateServices(settings);
        var recipe = services.GetRequiredService<Recipe>();
        recipe.BoltFastening.BoltPoints =
        [
            new() { Number = 1, HeatSink = HeatSinkSlot.HeatSink1, X = 10, Y = 10 },
            new() { Number = 2, HeatSink = HeatSinkSlot.HeatSink2, X = 30, Y = 10 },
        ];
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var inspection = services.GetRequiredService<InspectionWork>();
        IMotionFeedback[] disabledMotions =
        [
            services.GetRequiredService<PcbSupplyHandler>().Feedback,
            services.GetRequiredService<PcbPlacementHandler>().Feedback,
            services.GetRequiredService<BoltFasteningGantry>().Feedback,
        ];
        var disabledOutputs = settings.PcbSupplyHardware.Outputs.Keys
            .Concat(settings.PcbPlacementHandlerHardware.Outputs.Keys)
            .Concat(settings.BoltFasteningHardware.Outputs.Keys)
            .Concat(settings.BoltFeederHardware.Outputs.Keys).ToHashSet();
        var unexpectedOutputs = new ConcurrentBag<OutputIo>();
        var adcFrames = 0;
        services.GetRequiredService<IAdcBus>().FrameTransferred += (_, _) =>
            Interlocked.Increment(ref adcFrames);
        io.OutputChanged += (output, value) =>
        {
            if (value && disabledOutputs.Contains(output)) unexpectedOutputs.Add(output);
        };
        var arrived = 0;
        var entered = false;
        var exited = false;
        io.InputChanged += (input, value) =>
        {
            if (value)
            {
                Interlocked.Or(ref arrived, input switch
                {
                    InputIo.PcbPlacementCarrierPresent => 1,
                    InputIo.BoltFasteningCarrierPresent => 2,
                    InputIo.InspectionCarrierPresent => 4,
                    _ => 0,
                });
            }
            if (input == InputIo.MainConveyorEntryCarrierDetected && value) entered = true;
            if (input == InputIo.MainConveyorAvailableFromFront2 && value && entered)
                io.SetInput(input, false);
            if (input == InputIo.MainConveyorExitCarrierDetected && !value) exited = true;
        };
        if (missingBolts)
        {
            var camera = services.GetRequiredService<VirtualCamera>();
            var image = camera.Capture();
            camera.SourceImage = image with { Pixels = new byte[image.Pixels.Length] };
        }

        await machine.InitializeAsync();
        Assert.True(machine.CanHome);
        await machine.HomeAsync(CancellationToken.None);
        io.SetInput(InputIo.MainConveyorReadyFromRear, false);
        io.SetInput(InputIo.AutoMode, true);
        Assert.True(machine.CanStart);
        var run = machine.StartAsync();
        try
        {
            Assert.True(await VirtualTest.WaitUntilAsync(
                () => inspection.Completed, TimeSpan.FromSeconds(10)));
            Assert.Equal(7, arrived);
            Assert.Equal(missingBolts, inspection.HasNg);
            Assert.All(inspection.Assemblies, assembly =>
            {
                Assert.Empty(assembly.PcbBoltResults);
                Assert.Empty(assembly.IpmSeatingResults);
                Assert.Empty(assembly.IpmFinalResults);
                Assert.Equal(!missingBolts, Assert.Single(assembly.BoltPresenceResults).Value);
            });
            if (missingBolts)
            {
                Assert.True(await VirtualTest.WaitUntilAsync(
                    () => io.GetInput(InputIo.NgConveyorPosition1Occupied)
                        && io.GetInput(InputIo.NgShuttleUp) && !io.GetOutput(OutputIo.NgConveyorRun),
                    TimeSpan.FromSeconds(5)));
                Assert.False(exited);
            }
            else
            {
                Assert.False(exited);
                io.SetInput(InputIo.MainConveyorReadyFromRear, true);
                await WaitUntilAsync(() => exited);
            }
        }
        finally
        {
            machine.Stop();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
        }

        Assert.Equal(MachineAlarm.None, state.Alarm);
        Assert.Empty(unexpectedOutputs);
        Assert.Equal(0, adcFrames);
        Assert.All(disabledMotions, motion => Assert.All(motion.Axes, axis =>
        {
            Assert.False(motion.GetAxisState(axis).ServoOn);
            Assert.False(motion.GetAxisState(axis).Homed);
        }));
    }

    [Fact]
    public async Task ManualJogFaultStopsTheMachineAndAllowsReset()
    {
        var settings = new MachineSettings
        {
            Units = EnableOnly(MachineUnit.NgCarrierTransfer),
            Home = FastHome(),
        };
        using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var gantry = services.GetRequiredService<InspectionGantry>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);

        var fault = new InvalidOperationException("Jog feedback failed.");
        var failed = 0;
        var reported = new TaskCompletionSource<Exception>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        gantry.Feedback.Faulted += error => reported.TrySetResult(error);
        gantry.Feedback.PositionChanged += (_, _, _) =>
        {
            if (gantry.Feedback.IsMoving && Interlocked.Exchange(ref failed, 1) == 0)
            {
                throw fault;
            }
        };
        io.SetOutput(OutputIo.NgConveyorRun, true);
        gantry.Jog(MotionAxis.X, 10);

        Assert.Same(fault, await reported.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(MachineAlarm.MotionUnavailable, state.Alarm);
        Assert.False(gantry.Feedback.IsMoving);
        Assert.False(io.GetOutput(OutputIo.NgConveyorRun));

        await machine.ResetAsync();
        Assert.Equal(MachineAlarm.None, state.Alarm);
        using var stopped = new CancellationTokenSource();
        gantry.Jog(MotionAxis.X, 10, stopped.Token);
        Assert.True(gantry.Feedback.IsMoving);
        stopped.Cancel();
        await WaitUntilAsync(() => !gantry.Feedback.IsMoving);
        Assert.Equal(MachineAlarm.None, state.Alarm);
    }

    [Fact]
    public async Task HomeRequiresEmptyEquipmentAndRaisedCylindersWithoutChangingOutputs()
    {
        var settings = FlowSettings();
        using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var manual = services.GetRequiredService<ManualHardwareViewModel>();
        await machine.InitializeAsync();
        Assert.True(machine.CanHome);
        Assert.False(state.ManualControlsEnabled);
        Assert.True(state.ManualOutputsEnabled);
        io.SetInput(InputIo.AutoMode, true);
        Assert.False(state.ManualOutputsEnabled);
        io.SetInput(InputIo.AutoMode, false);
        using (services.GetRequiredService<OperationCancellation>().Link())
            Assert.False(state.ManualOutputsEnabled);
        Assert.True(state.ManualOutputsEnabled);
        var outputsChanged = 0;
        io.OutputChanged += (_, _) => outputsChanged++;

        foreach (var input in new[]
        {
            InputIo.MainConveyorEntryCarrierDetected, InputIo.PcbPlacementCarrierPresent,
            InputIo.BoltFasteningCarrierPresent, InputIo.InspectionCarrierPresent,
            InputIo.MainConveyorExitCarrierDetected, InputIo.NgCarrierDetected,
            InputIo.NgShuttleCarrierDetected, InputIo.NgConveyorPosition1Occupied,
            InputIo.NgConveyorPosition2Occupied, InputIo.NgConveyorPosition3Occupied,
        })
        {
            io.SetInput(input, true);
            Assert.Equal(HomeBlockReason.CarrierDetected, machine.HomeBlock);
            Assert.False(machine.CanHome);
            Assert.All(manual.Axes, axis => Assert.False(manual.HomeAxisCommand.CanExecute(axis)));
            await machine.HomeAsync(CancellationToken.None);
            await manual.HomeAxisCommand.ExecuteAsync(manual.Axes[3]);
            io.SetInput(input, false);
        }

        foreach (var (up, down, reason) in new[]
        {
            (InputIo.PcbPlacementHandlerUp, InputIo.PcbPlacementHandlerDown, HomeBlockReason.PlacementNotRaised),
            (InputIo.PcbPlacementIpmUp, InputIo.PcbPlacementIpmDown, HomeBlockReason.PlacementNotRaised),
            (InputIo.BoltTableUp, InputIo.BoltTableDown, HomeBlockReason.FasteningNotRaised),
            (InputIo.PickupHeadUp, InputIo.PickupHeadDown, HomeBlockReason.FasteningNotRaised),
            (InputIo.ShootingHeadUp, InputIo.ShootingHeadDown, HomeBlockReason.FasteningNotRaised),
            (InputIo.NgCarrierPickupUp, InputIo.NgCarrierPickupDown, HomeBlockReason.NgPickupNotRaised),
        })
        {
            io.SetInput(up, false);
            Assert.Equal(reason, machine.HomeBlock);
            Assert.False(machine.CanHome);
            await machine.HomeAsync(CancellationToken.None);
            io.SetInput(down, true);
            io.SetInput(up, true);
            Assert.Equal(reason, machine.HomeBlock);
            io.SetInput(down, false);
            Assert.True(machine.CanHome);
        }
        Assert.Equal(0, outputsChanged);

        settings.Units.PcbSupply = settings.Units.PcbPlacement = settings.Units.BoltFastening = false;
        io.SetInput(InputIo.PcbPlacementHandlerUp, false);
        io.SetInput(InputIo.BoltTableUp, false);
        io.SetInput(InputIo.NgShuttleUp, false);
        io.SetInput(InputIo.NgShuttleDown, true);
        Assert.True(machine.CanHome);
        Assert.False(manual.HomeAxisCommand.CanExecute(manual.Axes[3]));
        Assert.True(manual.HomeAxisCommand.CanExecute(manual.Axes[9]));
        io.SetInput(InputIo.NgConveyorPosition1Occupied, true);
        Assert.False(machine.CanHome);
    }

    [Fact]
    public async Task RaiseCylindersPreparesHomeWithoutMovingAxesOrOtherActuators()
    {
        using var services = CreateServices(FlowSettings());
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        IIoService signals = io;
        await machine.InitializeAsync();
        OutputIo[] cylinders =
        [
            OutputIo.PcbPlacementHandlerDown, OutputIo.PcbPlacementIpmDown,
            OutputIo.BoltTableDown, OutputIo.PickupHeadDown,
            OutputIo.ShootingHeadDown, OutputIo.NgCarrierPickupDown,
        ];
        await Task.WhenAll(cylinders.Select(output => signals.SetOutputAndWaitAsync(output, true)));
        var motions = new[]
        {
            services.GetRequiredService<PcbSupplyHandler>().Feedback,
            services.GetRequiredService<PcbPlacementHandler>().Feedback,
            services.GetRequiredService<BoltFasteningGantry>().Feedback,
            services.GetRequiredService<InspectionGantry>().Feedback,
        };
        var moved = false;
        foreach (var motion in motions) motion.MovingChanged += moving => moved |= moving;
        var outputChanges = new ConcurrentQueue<OutputIo>();
        io.OutputChanged += (output, _) => outputChanges.Enqueue(output);

        io.SetInput(InputIo.NgConveyorPosition3Occupied, true);
        Assert.False(machine.CanRaiseCylinders);
        await machine.RaiseCylindersAsync(CancellationToken.None);
        Assert.Empty(outputChanges);
        io.SetInput(InputIo.NgConveyorPosition3Occupied, false);
        Assert.True(machine.CanRaiseCylinders);
        Assert.False(machine.CanHome);

        var raising = machine.RaiseCylindersAsync(CancellationToken.None);
        Assert.True(state.IsRunning);
        Assert.False(state.IsHoming);
        Assert.False(state.ManualOutputsEnabled);
        Assert.False(machine.CanHome);
        await raising;
        Assert.True(machine.CanHome);
        Assert.False(state.Homed);
        Assert.False(moved);
        Assert.Equal(cylinders.Order(), outputChanges.Order());
        Assert.All(cylinders, output => Assert.False(io.GetOutput(output)));
        await machine.RaiseCylindersAsync(CancellationToken.None);
        Assert.True(machine.CanHome);
    }

    [Fact]
    public async Task RaiseCylindersUsesEnabledUnitsAndKeepsOutputsOnStopOrTimeout()
    {
        var settings = new MachineSettings
        {
            Units = EnableOnly(MachineUnit.NgCarrierTransfer),
            Home = FastHome(),
        };
        settings.Options.TimeoutMilliseconds = 100;
        using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        io.AutoResponseEnabled = false;
        io.SetOutput(OutputIo.NgCarrierPickupDown, true);
        io.SetOutput(OutputIo.PcbPlacementHandlerDown, true);
        io.SetInput(InputIo.NgCarrierPickupUp, false);
        io.SetInput(InputIo.NgCarrierPickupDown, true);
        var raising = machine.RaiseCylindersAsync(CancellationToken.None);
        machine.Stop();
        await raising;
        Assert.Equal(MachineAlarm.None, state.Alarm);
        Assert.False(io.GetOutput(OutputIo.NgCarrierPickupDown));
        Assert.True(io.GetOutput(OutputIo.PcbPlacementHandlerDown));
        Assert.False(machine.CanHome);

        await machine.RaiseCylindersAsync(CancellationToken.None);
        Assert.Equal(MachineAlarm.NgCarrierTransfer, state.Alarm);
        Assert.False(io.GetOutput(OutputIo.NgCarrierPickupDown));
        Assert.False(state.IsRunning);
        Assert.False(state.Homed);
    }

    [Fact]
    public async Task InspectionHomeRequiresReleasedCarrierAndRaisedPickupBeforeXy()
    {
        var settings = new MachineSettings
        {
            Units = EnableOnly(MachineUnit.NgCarrierTransfer),
            Home = FastHome(),
        };
        using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var gantry = services.GetRequiredService<InspectionGantry>();
        IIoService signals = io;
        await machine.InitializeAsync();
        await signals.SetOutputAndWaitAsync(OutputIo.NgCarrierGripperClose, true);
        await signals.SetOutputAndWaitAsync(OutputIo.NgCarrierPickupDown, true);
        io.SetInput(InputIo.NgCarrierDetected, true);

        Assert.False(machine.CanHome);
        await machine.HomeAsync(CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            gantry.HomeAxisAsync(MotionAxis.X, 100));
        Assert.True(io.GetOutput(OutputIo.NgCarrierGripperClose));
        Assert.False(state.Homed);

        io.SetInput(InputIo.NgCarrierDetected, false);
        Assert.False(machine.CanHome);
        Assert.False(gantry.CanMove);
        await machine.HomeAsync(CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            gantry.HomeAxisAsync(MotionAxis.X, 100));
        Assert.False(state.Homed);
        Assert.True(io.GetOutput(OutputIo.NgCarrierPickupDown));
        Assert.True(io.GetOutput(OutputIo.NgCarrierGripperClose));

        await signals.SetOutputAndWaitAsync(OutputIo.NgCarrierGripperClose, false);
        await signals.SetOutputAndWaitAsync(OutputIo.NgCarrierPickupDown, false);
        Assert.True(machine.CanHome);

        var unsafeMovement = false;
        gantry.Feedback.MovingChanged += moving =>
        {
            if (moving && state.IsHoming)
            {
                unsafeMovement |= !gantry.CanMove || !gantry.CanHome
                    || !io.GetInput(InputIo.NgCarrierGripperOpen);
            }
        };
        await machine.HomeAsync(CancellationToken.None);
        Assert.True(state.Homed);
        Assert.False(unsafeMovement);
        Assert.True(gantry.CanMove);

        io.SetInput(InputIo.NgCarrierPickupUp, false);
        io.SetInput(InputIo.NgCarrierPickupDown, true);
        Assert.Throws<InvalidOperationException>(() =>
            gantry.Jog(MotionAxis.X, 10));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await gantry.MoveToAsync(new AxisPosition { X = 20, Y = 10 }, 100));
        io.SetInput(InputIo.NgCarrierPickupDown, false);
        io.SetInput(InputIo.NgCarrierPickupUp, true);
        var moving = gantry.MoveToAsync(new AxisPosition { X = 20, Y = 10 }, 10);
        await WaitUntilAsync(() => gantry.Feedback.IsMoving);
        io.SetInput(InputIo.NgCarrierPickupUp, false);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => moving);
        Assert.False(gantry.Feedback.IsMoving);
        Assert.Equal(MachineAlarm.NgCarrierTransfer, state.Alarm);

        io.SetInput(InputIo.NgCarrierPickupUp, true);
        await machine.ResetAsync();
        await gantry.MoveToAsync(new AxisPosition { X = 20, Y = 10 }, 1000);
        Assert.Equal(MachineAlarm.None, state.Alarm);
    }

    [Fact]
    public void UnitSettingsRemainLiveAfterComposition()
    {
        var settings = new MachineSettings
        {
            Units = EnableOnly(MachineUnit.NgConveyor),
        };
        using var services = CreateServices(settings);
        var state = services.GetRequiredService<MachineState>();

        Assert.Same(
            settings.Units,
            services.GetRequiredService<UnitSettings>());
        Assert.True(state.Homed);

        settings.Units.NgCarrierTransfer = true;
        Assert.False(state.Homed);

        settings.Units.NgCarrierTransfer = false;
        Assert.True(state.Homed);
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
                Drivers = new()
                {
                    Inspection = InspectionAlgorithm.Virtual,
                },
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

            if (unit == MachineUnit.MainConveyor)
            {
                io.SetInput(InputIo.NgCarrierPickupUp, false);
                io.SetInput(InputIo.NgCarrierPickupDown, true);
                io.SetInput(InputIo.PcbPlacementCarrierPresent, true);
                io.SetInput(InputIo.BoltFasteningCarrierPresent, true);
                await ((IIoService)io).SetOutputAndWaitAsync(
                    OutputIo.PcbPlacementBackupPlateUp,
                    true);
                await ((IIoService)io).SetOutputAndWaitAsync(
                    OutputIo.BoltFasteningBackupPlateUp,
                    true);
                Assert.False(services.GetRequiredService<InspectionWork>().CanReceive);
            }

            io.SetInput(InputIo.AutoMode, true);
            Assert.True(machine.CanStart);

            var run = machine.StartAsync();
            await WaitUntilAsync(() => state.AutomaticRunning);
            if (unit == MachineUnit.MainConveyor)
            {
                Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
                Assert.False(io.GetInput(InputIo.InspectionCarrierPresent));
                io.SetInput(InputIo.NgCarrierPickupDown, false);
                io.SetInput(InputIo.NgCarrierPickupUp, true);
                await ((IIoService)io).WaitForInputAsync(
                    InputIo.InspectionCarrierPresent,
                    true);
                var gantry = services.GetRequiredService<InspectionGantry>();
                Assert.False(gantry.Feedback.GetAxisState(MotionAxis.X).ServoOn);
                Assert.False(gantry.Feedback.GetAxisState(MotionAxis.X).Homed);
            }

            machine.Stop();
            await run.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(MachineAlarm.None, state.Alarm);
            Assert.False(state.IsRunning);
        }
    }

    [Fact]
    public async Task DisabledTransferStillBlocksShuttleUntilPickupIsRaised()
    {
        var settings = new MachineSettings
        {
            Units = EnableOnly(MachineUnit.NgShuttle),
        };
        using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<VirtualIoService>();
        var shuttle = services.GetRequiredService<NgShuttle>();
        var gantry = services.GetRequiredService<InspectionGantry>();
        await machine.InitializeAsync();
        io.SetInput(InputIo.NgCarrierPickupUp, false);
        io.SetInput(InputIo.NgCarrierPickupDown, true);
        io.SetInput(InputIo.NgCarrierGripperOpen, false);
        io.SetInput(InputIo.NgCarrierGripperClosed, true);
        io.SetInput(InputIo.NgCarrierDetected, true);
        io.SetInput(InputIo.NgShuttleCarrierDetected, true);
        io.SetInput(InputIo.AutoMode, true);

        var run = machine.StartAsync();
        try
        {
            Assert.Equal(NgShuttleState.WaitingForCarrierPickupUp, shuttle.State);
            Assert.False(io.GetOutput(OutputIo.NgShuttleDown));
            Assert.False(gantry.Feedback.GetAxisState(MotionAxis.X).ServoOn);
            Assert.False(gantry.Feedback.GetAxisState(MotionAxis.X).Homed);

            io.SetInput(InputIo.NgCarrierGripperClosed, false);
            io.SetInput(InputIo.NgCarrierGripperOpen, true);
            Assert.Equal(NgShuttleState.WaitingForCarrierPickupUp, shuttle.State);
            Assert.False(io.GetOutput(OutputIo.NgShuttleDown));

            io.SetInput(InputIo.NgCarrierPickupDown, false);
            io.SetInput(InputIo.NgCarrierPickupUp, true);
            await ((IIoService)io).WaitForInputAsync(InputIo.NgShuttleDown, true);
            Assert.True(io.GetInput(InputIo.NgCarrierDetected));
            Assert.False(gantry.Feedback.GetAxisState(MotionAxis.X).ServoOn);
            Assert.False(gantry.Feedback.GetAxisState(MotionAxis.X).Homed);
        }
        finally
        {
            machine.Stop();
            await run;
        }
    }

    [Fact]
    public async Task AutomaticStartWaitsForCanceledManualScopeToFinish()
    {
        using var services = CreateServices(new MachineSettings
        {
            Units = EnableOnly(MachineUnit.MainConveyor),
        });
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var operations = services.GetRequiredService<OperationCancellation>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        io.SetInput(InputIo.AutoMode, true);
        Assert.True(machine.CanStart);

        using var manual = operations.Link();
        Assert.True(state.IsRunning);
        Assert.False(machine.CanStart);
        await machine.StartAsync();
        Assert.False(state.AutomaticRunning);

        machine.Stop();
        Assert.True(manual.IsCancellationRequested);
        Assert.True(state.IsRunning);
        Assert.False(machine.CanStart);
        manual.Dispose();
        Assert.False(state.IsRunning);
        Assert.True(machine.CanStart);
    }

    [Fact]
    public async Task StopDuringHardwareReadinessPreventsStartAndAllowsRestart()
    {
        var settings = new MachineSettings
        {
            Home = FastHome(),
            Units = EnableOnly(MachineUnit.BoltFastening),
        };
        var head = new WaitingBoltHead();
        using var services = new ServiceCollection()
            .AddSingleton<MachineStore>()
            .AddIbtmApplication(settings)
            .AddKeyedSingleton<IBoltHead>(FasteningHead.Shooting, head)
            .AddKeyedSingleton<IBoltHead>(FasteningHead.Pickup, head)
            .BuildServiceProvider();
        PrepareCarrierTeaching(settings, services.GetRequiredService<Recipe>());
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();

        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        io.SetInput(InputIo.AutoMode, true);
        head.WaitForReadiness = true;
        var readinessChecks = head.ReadinessChecks;

        var starting = machine.StartAsync();
        await head.ReadinessEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(state.AutomaticRunning);
        Assert.False(machine.CanStart);
        Assert.False(machine.CanHome);
        await machine.StartAsync().WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(readinessChecks + 1, head.ReadinessChecks);

        machine.Stop();
        await starting.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(state.IsRunning);
        Assert.Equal(MachineAlarm.None, state.Alarm);

        head.ReadinessReleased.TrySetResult();
        var resumed = machine.StartAsync();
        await WaitUntilAsync(() => state.AutomaticRunning);
        machine.Stop();
        await resumed.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(state.IsRunning);
        Assert.Equal(MachineAlarm.None, state.Alarm);
    }

    [Fact]
    public async Task StopDuringFirstUnitOutputCancelsAllStartingUnits()
    {
        var settings = new MachineSettings
        {
            Units = EnableOnly(MachineUnit.MainConveyor),
        };
        settings.Units.ShootingBoltFeeder = true;
        using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        io.SetInput(InputIo.AutoMode, true);
        var stopped = false;
        io.OutputChanged += (output, value) =>
        {
            if (output == OutputIo.MainConveyorReadyToFront2 && value)
            {
                stopped = true;
                machine.Stop();
            }
        };

        await machine.StartAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(stopped);
        Assert.False(state.IsRunning);
        Assert.Equal(MachineAlarm.None, state.Alarm);
        Assert.False(io.GetOutput(OutputIo.ShootingFeederRunSignal));
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
        io.SetInput(InputIo.ResetButton, true);
        await WaitUntilAsync(() => state.Alarm == MachineAlarm.None);
        io.SetInput(InputIo.ResetButton, false);

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

    [Theory]
    [InlineData(InputIo.NgConveyorPosition2Occupied, true)]
    [InlineData(InputIo.BoltTableUp, false)]
    public async Task HomeStopsWhenItsCarrierOrCylinderConditionChanges(InputIo input, bool value)
    {
        var settings = FlowSettings();
        settings.Home.ZSpeed = 20;
        using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var placement = services.GetRequiredService<PcbPlacementHandler>();
        var fastening = services.GetRequiredService<BoltFasteningGantry>();
        await machine.InitializeAsync();
        await Task.WhenAll(placement.MoveZAsync(50), fastening.MoveZAsync(50));
        var homing = machine.HomeAsync(CancellationToken.None);
        await WaitUntilAsync(() => placement.Feedback.IsMoving && fastening.Feedback.IsMoving);
        Assert.False(state.ManualOutputsEnabled);
        io.SetInput(input, value);
        await homing.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(state.IsHoming);
        Assert.False(placement.Feedback.IsMoving);
        Assert.False(fastening.Feedback.IsMoving);
        Assert.False(placement.Feedback.GetAxisState(MotionAxis.Z).Homed);
        Assert.False(machine.CanHome);
    }

    [Fact]
    public async Task MotionAlarmBlocksHomeAndStopsAllHomingAxes()
    {
        var settings = FlowSettings();
        settings.Home.ZSpeed = 20;
        using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var placement = (VirtualMotionService)services
            .GetRequiredKeyedService<IXyMotion>(MotionGroup.PcbPlacementHandler);
        var fastening = (VirtualMotionService)services
            .GetRequiredKeyedService<IXyMotion>(MotionGroup.BoltFastening);
        await machine.InitializeAsync();

        fastening.SetAlarm(MotionAxis.X, true);
        Assert.False(machine.CanHome);
        await machine.ResetAsync();
        Assert.True(machine.CanHome);

        await Task.WhenAll(
            placement.MoveZAsync(50, 10_000),
            fastening.MoveZAsync(50, 10_000));
        var homing = machine.HomeAsync(CancellationToken.None);
        await WaitUntilAsync(() => placement.IsMoving && fastening.IsMoving);
        fastening.SetAlarm(MotionAxis.X, true);
        await homing.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(MachineAlarm.MotionUnavailable, state.Alarm);
        Assert.False(state.IsHoming);
        Assert.False(state.IsRunning);
        Assert.False(placement.IsMoving);
        Assert.False(fastening.IsMoving);
        Assert.False(placement.GetAxisState(MotionAxis.Z).Homed);
        Assert.False(fastening.GetAxisState(MotionAxis.Z).Homed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedHomeStopsOtherHomingAxesImmediately(bool exception)
    {
        var settings = FlowSettings();
        settings.Home.ZSpeed = 1;
        HomeResultMotion? homeResult = null;
        using var services = new ServiceCollection()
            .AddSingleton<MachineStore>()
            .AddIbtmApplication(settings)
            .AddSingleton(provider =>
            {
                var motion = DispatchProxy.Create<IXyMotion, HomeResultMotion>();
                homeResult = (HomeResultMotion)motion;
                homeResult.Motion = provider.GetRequiredKeyedService<IXyMotion>(
                    MotionGroup.BoltFastening);
                return new BoltFasteningGantry(
                    provider.GetRequiredKeyedService<IBoltHead>(FasteningHead.Shooting),
                    provider.GetRequiredKeyedService<IBoltHead>(FasteningHead.Pickup),
                    provider.GetRequiredService<IIoService>(),
                    motion,
                    settings.BoltFastening,
                    settings.CarrierReference);
            })
            .BuildServiceProvider();
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var placement = services.GetRequiredKeyedService<IXyMotion>(
            MotionGroup.PcbPlacementHandler);
        var supply = services.GetRequiredKeyedService<IAxisMotion>(
            MotionGroup.PcbSupply);
        await machine.InitializeAsync();
        await placement.MoveZAsync(50, 10_000);

        var homing = machine.HomeAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => placement.IsMoving && supply.IsMoving);
            if (exception)
            {
                homeResult!.Result.SetException(new InvalidOperationException("Home command failed."));
            }
            else
            {
                homeResult!.Result.SetResult(false);
            }
            await homing.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(MachineAlarm.HomeFailed, state.Alarm);
            Assert.False(state.IsHoming);
            Assert.False(placement.IsMoving);
            Assert.False(supply.IsMoving);
            Assert.False(placement.GetAxisState(MotionAxis.Z).Homed);
            Assert.Equal(0, homeResult.HorizontalHomeCalls);
        }
        finally
        {
            machine.Stop();
            await homing;
        }
    }

    public class HomeResultMotion : DispatchProxy
    {
        public IXyMotion Motion { get; set; } = null!;
        public TaskCompletionSource<bool> Result { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public int HorizontalHomeCalls { get; private set; }

        protected override object? Invoke(MethodInfo? method, object?[]? arguments)
        {
            if (method!.Name == nameof(IAxisMotion.HomeAsync)
                && (MotionAxis)arguments![0]! == MotionAxis.Z)
            {
                return Result.Task.WaitAsync((CancellationToken)arguments[2]!);
            }

            if (method.Name == nameof(IXyMotion.HomeHorizontalAsync))
            {
                HorizontalHomeCalls++;
            }

            return method.Invoke(Motion, arguments);
        }
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

    private sealed class StoppingBoltHead : IBoltHead
    {
        public TaskCompletionSource Stopping { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Stopped { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public BoltHeadState State => BoltHeadState.Ready;
        public Task CheckReadyAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SelectPresetAsync(ushort preset, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void DiscardPendingResult() { }

        public async Task<BoltResult> TightenAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return new(true, 1);
            }
            finally
            {
                Stopping.SetResult();
                await Stopped.Task;
            }
        }
    }

    private sealed class WaitingBoltHead : IBoltHead
    {
        public bool WaitForReadiness { get; set; }
        public int ReadinessChecks { get; private set; }
        public TaskCompletionSource ReadinessEntered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReadinessReleased { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public BoltHeadState State => BoltHeadState.Ready;

        public Task CheckReadyAsync(CancellationToken cancellationToken = default)
        {
            ReadinessChecks++;
            if (!WaitForReadiness)
            {
                return Task.CompletedTask;
            }

            ReadinessEntered.TrySetResult();
            return ReadinessReleased.Task.WaitAsync(cancellationToken);
        }

        public Task SelectPresetAsync(
            ushort preset,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<BoltResult> TightenAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public void DiscardPendingResult()
        {
        }
    }

    private static ServiceProvider CreateServices(MachineSettings settings)
        => new ServiceCollection()
            .AddSingleton<MachineStore>()
            .AddIbtmApplication(settings)
            .BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true,
            });

    private static UnitSettings EnableOnly(MachineUnit unit) => new()
    {
        MainConveyor = unit == MachineUnit.MainConveyor,
        PcbSupply = unit == MachineUnit.PcbSupply,
        PcbPlacement = unit == MachineUnit.PcbPlacement,
        PickupBoltFeeder = unit == MachineUnit.PickupBoltFeeder,
        ShootingBoltFeeder = unit == MachineUnit.ShootingBoltFeeder,
        BoltFastening = unit == MachineUnit.BoltFastening,
        Inspection = unit == MachineUnit.Inspection,
        NgCarrierTransfer = unit == MachineUnit.NgCarrierTransfer,
        NgShuttle = unit == MachineUnit.NgShuttle,
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
            Drivers = new() { Inspection = InspectionAlgorithm.Virtual },
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
        settings.NgCarrierTransfer.Speed = 10_000;
        settings.NgCarrierTransfer.CarrierPickupPosition = new() { X = 20, Y = 20 };
        settings.NgCarrierTransfer.ShuttlePlacePosition = new() { X = 150, Y = 20 };
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
        NgCarrierTransfer,
        NgShuttle,
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
