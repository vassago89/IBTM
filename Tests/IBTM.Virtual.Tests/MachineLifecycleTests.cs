using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using IBTM.BoltFastening;
using IBTM.Core;
using IBTM.Conveyor;
using IBTM.Device;
using IBTM.Hantas;
using IBTM.Inspection;
using IBTM.NgConveyor;
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
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisabledPlacementAndFasteningTransferCarrierThroughBothStations(bool raisedAtStart)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.MainConveyor);
        settings.Conveyor.CarrierStopDelaySeconds = 0;
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var conveyor = services.GetRequiredService<MainConveyor>();
        var fastening = services.GetRequiredService<BoltFasteningWork>();
        var inspection = services.GetRequiredService<InspectionWork>();
        var fasteningPlate = new ConcurrentQueue<bool>();
        var workOutputs = new ConcurrentQueue<OutputIo>();
        await machine.InitializeAsync();
        if (raisedAtStart)
        {
            await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.PcbPlacementBackupPlateUp, true);
            await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.PcbPlacementStopperUp, true);
        }
        io.SetInputs(
            (InputIo.MainConveyorAvailableFromFront2, false),
            (InputIo.MainConveyorReadyFromRear, false),
            (InputIo.NgCarrierDetected, true),
            (InputIo.PcbPlacementHeatSink1Present, false),
            (InputIo.PcbPlacementHeatSink2Present, true));
        // Match the equipment case: HS2 only and the empty next station raised.
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.InspectionBackupPlateUp, true);
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.InspectionStopperUp, true);
        io.OutputChanged += (output, value) =>
        {
            if (output == OutputIo.BoltFasteningBackupPlateUp)
                fasteningPlate.Enqueue(value);
            if (value && output is OutputIo.PcbPlacementHandlerDown or OutputIo.PcbPlacementVacuumEjector
                or OutputIo.ShootBolt or OutputIo.ShootingHeadDown or OutputIo.PickupHeadDown
                or OutputIo.NgCarrierPickupDown or OutputIo.NgCarrierGripperClose)
                workOutputs.Enqueue(output);
        };
        Assert.False(fastening.Enabled);
        Assert.True(machine.IsStartAllowed);
        var run = machine.StartAsync();
        try
        {
            Assert.True(
                await VirtualTest.WaitUntilAsync(() => inspection.Station.CarrierSeated, TimeSpan.FromSeconds(5)),
                $"Conveyor={conveyor.State}, FasteningCompleted={fastening.Completed}, "
                    + $"FasteningSeated={fastening.Station.CarrierSeated}, InspectionCanReceive={inspection.IsReceiveAllowed}, "
                    + $"Alarm={state.AlarmMessage}");
            Assert.Equal(new[] { true, false }, fasteningPlate);
            Assert.False(fastening.Station.CarrierPresent);
            Assert.False(inspection.Station.IsHeatSinkPresent(HeatSinkSlot.HeatSink1));
            Assert.True(inspection.Station.IsHeatSinkPresent(HeatSinkSlot.HeatSink2));
            Assert.True(io.GetInput(InputIo.NgCarrierDetected));
            Assert.False(conveyor.RunCommandOn);
            Assert.True(state.AutomaticRunning);
            Assert.Equal(MachineAlarm.None, state.Alarm);
            Assert.True(inspection.Completed);
            Assert.Empty(inspection.Assemblies);
            Assert.Empty(workOutputs);
        }
        finally
        {
            machine.Stop();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task InspectionRequiresBoltsAsWellAsDataMatrixTeaching()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.Inspection);
        await using var services = CreateServices(settings);
        var recipe = services.GetRequiredService<RecipeManager>().Current;
        TeachInspectionFovs(settings, recipe);
        var machine = services.GetRequiredService<MachineController>();
        Assert.False(machine.TeachingReady);

        recipe.Pcb.BoltPoints.Add(new() { Number = 1, X = 10, Y = 10 });
        TeachInspectionFovs(settings, recipe);
        Assert.True(machine.TeachingReady);
        settings.Units.Inspection = false;
        recipe.Pcb.BoltPoints.Clear();
        Assert.True(machine.TeachingReady);
    }

    [Theory]
    [InlineData(MachineUnit.BoltFastening)]
    [InlineData(MachineUnit.Inspection)]
    public async Task UntaughtPresentHeatSinkDoesNotCompleteProduction(MachineUnit unit)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(unit);
        settings.Units.ShootingBoltFeeder = unit == MachineUnit.BoltFastening;
        await using var services = CreateServices(settings);
        var recipe = services.GetRequiredService<RecipeManager>().Current;
        PrepareCarrierTeaching(settings, recipe);
        recipe.Pcb.BoltPoints.RemoveAll(bolt => bolt.HeatSink == HeatSinkSlot.HeatSink2);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        StationWork work = unit == MachineUnit.Inspection
            ? services.GetRequiredService<InspectionWork>()
            : services.GetRequiredService<BoltFasteningWork>();
        (InputIo HeatSink, OutputIo Plate, OutputIo Stopper) station = unit == MachineUnit.Inspection
            ? (InputIo.InspectionHeatSink2Present,
                OutputIo.InspectionBackupPlateUp, OutputIo.InspectionStopperUp)
            : (InputIo.BoltFasteningHeatSink2Present,
                OutputIo.BoltFasteningBackupPlateUp, OutputIo.BoltFasteningStopperUp);
        await machine.InitializeAsync();
        try
        {
            await machine.HomeAsync(CancellationToken.None);
            io.AutoResponseEnabled = false;
            var plate = io.GetOutputFeedback(station.Plate)!;
            var stopper = io.GetOutputFeedback(station.Stopper)!;
            var inspecting = unit == MachineUnit.Inspection;
            io.SetInput(station.HeatSink, true);
            io.SetInput(plate.OffInput!.Value, inspecting);
            io.SetInput(plate.OnInput, !inspecting);
            io.SetInput(stopper.OnInput, inspecting);
            io.SetInput(stopper.OffInput!.Value, !inspecting);
            Assert.True(
                await VirtualTest.WaitUntilAsync(() => machine.IsStartAllowed, TimeSpan.FromSeconds(2)),
                $"START blocked: {machine.StartBlock}; busy={state.IsRunning}; alarm={state.AlarmDetail}");
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await machine.StartAsync(stop.Token);
            Assert.False(work.Completed);
            Assert.Equal(unit == MachineUnit.Inspection ? MachineAlarm.Inspection : MachineAlarm.BoltFastening, state.Alarm);
            Assert.Contains("no taught bolts", state.AlarmMessage);
            Assert.Empty(work.Assemblies);
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(PlatePreparationOutcome.Arrived)]
    [InlineData(PlatePreparationOutcome.Stopped)]
    [InlineData(PlatePreparationOutcome.TimedOut)]
    public async Task StartPreparesEmptyBackupPlatesBeforeStartingUnits(PlatePreparationOutcome outcome)
    {
        var settings = new MachineSettings
        {
            Units = EnableOnly(MachineUnit.MainConveyor),
            Options = new() { TimeoutMilliseconds = 500 },
        };
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        await WaitUntilAsync(() => machine.IsStartAllowed);
        io.AutoResponseEnabled = false;
        OutputIo[] plates =
        [
            OutputIo.PcbPlacementBackupPlateUp,
            OutputIo.BoltFasteningBackupPlateUp,
            OutputIo.InspectionBackupPlateUp,
        ];
        foreach (var plate in plates)
        {
            var feedback = io.GetOutputFeedback(plate)!;
            io.SetOutput(plate, true);
            io.SetInput(feedback.OnInput, true);
            io.SetInput(feedback.OffInput!.Value, false);
        }
        var conveyorStarted = false;
        // Teaching suppresses SMEMA, so observe automatic start directly.
        state.Changed += () => conveyorStarted |= state.AutomaticRunning;

        var run = machine.StartAsync();
        try
        {
            await WaitUntilAsync(() => plates.All(plate => !io.GetOutput(plate)));
            foreach (var plate in plates.Take(2))
            {
                var feedback = io.GetOutputFeedback(plate)!;
                io.SetInput(feedback.OnInput, false);
                io.SetInput(feedback.OffInput!.Value, true);
            }
            Assert.False(conveyorStarted);
            Assert.False(state.AutomaticRunning);
            Assert.False(io.GetOutput(OutputIo.MainConveyorRun));

            if (outcome == PlatePreparationOutcome.Arrived)
            {
                var feedback = io.GetOutputFeedback(plates[2])!;
                io.SetInput(feedback.OnInput, false);
                io.SetInput(feedback.OffInput!.Value, true);
                Assert.True(
                    await VirtualTest.WaitUntilAsync(() => conveyorStarted, TimeSpan.FromSeconds(2)),
                    $"Automatic={state.AutomaticRunning}, Alarm={state.AlarmMessage}, "
                        + $"Conveyor={services.GetRequiredService<MainConveyor>().State}, "
                        + $"Teaching={io.GetInput(InputIo.AutoMode)}");
                Assert.True(state.AutomaticRunning);
                Assert.All(plates, plate => Assert.False(io.GetOutput(plate)));
                machine.Stop();
            }
            else if (outcome == PlatePreparationOutcome.Stopped)
            {
                machine.Stop();
            }

            await run.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(outcome == PlatePreparationOutcome.Arrived, conveyorStarted);
            Assert.Equal(outcome == PlatePreparationOutcome.TimedOut ? MachineAlarm.MainConveyor : MachineAlarm.None, state.Alarm);
            if (outcome == PlatePreparationOutcome.TimedOut)
                Assert.Contains("Inspection Backup Plate Down=ON timeout", state.AlarmMessage);
            Assert.False(state.IsRunning);
            Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
        }
        finally
        {
            machine.Stop();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task DataMatrixFailureStopsInspectionAndResetAllowsARealRead()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.Inspection);
        await using var services = CreateServices(settings);
        var recipe = services.GetRequiredService<RecipeManager>().Current;
        recipe.Pcb.BoltPoints = [new() { Number = 1, X = 10, Y = 10 }

        ];
        TeachInspectionFovs(settings, recipe);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var work = services.GetRequiredService<InspectionWork>();
        var camera = services.GetRequiredService<VirtualCamera>();
        var io = services.GetRequiredService<VirtualIoService>();
        settings.CarrierReference.UpperLeftLocatingPin = null;
        settings.CarrierReference.LowerRightLocatingPin = null;
        foreach (var bolt in recipe.Pcb.BoltPoints)
        {
            bolt.X = null;
            bolt.Y = null;
        }
        Assert.True(machine.TeachingReady);
        settings.Units.BoltFastening = true;
        settings.Units.ShootingBoltFeeder = true;
        Assert.False(machine.TeachingReady);
        settings.Units.BoltFastening = false;
        settings.Units.ShootingBoltFeeder = false;
        var boltFov = recipe.CarrierImages.First(fov => fov.BoltNumber is not null);
        var boltRegion = boltFov.Region;
        boltFov.Region = null;
        Assert.False(machine.TeachingReady);
        boltFov.Region = boltRegion;
        Assert.True(machine.TeachingReady);
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);

        var inspector = services.GetRequiredService<BoltInspector>();
        var barcodeImage = await inspector.CaptureBarcodeAsync(HeatSinkSlot.HeatSink2);
        Assert.Equal("PCB-2", DataMatrixReader.Read(barcodeImage, inspector.GetBarcodeFov(HeatSinkSlot.HeatSink2).Region!));
        Assert.True(inspector.IsAtBarcode(HeatSinkSlot.HeatSink2));

        var frame = await camera.CaptureAsync();
        camera.SourceImage = frame with { Pixels = new byte[frame.Pixels.Length] };
        VirtualTest.SetCarrier(io, InputIo.InspectionHeatSink1Present, true);
        io.SetInput(InputIo.InspectionBackupPlateUp, true);
        io.SetInput(InputIo.InspectionBackupPlateDown, false);
        io.SetInput(InputIo.InspectionStopperDown, true);
        io.SetInput(InputIo.InspectionStopperUp, false);
        io.SetInput(InputIo.InspectionHeatSink1Present, true);
        io.SetInput(InputIo.AutoMode, false);
        Assert.True(work.Station.CarrierSeated);
        await WaitUntilAsync(() => state.Homed);
        Assert.True(machine.IsStartAllowed);
        using var failureStop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await machine.StartAsync(failureStop.Token);
        Assert.True(state.Alarm == MachineAlarm.Inspection, $"{state.Alarm}: {state.AlarmDetail} {state.AlarmMessage}");
        Assert.Contains("Data Matrix", state.AlarmDetail);
        Assert.StartsWith("Data Matrix could not be read", state.AlarmMessage);
        Assert.False(work.Completed);
        Assert.Null(work.GetAssembly(HeatSinkSlot.HeatSink1).PcbBarcode);
        Assert.Empty(work.GetAssembly(HeatSinkSlot.HeatSink1).BoltPresenceResults);
        Assert.False(services.GetRequiredService<NgCarrierTransfer>().Feedback.IsMoving);

        camera.SourceImage = null;
        await machine.ResetAsync();
        await WaitUntilAsync(() => state.Homed);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var run = machine.StartAsync(stop.Token);
        try
        {
            Assert.True(await VirtualTest.WaitUntilAsync(() => work.Completed, TimeSpan.FromSeconds(2)));
            Assert.Equal("PCB-1", work.GetAssembly(HeatSinkSlot.HeatSink1).PcbBarcode);
            Assert.Single(work.GetAssembly(HeatSinkSlot.HeatSink1).BoltPresenceResults);
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
    public async Task ManualShootingLatchesUntilOffOrMachineStop()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.BoltFastening);
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var teaching = services.GetRequiredService<TeachingViewModel>();
        var io = services.GetRequiredService<VirtualIoService>();
        var state = services.GetRequiredService<MachineState>();
        await machine.InitializeAsync();
        io.AutoResponseEnabled = false;
        io.SetInput(InputIo.ShootingTubeBoltDetected, true);
        io.SetInput(InputIo.ShootingHeadVacuumDetected, false);
        teaching.SelectedTeachingUnit = HardwareArea.BoltFastening;
        var shoot = TeachingRows(teaching)[OutputIo.ShootBolt];
        var outputs = new List<OutputIo>();
        io.OutputChanged += (output, _) => outputs.Add(output);
        await WaitUntilAsync(() => shoot.ToggleOutputCommand.CanExecute(null));
        await shoot.ToggleOutputCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(io.GetOutput(OutputIo.ShootBolt));
        Assert.True(state.ManualSetupEnabled);
        Assert.False(services.GetRequiredService<OperationCancellation>().HasActiveOperations);

        teaching.SelectedTeachingUnit = HardwareArea.InspectionGantry;
        teaching.Deactivate();
        Assert.True(io.GetOutput(OutputIo.ShootBolt));
        Assert.Single(outputs);

        teaching.SelectedTeachingUnit = HardwareArea.BoltFastening;
        await WaitUntilAsync(() => shoot.ToggleOutputCommand.CanExecute(null));
        await shoot.ToggleOutputCommand.ExecuteAsync(null);
        Assert.False(io.GetOutput(OutputIo.ShootBolt));
        Assert.All(outputs, output => Assert.Equal(OutputIo.ShootBolt, output));

        await WaitUntilAsync(() => shoot.ToggleOutputCommand.CanExecute(null));
        await shoot.ToggleOutputCommand.ExecuteAsync(null);
        machine.Stop();
        Assert.False(io.GetOutput(OutputIo.ShootBolt));

        await WaitUntilAsync(() => shoot.ToggleOutputCommand.CanExecute(null));
        await shoot.ToggleOutputCommand.ExecuteAsync(null);
        io.SetInput(InputIo.AutoMode, false);
        Assert.False(io.GetOutput(OutputIo.ShootBolt));
        Assert.Equal(MachineAlarm.None, state.Alarm);
        await WaitUntilAsync(() => !shoot.ToggleOutputCommand.CanExecute(null));
    }

    [Fact]
    public async Task StoppedNgTransferStartsWithHeldCarrierAndPlacesItOnShuttle()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.NgCarrierTransfer);
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var gantry = services.GetRequiredService<NgCarrierTransfer>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        VirtualTest.SetCarrier(io, InputIo.InspectionHeatSink1Present, true);
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.InspectionBackupPlateUp, true);
        // Presence and a closed empty gripper must not skip the first pickup descent.
        await services.GetRequiredService<NgCarrierTransfer>().SetGripperOpenAsync(false);
        io.SetInput(InputIo.NgCarrierDetected, true);
        var pickupDescents = 0;
        io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.NgCarrierPickupDown && on)
                pickupDescents++;
        };
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
        Assert.Equal(1, pickupDescents);
        Assert.False(gantry.Feedback.IsMoving);
        Assert.True(io.GetInput(InputIo.NgCarrierGripperClosed));
        Assert.True(io.GetInput(InputIo.NgCarrierDetected));
        Assert.Equal(MachineAlarm.None, state.Alarm);

        Assert.Equal(StartBlockReason.None, machine.StartBlock);
        await machine.ResetAsync();
        Assert.False(state.IsError);
        Assert.Equal(StartBlockReason.None, machine.StartBlock);
        Assert.True(io.GetInput(InputIo.NgCarrierDetected));
        var restarted = machine.StartAsync();
        try
        {
            await WaitUntilAsync(() => io.GetInput(InputIo.NgShuttleCarrierDetected)
                && !io.GetInput(InputIo.NgCarrierDetected)
                && io.GetInput(InputIo.NgCarrierGripperOpen));
            Assert.Equal(MachineAlarm.None, state.Alarm);
        }
        finally
        {
            machine.Stop();
            await restarted.WaitAsync(TimeSpan.FromSeconds(3));
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(NgTransferLiftState.Down)]
    [InlineData(NgTransferLiftState.Between)]
    [InlineData(NgTransferLiftState.Up)]
    public async Task ReleasedNgCarrierIsNotGrippedAgainOnRestart(NgTransferLiftState lift)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.NgCarrierTransfer);
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<VirtualIoService>();
        var gantry = services.GetRequiredService<NgCarrierTransfer>();
        var station = services.GetRequiredService<InspectionStation>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        await gantry.MoveToAsync(settings.NgCarrierTransfer.ShuttlePlacePosition, 10_000);
        io.AutoResponseEnabled = false;
        io.SetInput(InputIo.NgCarrierDetected, true);
        io.SetInput(InputIo.NgShuttleCarrierDetected, true);
        io.SetInput(InputIo.NgCarrierPickupDown, lift == NgTransferLiftState.Down);
        io.SetInput(InputIo.NgCarrierPickupUp, lift == NgTransferLiftState.Up);

        Assert.Equal(
            lift == NgTransferLiftState.Up
                ? InspectionStationState.ReturningToNgPickup
                : InspectionStationState.TransferringNgCarrier,
            station.GetState([]));

        using var stop = new CancellationTokenSource();
        var run = station.RunAsync([], stop.Token);
        Assert.False(io.GetOutput(OutputIo.NgCarrierGripperClose));
        stop.Cancel();
        await run;

        if (lift != NgTransferLiftState.Up)
            await VerifyTransferReleaseAsync();
        // The carrier may leave the pickup sensor before the gripper reaches Open.
        io.SetInput(InputIo.NgCarrierDetected, false);
        io.SetInput(InputIo.NgCarrierPickupUp, false);
        io.SetInput(InputIo.NgCarrierPickupDown, true);
        io.SetInput(InputIo.NgCarrierGripperOpen, false);
        io.SetInput(InputIo.NgCarrierGripperClosed, false);
        Assert.Equal(InspectionStationState.TransferringNgCarrier, station.GetState([]));
        await VerifyTransferReleaseAsync();

        async Task VerifyTransferReleaseAsync()
        {
            var move = services.GetRequiredService<NgCarrierTransfer>();
            using var moveStop = new CancellationTokenSource();
            var moveTask = move.RunToAsync(NgTransferDestination.Shuttle, moveStop.Token);
            try
            {
                Assert.Equal(NgTransferState.PlacingCarrier, move.GetState(NgTransferDestination.Shuttle, canPickUp: true));
                Assert.False(io.GetOutput(OutputIo.NgCarrierGripperClose));
            }
            finally
            {
                moveStop.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => moveTask);
            }
        }
    }

    [Fact]
    public async Task BinaryInspectionStartsWithoutTrainingOrModel()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.Inspection);
        await using var services = new ServiceCollection().AddSingleton(_ => VirtualTest.OpenMachineStore())
            .AddIbtmApplication(settings)
            .BuildServiceProvider();
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        services.GetRequiredService<RecipeManager>().Current
            .Pcb.BoltPoints.Add(new BoltPoint { Number = 1, X = 10, Y = 10 });
        TeachInspectionFovs(settings, services.GetRequiredService<RecipeManager>().Current);

        await machine.InitializeAsync();
        Assert.Equal(MachineAlarm.None, state.Alarm);
        Assert.True(machine.IsHomeAllowed);
        await machine.HomeAsync(CancellationToken.None);
        Assert.True(state.ManualControlsEnabled);

        io.SetInput(InputIo.AutoMode, false);
        Assert.True(machine.IsStartAllowed);
        var run = machine.StartAsync();
        try
        {
            await WaitUntilAsync(() => state.AutomaticRunning);
            Assert.Equal(MachineAlarm.None, state.Alarm);
        }
        finally
        {
            machine.Stop();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
        }

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
        bool inspectionEnabled,
        bool transferEnabled,
        bool expectNg)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.MainConveyor);
        settings.Units.Inspection = inspectionEnabled;
        settings.Units.NgCarrierTransfer = transferEnabled;
        await using var services = CreateServices(settings);
        var io = services.GetRequiredService<VirtualIoService>();
        var work = services.GetRequiredService<InspectionWork>();
        var conveyor = services.GetRequiredService<MainConveyor>();
        var inspection = services.GetRequiredService<InspectionStation>();
        var machine = services.GetRequiredService<MachineController>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        if (inspectionEnabled || transferEnabled)
        {
            await services.GetRequiredService<NgCarrierTransfer>().MoveToCarrierAsync(
                NgTransferDestination.Station, CancellationToken.None);
        }
        io.SetInput(InputIo.AutoMode, false);
        VirtualTest.SetCarrier(io, InputIo.InspectionHeatSink1Present, true);
        io.SetInput(InputIo.InspectionHeatSink1Present, true);
        io.SetInput(InputIo.MainConveyorReadyFromRear, true);
        await work.Station.SeatAsync(CancellationToken.None);
        var assembly = work.GetAssembly(HeatSinkSlot.HeatSink1);
        assembly.RecordBoltPresence(1, true);
        assembly.CompleteInspection();
        work.Complete(work.CurrentJob);

        Assert.Equal(expectNg, inspection.GetState([]) == InspectionStationState.TransferringNgCarrier);
        Assert.Equal(!expectNg, conveyor.State == MainConveyorState.DischargingInspectionCarrier);
        await machine.ShutdownAsync();
    }

    [Theory]
    [InlineData(FasteningHead.Pickup)]
    [InlineData(FasteningHead.Shooting)]
    public async Task ManualBoltTestPreservesInterruptedProductionResult(FasteningHead selected)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.BoltFastening);
        settings.Units.PickupBoltFeeder = selected == FasteningHead.Pickup;
        settings.Units.ShootingBoltFeeder = selected == FasteningHead.Shooting;
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var station = services.GetRequiredService<BoltFasteningStation>();
        var recipe = services.GetRequiredService<RecipeManager>().Current;
        recipe.Pcb.BoltPoints = [new() { Number = 1, Head = selected, X = 0, Y = 0 }];
        var productionHead = services.GetRequiredKeyedService<IBoltHead>(selected);
        var bus = services.GetRequiredService<IAdcBus>();
        var slave = selected == FasteningHead.Pickup
            ? settings.Hantas.PickupSlaveAddress
            : settings.Hantas.ShootingSlaveAddress;
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        io.AutoResponseEnabled = false;
        io.SetInputs(
            (InputIo.BoltFasteningHeatSink1Present, true),
            (InputIo.BoltFasteningBackupPlateUp, true),
            (InputIo.BoltFasteningBackupPlateDown, false),
            (InputIo.BoltFasteningStopperUp, false),
            (InputIo.BoltFasteningStopperDown, true),
            (InputIo.PickupHeadUp, true),
            (InputIo.PickupHeadDown, false),
            (InputIo.ShootingHeadUp, true),
            (InputIo.ShootingHeadDown, false),
            (InputIo.PickupHeadVacuumDetected, true),
            (InputIo.ShootingHeadVacuumDetected, true));
        using var stop = new CancellationTokenSource();
        void StopWhenStarted(AdcFrameDirection direction, byte[] frame)
        {
            if (direction == AdcFrameDirection.Transmit
                && frame[1] == (byte)AdcFunctionCode.WriteSingleRegister
                && BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(2)) == (ushort)AdcRemoteRegister.RemoteStart
                && BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(4)) != 0)
                stop.Cancel();
        }

        bus.FrameTransferred += StopWhenStarted;
        try
        {
            await station.RunAsync(stop.Token).WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            stop.Cancel();
            bus.FrameTransferred -= StopWhenStarted;
        }

        Assert.True(productionHead.HasPendingResult);
        Assert.True(station.HasPendingResult);
        var interruptedEvent = (await bus.ReadFasteningResultAsync(slave)).EventCount;

        settings.Units.PickupBoltFeeder = false;
        settings.Units.ShootingBoltFeeder = false;
        Assert.True(productionHead.HasPendingResult);
        Assert.True(station.HasPendingResult);

        using var diagnostics = new AdcProtocolViewModel(bus, settings.Hantas, machine, state)
        {
            SlaveText = slave.ToString(),
        };
        await diagnostics.StartCommand.ExecuteAsync(null);
        Assert.Contains("production fastening result is still pending", diagnostics.ConnectionStatus);
        Assert.False(machine.IsTestBoltHeadAllowed);
        Assert.True(machine.IsUseAdcProtocolAllowed);
        Assert.False(state.IsRunning);
        Assert.Equal(MachineAlarm.None, state.Alarm);
        Assert.True(productionHead.HasPendingResult);
        Assert.Equal(interruptedEvent, (await bus.ReadFasteningResultAsync(slave)).EventCount);
        Assert.Null(await productionHead.ReadPendingResultAsync());

        state.SetError(MachineAlarm.BoltFastening);
        await machine.ResetAsync();
        Assert.Equal(MachineAlarm.None, state.Alarm);
        Assert.True(productionHead.HasPendingResult);
        Assert.True(station.HasPendingResult);
        Assert.Equal(StartBlockReason.None, machine.StartBlock);
        Assert.False(machine.IsTestBoltHeadAllowed);
        Assert.Equal(interruptedEvent, (await bus.ReadFasteningResultAsync(slave)).EventCount);

        // Acknowledgement clears only this fastening operation, not other carriers.
        VirtualTest.SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, false);
        io.SetInput(InputIo.PcbPlacementHeatSink1Present, true);
        io.SetInputs(
            (InputIo.PickupHeadVacuumDetected, false),
            (InputIo.ShootingHeadVacuumDetected, false));
        await machine.ResetAsync();
        Assert.True(machine.IsTestBoltHeadAllowed);
        Assert.True(io.GetInput(InputIo.PcbPlacementHeatSink1Present));
        await diagnostics.StartCommand.ExecuteAsync(null);
        Assert.StartsWith("OK", diagnostics.ResultMessage);
        Assert.False(productionHead.HasPendingResult);
        Assert.Equal(interruptedEvent + 1, (await bus.ReadFasteningResultAsync(slave)).EventCount);
    }

    [Fact]
    public async Task AdcOperationKeepsMachineLockedUntilStopFinishes()
    {
        var settings = new MachineSettings
        {
            Units = EnableOnly(MachineUnit.NgConveyor),
        };
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var bus = new AdcProtocolTests.ControllerBus { StopPollsRemaining = -1 };
        await machine.InitializeAsync();
        using var diagnostics = new AdcProtocolViewModel(bus, settings.Hantas, machine, state);
        var testing = diagnostics.StartCommand.ExecuteAsync(null);
        Assert.True(state.IsRunning);
        machine.Stop();
        await WaitUntilAsync(() => bus.StopWrites > 0);
        io.SetInput(InputIo.AutoMode, false);
        Assert.True(state.IsRunning);
        Assert.False(machine.IsStartAllowed);
        Assert.False(machine.IsHomeAllowed);
        Assert.False(machine.IsResetAllowed);
        await machine.StartAsync();
        Assert.False(state.AutomaticRunning);

        bus.StopPollsRemaining = 0;
        await testing.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(state.IsRunning);
        Assert.True(machine.IsStartAllowed);
    }

    [Fact]
    public async Task AutomaticAlarmWaitsForHeadStopAndKeepsFirstCause()
    {
        var settings = new MachineSettings
        {
            Units = EnableOnly(MachineUnit.BoltFastening),
        };
        FastHomes(settings);
        settings.Units.ShootingBoltFeeder = true;
        var head = new StoppingBoltHead();
        await using var services = new ServiceCollection().AddSingleton(_ => VirtualTest.OpenMachineStore())
            .AddIbtmApplication(settings)
            .AddKeyedSingleton<IBoltHead>(FasteningHead.Shooting, head)
            .BuildServiceProvider();
        PrepareCarrierTeaching(settings, services.GetRequiredService<RecipeManager>().Current);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();

        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        VirtualTest.SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, true);
        io.SetInput(InputIo.BoltFasteningHeatSink1Present, true);
        io.SetInput(InputIo.BoltFasteningBackupPlateUp, true);
        io.SetInput(InputIo.BoltFasteningBackupPlateDown, false);
        io.SetInput(InputIo.BoltFasteningStopperUp, false);
        io.SetInput(InputIo.BoltFasteningStopperDown, true);
        io.SetInput(InputIo.ShootingHeadVacuumDetected, true);
        io.SetInput(InputIo.AutoMode, false);
        await WaitUntilAsync(() => machine.IsStartAllowed);
        Assert.True(services.GetRequiredService<BoltFasteningWork>().Station.CarrierSeated);

        var run = machine.StartAsync();
        try
        {
            Assert.True(
                await VirtualTest.WaitUntilAsync(() => head.Started.Task.IsCompleted, TimeSpan.FromSeconds(3)),
                $"Fastening did not start: block={machine.StartBlock}, alarm={state.Alarm}, "
                    + $"station={services.GetRequiredService<BoltFasteningStation>().GetState()}. {state.AlarmDetail}");
            io.SetInput(InputIo.AirPressureHigh, false);
            await head.Stopping.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.False(run.IsCompleted);
            Assert.True(state.AutomaticRunning);
            Assert.True(state.IsRunning);
            Assert.False(machine.IsResetAllowed);
            Assert.Equal(MachineAlarm.AirPressureLow, state.Alarm);

            head.Stopped.SetException(new InvalidOperationException("Head stop failed."));
            await run.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.False(state.IsRunning);
            Assert.Equal(MachineAlarm.AirPressureLow, state.Alarm);
            Assert.Null(state.AlarmMessage);
        }
        finally
        {
            machine.Stop();
            head.Stopped.TrySetResult();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task DisabledTransferStillBlocksShuttleUntilPickupIsRaised()
    {
        var settings = new MachineSettings
        {
            Units = EnableOnly(MachineUnit.NgShuttle),
        };
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<VirtualIoService>();
        var shuttle = services.GetRequiredService<NgShuttle>();
        var gantry = services.GetRequiredService<NgCarrierTransfer>();
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
        await using var services = CreateServices(
            new MachineSettings
            {
                Units = EnableOnly(MachineUnit.MainConveyor),
            });
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var operations = services.GetRequiredService<OperationCancellation>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        io.SetInput(InputIo.AutoMode, false);
        Assert.True(machine.IsStartAllowed);

        using var manual = operations.Link();
        Assert.True(state.IsRunning);
        Assert.False(machine.IsStartAllowed);
        await machine.StartAsync();
        Assert.False(state.AutomaticRunning);

        machine.Stop();
        Assert.True(manual.IsCancellationRequested);
        Assert.True(state.IsRunning);
        Assert.False(machine.IsStartAllowed);
        manual.Dispose();
        Assert.False(state.IsRunning);
        Assert.True(machine.IsStartAllowed);
    }

    [Fact]
    public async Task StopDuringMotionInitializationSkipsLaterUnitsAndCanRetry()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.PcbSupply);
        settings.Units.PcbPlacement = true;
        settings.Units.NgCarrierTransfer = true;
        await using var services = CreateMotionScopeServices(settings, out var probes);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var supply = probes[MotionGroup.PcbSupply];
        void StopAfterSupplyInitialization()
        {
            supply.Motion.StateChanged -= StopAfterSupplyInitialization;
            machine.Stop();
        }

        supply.Motion.StateChanged += StopAfterSupplyInitialization;
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => machine.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(2)));

            Assert.Equal(1, supply.InitializationCalls);
            Assert.All(
                probes.Where(item => item.Key != MotionGroup.PcbSupply),
                item => Assert.Equal(0, item.Value.InitializationCalls));
            Assert.False(state.IsRunning);
            Assert.Equal(MachineAlarm.None, state.Alarm);
            Assert.DoesNotContain(
                services.GetRequiredService<ApplicationLog>().Snapshot(),
                entry => entry.Level == "ERROR");

            await machine.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(2, supply.InitializationCalls);
            Assert.Equal(1, probes[MotionGroup.PcbPlacementHandler].InitializationCalls);
            Assert.Equal(1, probes[MotionGroup.InspectionGantry].InitializationCalls);
            Assert.Equal(0, probes[MotionGroup.BoltFastening].InitializationCalls);
            Assert.Equal(MachineAlarm.None, state.Alarm);
        }
        finally
        {
            supply.Motion.StateChanged -= StopAfterSupplyInitialization;
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task StopDuringHardwareReadinessPreventsStartAndAllowsRestart()
    {
        var settings = new MachineSettings
        {
            Units = EnableOnly(MachineUnit.BoltFastening),
        };
        FastHomes(settings);
        settings.Units.ShootingBoltFeeder = true;
        var head = new WaitingBoltHead();
        await using var services = new ServiceCollection().AddSingleton(_ => VirtualTest.OpenMachineStore())
            .AddIbtmApplication(settings)
            .AddKeyedSingleton<IBoltHead>(FasteningHead.Shooting, head)
            .AddKeyedSingleton<IBoltHead>(FasteningHead.Pickup, head)
            .BuildServiceProvider();
        PrepareCarrierTeaching(settings, services.GetRequiredService<RecipeManager>().Current);
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
        Assert.False(state.AutomaticRunning);
        Assert.True(state.IsRunning);
        await WaitUntilAsync(() => io.GetOutput(OutputIo.TowerLampYellow));
        Assert.False(io.GetOutput(OutputIo.TowerLampGreen));
        Assert.False(machine.IsStartAllowed);
        Assert.False(machine.IsHomeAllowed);
        await machine.StartAsync().WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(readinessChecks + 1, head.ReadinessChecks);

        machine.Stop();
        await starting.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(state.IsRunning);
        Assert.Equal(MachineAlarm.None, state.Alarm);

        head.ReadinessReleased.TrySetResult();
        var resumed = machine.StartAsync();
        await WaitUntilAsync(() => state.AutomaticRunning);
        await WaitUntilAsync(() => io.GetOutput(OutputIo.TowerLampGreen));
        machine.Stop();
        await resumed.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(state.IsRunning);
        Assert.Equal(MachineAlarm.None, state.Alarm);
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, false, true, true)]
    public async Task StopOrFailureDuringFirstUnitOutputPreventsLaterStarts(
        bool failure,
        bool stopBeforeFailure,
        bool motionFailure,
        bool combinedFailure)
    {
        var settings = new MachineSettings
        {
            Units = EnableOnly(MachineUnit.MainConveyor),
        };
        settings.Units.ShootingBoltFeeder = true;
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        io.SetInput(InputIo.AutoMode, false);
        io.SetInput(InputIo.MainConveyorAvailableFromFront2, false);
        var stopped = false;
        var feederStarted = false;
        Exception error = motionFailure
            ? new MotionException("Conveyor start", new IOException("Motion controller disconnected."))
            : new InvalidOperationException("Conveyor start failed.");
        if (combinedFailure)
            error = new AggregateException(new IOException("Output cleanup failed."), new AggregateException(error));
        io.OutputChanged += (output, value) =>
        {
            feederStarted |= output == OutputIo.ShootingFeederRunSignal && value;
            if (output == OutputIo.MainConveyorReadyToFront2 && value)
            {
                stopped = true;
                if (!failure || stopBeforeFailure)
                    machine.Stop();
                if (failure)
                    throw error;
            }
        };

        await machine.StartAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(stopped);
        Assert.False(feederStarted);
        Assert.False(state.IsRunning);
        var expectedAlarm = motionFailure
            ? MachineAlarm.MotionUnavailable
            : failure ? MachineAlarm.MainConveyor : MachineAlarm.None;
        Assert.Equal(expectedAlarm, state.Alarm);
        Assert.Equal(failure ? error.Message : null, state.AlarmMessage);
        Assert.False(services.GetRequiredService<OperationCancellation>().HasActiveOperations);
        Assert.False(io.GetOutput(OutputIo.ShootingFeederRunSignal));
        if (failure)
        {
            var entries = services.GetRequiredService<ApplicationLog>().Snapshot();
            Assert.Contains(entries, entry => entry.Message == $"Automatic unit MainConveyor failed. {error.Message}");
            var alarm = Assert.Single(entries, entry => entry.Detail == error.ToString());
            Assert.Equal($"Machine alarm: {expectedAlarm}.", alarm.Message);
            Assert.Equal(error.ToString(), state.AlarmDetail);
        }
    }

    [Fact]
    public async Task UnitFailureDuringSafetyStopKeepsFirstAlarmAndLogsFailure()
    {
        var settings = new MachineSettings { Units = EnableOnly(MachineUnit.MainConveyor) };
        await using var services = CreateServices(settings);
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
            if (output != OutputIo.MainConveyorReadyToFront2 || !on)
                return;
            io.SetInput(InputIo.AirPressureHigh, false);
            throw failure;
        };

        await machine.StartAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(MachineAlarm.AirPressureLow, state.Alarm);
        Assert.False(state.IsRunning);
        Assert.False(services.GetRequiredService<OperationCancellation>().HasActiveOperations);
        var entry = Assert.Single(log.Snapshot(), entry => entry.Detail == failure.ToString());
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
        await using var services = CreateServices(settings);
        PrepareCarrierTeaching(settings, services.GetRequiredService<RecipeManager>().Current);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();

        await machine.InitializeAsync();

        Assert.False(machine.IsStartAllowed);
        Assert.True(machine.IsHomeAllowed);
        Assert.False(machine.IsResetAllowed);

        await machine.HomeAsync(CancellationToken.None);
        Assert.True(state.Homed);

        io.SetInput(InputIo.AutoMode, false);
        Assert.True(machine.IsStartAllowed);
        await WaitUntilAsync(() => machine.IsStartAllowed);
        var firstRun = machine.StartAsync();
        Assert.True(await VirtualTest.WaitUntilAsync(
            () => state.AutomaticRunning, TimeSpan.FromSeconds(2)),
            $"{state.Alarm}: {state.AlarmDetail}");

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
        await WaitUntilAsync(() => machine.IsStartAllowed);
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
        await using var services = CreateServices(settings);
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
        Assert.True(machine.IsResetAllowed);

        await machine.ResetAsync();
        Assert.Equal(MachineAlarm.None, state.Alarm);

        settings.BoltFeeder.ShootingTimeoutMilliseconds = 500;
        var resumed = machine.StartAsync();
        await ((IIoService)io).WaitForInputAsync(InputIo.ShootingFeederBoltDetected, true);

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
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();

        await machine.InitializeAsync();
        io.SetInput(InputIo.AutoMode, false);
        var run = machine.StartAsync();
        await WaitUntilAsync(() => state.AutomaticRunning);

        io.IsReady = false;
        await run.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(MachineAlarm.IoCommunication, state.Alarm);
        Assert.False(state.IsRunning);

        Assert.Contains("disconnected", state.AlarmDetail);

        io.IsReady = true;
        Assert.True(machine.IsResetAllowed);
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
        await using var services = CreateServices(settings);
        PrepareCarrierTeaching(settings, services.GetRequiredService<RecipeManager>().Current);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var motion = (VirtualMotionService)services.GetRequiredKeyedService<IXyMotion>(
            MotionGroup.BoltFastening);

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

    public enum PlatePreparationOutcome
    {
        Arrived,
        Stopped,
        TimedOut,
    }
}
