using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using IBTM.BoltFastening;
using IBTM.Core;
using IBTM.Conveyor;
using IBTM.Device;
using IBTM.Inspection;
using IBTM.Inspection.Training;
using IBTM.NgConveyor;
using IBTM.PcbBuffer;
using IBTM.PcbPlacement;
using IBTM.PcbSupply;
using IBTM.Storage;
using IBTM.UI;
using IBTM.Virtual;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IBTM.Virtual.Tests;

public sealed partial class MachineLifecycleTests
{
    [Fact]
    public async Task DataMatrixFailureStopsInspectionAndResetAllowsARealRead()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.Inspection);
        using var services = CreateServices(settings);
        var recipe = services.GetRequiredService<Recipe>();
        recipe.Pcb.BoltPoints = [new() { Number = 1, X = 10, Y = 10 }];
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var work = services.GetRequiredService<InspectionWork>();
        var camera = services.GetRequiredService<VirtualCamera>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);

        var teaching = services.GetRequiredService<StationTeachingViewModel>();
        teaching.SelectedPcb = HeatSinkSlot.HeatSink2;
        teaching.SelectedPoint = teaching.FilteredPoints.Single(point => point.Target == TeachingTarget.DataMatrix);
        await WaitUntilAsync(() => teaching.CaptureInspectionCommand.CanExecute(null));
        await teaching.CaptureInspectionCommand.ExecuteAsync(null);
        Assert.Null(teaching.CameraError);
        Assert.Equal("PCB-2", teaching.Preview.Result);
        Assert.True(services.GetRequiredService<BoltInspector>().IsAtBarcode(HeatSinkSlot.HeatSink2));

        var frame = camera.Capture(500, 0);
        camera.SourceImage = frame with { Pixels = new byte[frame.Pixels.Length] };
        io.SetInput(InputIo.InspectionCarrierPresent, true);
        io.SetInput(InputIo.InspectionBackupPlateUp, true);
        io.SetInput(InputIo.InspectionBackupPlateDown, false);
        io.SetInput(InputIo.InspectionHeatSink1Present, true);
        io.SetInput(InputIo.AutoMode, false);
        Assert.True(work.CarrierSeated);
        Assert.True(machine.CanStart);
        using var failureStop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await machine.StartAsync(failureStop.Token);
        Assert.Equal(MachineAlarm.Inspection, state.Alarm);
        Assert.Contains("Data Matrix", state.AlarmDetail);
        Assert.StartsWith("Data Matrix could not be read", state.AlarmMessage);
        Assert.False(work.Completed);
        Assert.Null(work.Assembly(HeatSinkSlot.HeatSink1).PcbBarcode);
        Assert.Empty(work.Assembly(HeatSinkSlot.HeatSink1).BoltPresenceResults);
        Assert.False(services.GetRequiredService<InspectionGantry>().Feedback.IsMoving);

        camera.SourceImage = null;
        await machine.ResetAsync();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var run = machine.StartAsync(stop.Token);
        try
        {
            Assert.True(await VirtualTest.WaitUntilAsync(() => work.Completed, TimeSpan.FromSeconds(2)));
            Assert.Equal("PCB-1", work.Assembly(HeatSinkSlot.HeatSink1).PcbBarcode);
            Assert.Single(work.Assembly(HeatSinkSlot.HeatSink1).BoltPresenceResults);
            Assert.Equal(MachineAlarm.None, state.Alarm);
            Assert.Null(state.AlarmMessage);
        }
        finally
        {
            stop.Cancel();
            await run;
        }
    }

    [Fact]
    public async Task ManualShootingHoldsOnlyAirAndStopsOnReleaseNavigationOrStop()
    {
        using var services = CreateServices(FlowSettings());
        var machine = services.GetRequiredService<MachineController>();
        var teaching = services.GetRequiredService<StationTeachingViewModel>();
        var io = services.GetRequiredService<VirtualIoService>();
        var state = services.GetRequiredService<MachineState>();
        await machine.InitializeAsync();
        io.AutoResponseEnabled = false;
        io.SetInput(InputIo.ShootingTubeBoltDetected, true);
        io.SetInput(InputIo.ShootingHeadVacuumDetected, false);
        teaching.SelectedMotionGroup = MotionGroup.BoltFastening;
        var shoot = teaching.TeachingOutputs[OutputIo.ShootBolt];
        var outputs = new List<OutputIo>();
        io.OutputChanged += (output, _) => outputs.Add(output);
        Assert.True(shoot.HoldToRun);
        Assert.False(shoot.RequiresHandler);

        Action[] stopActions =
        [
            () => teaching.SetOutputOnCancelCommand.Execute(null),
            () => teaching.SelectedMotionGroup = MotionGroup.InspectionGantry,
            machine.Stop,
            teaching.Deactivate,
            () => io.SetInput(InputIo.AutoMode, false),
        ];
        foreach (var stop in stopActions)
        {
            teaching.SelectedMotionGroup = MotionGroup.BoltFastening;
            await WaitUntilAsync(() => teaching.SetOutputOnCommand.CanExecute(shoot));
            var holding = teaching.SetOutputOnCommand.ExecuteAsync(shoot);
            Assert.True(io.GetOutput(OutputIo.ShootBolt));
            Assert.False(holding.IsCompleted);
            Assert.False(state.ManualSetupEnabled);
            Assert.False(machine.CanStart);
            stop();
            await holding.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(io.GetOutput(OutputIo.ShootBolt));
            io.SetInput(InputIo.AutoMode, true);
        }
        Assert.All(outputs, output => Assert.Equal(OutputIo.ShootBolt, output));
        Assert.Equal(MachineAlarm.None, state.Alarm);
        io.SetInput(InputIo.AutoMode, false);
        await WaitUntilAsync(() => !teaching.SetOutputOnCommand.CanExecute(shoot));
    }

    [Fact]
    public async Task CarrierScanKeepsSelectionChangedAfterItsImagesWereSaved()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.Inspection);
        settings.CarrierReference.LowerRightLocatingPin = new() { X = 10, Y = 10 };
        using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var teaching = services.GetRequiredService<StationTeachingViewModel>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        teaching.RecipeEditor.Name = $"SelectionAfterSave-{Guid.NewGuid():N}";
        var next = teaching.FilteredPoints.Single(point => point.Target == TeachingTarget.CarrierLowerRightLocatingPin);
        teaching.RecipeEditor.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(RecipeEditor.ActiveName)) teaching.SelectedPoint = next;
        };

        await WaitUntilAsync(() => teaching.CaptureCarrierImagesCommand.CanExecute(null));
        await teaching.CaptureCarrierImagesCommand.ExecuteAsync(null);

        Assert.Same(next, teaching.SelectedPoint);
        Assert.True(teaching.HasCarrierImages);
        var saved = await services.GetRequiredService<RecipeStore>().LoadRecipeAsync(teaching.RecipeEditor.ActiveName);
        Assert.Equal(teaching.CarrierImages.Count, saved.CarrierImages.Count);
        Assert.Null(teaching.CameraError);
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
        io.SetInput(InputIo.AutoMode, false);
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

        if (lift != NgTransferLiftState.Up)
            await VerifyDryRunReleaseAsync(NgTransferState.Raising);

        // The carrier may leave the pickup sensor before the gripper reaches Open.
        io.SetInput(InputIo.NgCarrierDetected, false);
        io.SetInput(InputIo.NgCarrierPickupUp, false);
        io.SetInput(InputIo.NgCarrierPickupDown, true);
        io.SetInput(InputIo.NgCarrierGripperOpen, false);
        io.SetInput(InputIo.NgCarrierGripperClosed, false);
        Assert.Equal(InspectionStationState.OpeningTransferGripper, station.State([]));
        await VerifyDryRunReleaseAsync(NgTransferState.Opening);

        async Task VerifyDryRunReleaseAsync(NgTransferState expected)
        {
            var dryRun = services.GetRequiredService<NgTransferDryRun>();
            using var dryRunStop = new CancellationTokenSource();
            var dryRunTask = dryRun.RunAsync(dryRunStop.Token);
            try
            {
                Assert.Equal(NgTransferDestination.Shuttle, dryRun.Destination);
                Assert.Equal(expected, dryRun.State);
                Assert.False(io.GetOutput(OutputIo.NgCarrierGripperClose));
            }
            finally
            {
                dryRunStop.Cancel();
                await dryRunTask;
            }
        }
    }

    [Fact]
    public async Task MissingInspectionModelAllowsSetupButBlocksAutomaticStart()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.Inspection);
        settings.Drivers.Inspection = InspectionAlgorithm.TinyUnet;
        using var services = new ServiceCollection()
            .AddSingleton(_ => VirtualTest.OpenMachineStore())
            .AddIbtmApplication(settings, new Recipe { Pcb = VirtualTest.TaughtPcbLayout() })
            .AddSingleton(new BoltTrainingStore(Path.Combine(
                Path.GetTempPath(), $"IBTM-empty-training-{Guid.NewGuid():N}.db")))
            .BuildServiceProvider();
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        services.GetRequiredService<Recipe>().Pcb.BoltPoints.Add(
            new BoltPoint { Number = 1, X = 10, Y = 10 });

        await machine.InitializeAsync();
        Assert.Equal(MachineAlarm.None, state.Alarm);
        Assert.True(machine.CanHome);
        await machine.HomeAsync(CancellationToken.None);
        Assert.True(state.ManualControlsEnabled);

        io.SetInput(InputIo.AutoMode, false);
        Assert.True(machine.CanStart);
        await machine.StartAsync();
        Assert.Equal(MachineAlarm.Inspection, state.Alarm);
        Assert.False(state.IsRunning);

        io.SetInput(InputIo.AutoMode, true);
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
        io.SetInput(InputIo.AutoMode, false);
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

    [Fact]
    public async Task AutomaticAlarmWaitsForHeadStopAndKeepsFirstCause()
    {
        var settings = new MachineSettings
        {
            Units = EnableOnly(MachineUnit.BoltFastening),
        };
        FastHomes(settings);
        var head = new StoppingBoltHead();
        using var services = new ServiceCollection()
            .AddSingleton(_ => VirtualTest.OpenMachineStore())
            .AddIbtmApplication(settings)
            .AddKeyedSingleton<IBoltHead>(FasteningHead.Shooting, head)
            .BuildServiceProvider();
        PrepareCarrierTeaching(settings, services.GetRequiredService<Recipe>());
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();

        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        io.SetInput(InputIo.BoltFasteningCarrierPresent, true);
        io.SetInput(InputIo.BoltFasteningHeatSink1Present, true);
        io.SetInput(InputIo.BoltFasteningBackupPlateUp, true);
        io.SetInput(InputIo.BoltFasteningBackupPlateDown, false);
        io.SetInput(InputIo.ShootingHeadVacuumDetected, true);
        io.SetInput(InputIo.AutoMode, false);

        var run = machine.StartAsync();
        await head.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        io.SetInput(InputIo.AirPressureHigh, false);
        await head.Stopping.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(run.IsCompleted);
        Assert.True(state.AutomaticRunning);
        Assert.True(state.IsRunning);
        Assert.False(machine.CanReset);
        Assert.Equal(MachineAlarm.AirPressureLow, state.Alarm);

        head.Stopped.SetException(new InvalidOperationException("Head stop failed."));
        await run.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(state.IsRunning);
        Assert.Equal(MachineAlarm.AirPressureLow, state.Alarm);
        Assert.Null(state.AlarmMessage);
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
        io.SetInput(InputIo.AutoMode, false);

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
        io.SetInput(InputIo.AutoMode, false);
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
            Units = EnableOnly(MachineUnit.BoltFastening),
        };
        FastHomes(settings);
        var head = new WaitingBoltHead();
        using var services = new ServiceCollection()
            .AddSingleton(_ => VirtualTest.OpenMachineStore())
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
        io.SetInput(InputIo.AutoMode, false);
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

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task StopOrFailureDuringFirstUnitOutputPreventsLaterStarts(bool failure, bool stopBeforeFailure)
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
        io.SetInput(InputIo.AutoMode, false);
        io.SetInput(InputIo.MainConveyorAvailableFromFront2, false);
        var stopped = false;
        var feederStarted = false;
        var error = new InvalidOperationException("Conveyor start failed.");
        io.OutputChanged += (output, value) =>
        {
            feederStarted |= output == OutputIo.ShootingFeederRunSignal && value;
            if (output == OutputIo.MainConveyorReadyToFront2 && value)
            {
                stopped = true;
                if (!failure || stopBeforeFailure) machine.Stop();
                if (failure) throw error;
            }
        };

        await machine.StartAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(stopped);
        Assert.False(feederStarted);
        Assert.False(state.IsRunning);
        Assert.Equal(failure ? MachineAlarm.MainConveyor : MachineAlarm.None, state.Alarm);
        Assert.Equal(failure ? error.Message : null, state.AlarmMessage);
        Assert.False(services.GetRequiredService<OperationCancellation>().HasActiveOperations);
        Assert.False(io.GetOutput(OutputIo.ShootingFeederRunSignal));
    }

    [Fact]
    public async Task UnitFailureDuringSafetyStopKeepsFirstAlarmAndLogsFailure()
    {
        var settings = new MachineSettings { Units = EnableOnly(MachineUnit.MainConveyor) };
        using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var log = services.GetRequiredService<ApplicationLog>();
        await machine.InitializeAsync();
        io.SetInput(InputIo.AutoMode, false);
        io.SetInput(InputIo.MainConveyorAvailableFromFront2, false);
        var failure = new IOException("Conveyor cleanup failed after the air pressure trip.");
        io.OutputChanged += (output, on) =>
        {
            if (output != OutputIo.MainConveyorReadyToFront2 || !on) return;
            io.SetInput(InputIo.AirPressureHigh, false);
            throw failure;
        };

        await machine.StartAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(MachineAlarm.AirPressureLow, state.Alarm);
        Assert.False(state.IsRunning);
        Assert.False(services.GetRequiredService<OperationCancellation>().HasActiveOperations);
        var entry = Assert.Single(log.ReadAfter(0), entry => entry.Detail == failure.ToString());
        Assert.Contains("Automatic unit MainConveyor", entry.Message);
        Assert.Contains("AirPressureLow", entry.Message);
    }

    [Fact]
    public async Task DoorTripStopsAndResetsFromLiveHardwareState()
    {
        var settings = new MachineSettings
        {
            Units = EnableOnly(MachineUnit.BoltFastening),
        };
        FastHomes(settings);
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

        io.SetInput(InputIo.AutoMode, false);
        Assert.True(machine.CanStart);
        var firstRun = machine.StartAsync();
        await WaitUntilAsync(() => state.AutomaticRunning);

        io.SetInput(InputIo.Door1Open, false);
        await firstRun.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(MachineAlarm.DoorOpen, state.Alarm);
        Assert.False(state.IsRunning);
        Assert.False(state.ServosOn);

        io.SetInput(InputIo.AutoMode, true);
        io.SetInput(InputIo.ResetButton, true);
        await WaitUntilAsync(() => !state.IsError);
        io.SetInput(InputIo.ResetButton, false);

        Assert.True(state.ServosOn);
        Assert.True(state.Homed);

        io.SetInput(InputIo.Door1Open, true);
        io.SetInput(InputIo.AutoMode, false);
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
        settings.Units.MainConveyor = true;
        settings.BoltFeeder.ShootingTimeoutMilliseconds = 50;
        using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();

        await machine.InitializeAsync();
        io.SetInput(InputIo.AutoMode, false);
        io.SetInput(InputIo.MainConveyorAvailableFromFront2, false);
        await machine.StartAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(MachineAlarm.ShootingBoltFeeder, state.Alarm);
        Assert.False(io.GetOutput(OutputIo.ShootingFeederRunSignal));
        Assert.False(io.GetOutput(OutputIo.MainConveyorReadyToFront2));
        Assert.False(state.IsRunning);
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
        io.SetInput(InputIo.AutoMode, false);
        var run = machine.StartAsync();
        await WaitUntilAsync(() => state.AutomaticRunning);

        io.SetConnected(false);
        await run.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(MachineAlarm.IoCommunication, state.Alarm);
        Assert.False(state.IsRunning);

        Assert.Contains("disconnected", state.AlarmDetail);

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
            Units = EnableOnly(MachineUnit.BoltFastening),
        };
        FastHomes(settings);
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
        io.SetInput(InputIo.AutoMode, false);
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
}
