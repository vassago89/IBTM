using IBTM.BoltFeeder;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Data.Sqlite;
using static IBTM.Virtual.Tests.VirtualTest;
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
        var fastening = services.GetRequiredService<BoltFasteningStation>().Station;
        var inspection = services.GetRequiredService<InspectionStation>();
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
        Assert.False(services.GetRequiredService<UnitSettings>().BoltFastening);
        Assert.True(machine.IsStartAllowed);
        var run = machine.StartAsync();
        try
        {
            Assert.True(
                await VirtualTest.WaitUntilAsync(() => inspection.Station.CarrierSeated, TimeSpan.FromSeconds(5)),
                $"Conveyor={conveyor.Step}, FasteningCompleted={fastening.Completed}, "
                    + $"FasteningSeated={fastening.CarrierSeated}, InspectionCanReceive={inspection.IsReceiveAllowed}, "
                    + $"Alarm={state.AlarmMessage}");
            Assert.Equal(new[] { true, false }, fasteningPlate);
            Assert.False(fastening.CarrierPresent);
            Assert.False(inspection.Station.IsHeatSinkPresent(HeatSinkSlot.HeatSink1));
            Assert.True(inspection.Station.IsHeatSinkPresent(HeatSinkSlot.HeatSink2));
            Assert.True(io.GetInput(InputIo.NgCarrierDetected));
            Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
            Assert.True(state.AutomaticRunning);
            Assert.Equal(MachineAlarm.None, state.Alarm);
            Assert.True(inspection.Station.Completed);
            Assert.Empty(inspection.Station.Assemblies);
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
        TeachInspectionFovs(recipe);
        var machine = services.GetRequiredService<MachineController>();
        Assert.False(machine.TeachingReady);

        recipe.Pcb.BoltPoints.Add(new() { Number = 1, X = 10, Y = 10 });
        TeachInspectionFovs(recipe);
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
        ConveyorStation work = unit == MachineUnit.Inspection
            ? services.GetRequiredService<InspectionStation>().Station
            : services.GetRequiredService<BoltFasteningStation>().Station;
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
                        + $"Conveyor={services.GetRequiredService<MainConveyor>().Step}, "
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

    [Theory]
    [InlineData(HeatSinkSlot.HeatSink1)]
    [InlineData(HeatSinkSlot.HeatSink2)]
    public async Task InspectionRecordsNgAndFinishesEachPcbBeforeTheNext(HeatSinkSlot unreadPcb)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.Inspection);
        await using var services = CreateServices(settings);
        var recipe = services.GetRequiredService<RecipeManager>().Current;
        recipe.Pcb.BoltPoints = [
            new() { Number = 4, HeatSink = HeatSinkSlot.HeatSink2, X = 30, Y = 20 },
            new() { Number = 3, HeatSink = HeatSinkSlot.HeatSink1, X = 10, Y = 20 },
            new() { Number = 2, HeatSink = HeatSinkSlot.HeatSink2, X = 30, Y = 10 },
            new() { Number = 1, HeatSink = HeatSinkSlot.HeatSink1, X = 10, Y = 10 },
        ];
        TeachInspectionFovs(recipe);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var work = services.GetRequiredService<InspectionStation>();
        var camera = services.GetRequiredService<VirtualCamera>();
        var io = services.GetRequiredService<VirtualIoService>();
        settings.CarrierReference.UpperLeftLocatingPin = null;
        settings.CarrierReference.LowerRightLocatingPin = null;
        foreach (var bolt in recipe.Pcb.BoltPoints)
        {
            bolt.FasteningX = null;
            bolt.FasteningY = null;
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

        var inspector = services.GetRequiredService<InspectionStation>();
        var barcodeResult = await inspector.ReadBarcodeAsync(HeatSinkSlot.HeatSink2, CancellationToken.None);
        Assert.True(barcodeResult.Success);
        Assert.Equal("PCB-2", barcodeResult.Barcode);
        Assert.True(MotionService.IsAt(inspector.Motion.Feedback, recipe.GetInspectionPosition(inspector.GetBarcodeFov(HeatSinkSlot.HeatSink2))));

        var barcodeImage = barcodeResult.Frame;
        var blankImage = barcodeImage with { Pixels = new byte[barcodeImage.Pixels.Length] };
        var view = services.GetRequiredService<OperationViewModel>();
        var transfer = services.GetRequiredService<InspectionStation>();
        var captures = new List<(HeatSinkSlot Pcb, int? Bolt)>();
        inspector.Trace += message =>
        {
            if (message.Contains(": ReadingBarcode ", StringComparison.Ordinal))
            {
                Assert.Null(inspector.ActiveBolt);
                Assert.Contains("Data Matrix", message);
                camera.SourceImage = inspector.ActivePcb == unreadPcb ? blankImage : null;
            }
            else if (message.Contains(": InspectingBolt ", StringComparison.Ordinal))
            {
                camera.SourceImage = inspector.ActiveBolt?.Number == 3 ? blankImage : null;
            }
        };
        inspector.InspectionCaptured += (image, pcb, boltNumber) =>
        {
            captures.Add((pcb, boltNumber));
            var fov = recipe.CarrierImages.Single(fov => fov.HeatSink == pcb
                && (boltNumber is null ? fov.IsBarcode : !fov.IsBarcode && fov.BoltNumber == boltNumber));
            Assert.Equal((recipe.GetInspectionPosition(fov).X, recipe.GetInspectionPosition(fov).Y, 0d), transfer.Motion.Feedback.Position);
            Assert.False(transfer.Motion.Feedback.IsMoving);
            Assert.Equal(pcb, inspector.ActivePcb);
            Assert.Equal(boltNumber, inspector.ActiveBolt?.Number);
            Assert.Equal($"{pcb.GetDescription()} · " + (boltNumber is null ? "Data Matrix" : $"Bolt {boltNumber}"),
                view.InspectionImageCaption);
            if (boltNumber is null && pcb != unreadPcb)
                Assert.Equal(pcb == HeatSinkSlot.HeatSink1 ? "PCB-1" : "PCB-2",
                    DataMatrixReader.Read(image, fov.Region!, recipe.BoltInspection.GetDataMatrix(pcb)));
        };
        VirtualTest.SetCarrier(io, InputIo.InspectionHeatSink1Present, true);
        io.SetInput(InputIo.InspectionHeatSink1Present, true);
        io.SetInput(InputIo.InspectionHeatSink2Present, true);
        await work.Station.PrepareToReceiveAsync(CancellationToken.None);
        io.SetInput(InputIo.AutoMode, false);
        Assert.True(work.IsAtInspectionPosition);
        await WaitUntilAsync(() => state.FeedbackReadiness.Homed);
        Assert.True(machine.IsStartAllowed);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var run = machine.StartAsync(stop.Token);
        try
        {
            Assert.True(await VirtualTest.WaitUntilAsync(() => work.Station.Completed, TimeSpan.FromSeconds(3)),
                $"Alarm={state.Alarm}; inspection={inspector.GetNextStep()}; captures={string.Join(", ", captures)}; {state.AlarmDetail}");
            Assert.Equal(new (HeatSinkSlot, int?)[] {
                (HeatSinkSlot.HeatSink1, null), (HeatSinkSlot.HeatSink1, 1), (HeatSinkSlot.HeatSink1, 3),
                (HeatSinkSlot.HeatSink2, null), (HeatSinkSlot.HeatSink2, 2), (HeatSinkSlot.HeatSink2, 4),
            }, captures);
            foreach (var pcb in Enum.GetValues<HeatSinkSlot>())
            {
                var assembly = work.Station.GetAssembly(pcb);
                Assert.Equal(pcb == unreadPcb ? AssemblyResult.Ng : AssemblyResult.Ok, assembly.PcbBarcodeResult);
                Assert.Equal(pcb == unreadPcb ? null : pcb == HeatSinkSlot.HeatSink1 ? "PCB-1" : "PCB-2", assembly.PcbBarcode);
                Assert.Equal(2, assembly.BoltPresenceResults.Count);
                Assert.Equal(pcb == unreadPcb || pcb == HeatSinkSlot.HeatSink1 ? AssemblyResult.Ng : AssemblyResult.Ok,
                    assembly.InspectionResult);
            }
            Assert.True(work.Station.GetAssembly(HeatSinkSlot.HeatSink1).BoltPresenceResults[1]);
            Assert.False(work.Station.GetAssembly(HeatSinkSlot.HeatSink1).BoltPresenceResults[3]);
            Assert.All(work.Station.GetAssembly(HeatSinkSlot.HeatSink2).BoltPresenceResults.Values, Assert.True);
            Assert.Equal("NG · Not Read", unreadPcb == HeatSinkSlot.HeatSink1 ? view.InspectionPcb1Barcode : view.InspectionPcb2Barcode);
            Assert.True(work.HasNg);
            Assert.True(work.RouteToNg);
            Assert.True(state.AutomaticRunning);
            Assert.False(run.IsCompleted);
            Assert.True(state.Alarm == MachineAlarm.None, state.AlarmDetail);
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
        settings.Units = EnableOnly(MachineUnit.Inspection);
        settings.Units.MainConveyor = true;
        await using var services = CreateServices(settings);
        PrepareCarrierTeaching(settings, services.GetRequiredService<RecipeManager>().Current);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var gantry = services.GetRequiredService<InspectionStation>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        io.SetInput(InputIo.MainConveyorAvailableFromFront2, false);
        VirtualTest.SetCarrier(io, InputIo.InspectionHeatSink1Present, true);
        await services.GetRequiredService<InspectionStation>().Station.PrepareToReceiveAsync(CancellationToken.None);
        services.GetRequiredService<VirtualCamera>().BoltsPresent = false;
        // Presence and a closed empty gripper must not skip the first pickup descent.
        await services.GetRequiredService<InspectionStation>().SetGripperOpenAsync(false);
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

        gantry.Motion.Feedback.PositionChanged += StopDuringTransfer;
        using var initialTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await machine.StartAsync(initialTimeout.Token);
        gantry.Motion.Feedback.PositionChanged -= StopDuringTransfer;
        Assert.False(initialTimeout.IsCancellationRequested,
            $"Inspection={gantry.GetNextStep()}, Main={services.GetRequiredService<MainConveyor>().Step}, "
            + $"Completed={services.GetRequiredService<InspectionStation>().Station.Completed}, NG={services.GetRequiredService<InspectionStation>().HasNg}, "
            + $"Position={gantry.Motion.Feedback.Position}, Lift={gantry.Lift}, Gripper={gantry.Gripper}, Alarm={state.AlarmDetail}");
        var stoppedX = gantry.Motion.Feedback.Position.X;
        Assert.InRange(stoppedX, 40, 149);
        Assert.Equal(1, pickupDescents);
        Assert.False(gantry.Motion.Feedback.IsMoving);
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
    [InlineData(StationCylinderState.Down)]
    [InlineData(StationCylinderState.Between)]
    [InlineData(StationCylinderState.Up)]
    public async Task ReleasedNgCarrierIsNotGrippedAgainOnRestart(StationCylinderState lift)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.Inspection);
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<VirtualIoService>();
        var gantry = services.GetRequiredService<InspectionStation>();
        var station = services.GetRequiredService<InspectionStation>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        await gantry.MoveToAsync(settings.NgCarrierTransfer.ShuttlePlacePosition, 10_000);
        io.AutoResponseEnabled = false;
        io.SetInput(InputIo.NgCarrierDetected, true);
        io.SetInput(InputIo.NgShuttleCarrierDetected, true);
        io.SetInput(InputIo.NgCarrierPickupDown, lift == StationCylinderState.Down);
        io.SetInput(InputIo.NgCarrierPickupUp, lift == StationCylinderState.Up);

        Assert.Equal(
            lift == StationCylinderState.Up
                ? InspectionStationState.ReturningToWaitingPosition
                : InspectionStationState.PlacingCarrier,
            station.GetNextStep());

        using var stop = new CancellationTokenSource();
        var run = station.RunAsync(stop.Token);
        Assert.False(io.GetOutput(OutputIo.NgCarrierGripperClose));
        stop.Cancel();
        await run;

        if (lift != StationCylinderState.Up)
            await VerifyTransferReleaseAsync();
        // The carrier may leave the pickup sensor before the gripper reaches Open.
        io.SetInput(InputIo.NgCarrierDetected, false);
        io.SetInput(InputIo.NgCarrierPickupUp, false);
        io.SetInput(InputIo.NgCarrierPickupDown, true);
        io.SetInput(InputIo.NgCarrierGripperOpen, false);
        io.SetInput(InputIo.NgCarrierGripperClosed, false);
        Assert.Equal(InspectionStationState.PlacingCarrier, station.GetNextStep());
        await VerifyTransferReleaseAsync();

        async Task VerifyTransferReleaseAsync()
        {
            var move = services.GetRequiredService<InspectionStation>();
            using var moveStop = new CancellationTokenSource();
            var moveTask = move.RunToAsync(NgTransferDestination.Shuttle, moveStop.Token);
            try
            {
                Assert.Equal(InspectionStationState.PlacingCarrier, move.GetNextTransferStep(NgTransferDestination.Shuttle, canPickUp: true));
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
        TeachInspectionFovs(services.GetRequiredService<RecipeManager>().Current);

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
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task InspectionAndConveyorAgreeOnBypassRoute(bool inspectionEnabled, bool ng)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.MainConveyor);
        settings.Units.Inspection = inspectionEnabled;
        await using var services = CreateServices(settings);
        var io = services.GetRequiredService<VirtualIoService>();
        var work = services.GetRequiredService<InspectionStation>();
        var conveyor = services.GetRequiredService<MainConveyor>();
        var inspection = services.GetRequiredService<InspectionStation>();
        var machine = services.GetRequiredService<MachineController>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        if (inspectionEnabled)
        {
            await services.GetRequiredService<InspectionStation>().MoveToCarrierAsync(
                NgTransferDestination.Station, CancellationToken.None);
        }
        io.SetInput(InputIo.AutoMode, false);
        VirtualTest.SetCarrier(io, InputIo.InspectionHeatSink1Present, true);
        io.SetInput(InputIo.InspectionHeatSink1Present, true);
        io.SetInput(InputIo.MainConveyorReadyFromRear, true);
        await work.Station.SeatAsync(CancellationToken.None);
        var assembly = work.Station.GetAssembly(HeatSinkSlot.HeatSink1);
        assembly.RecordBoltPresence(1, !ng);
        assembly.CompleteInspection();
        work.Station.Complete(work.Station.CurrentJob);

        var expectNg = inspectionEnabled && ng;
        Assert.Equal(expectNg, inspection.GetNextStep() == InspectionStationState.PickingCarrier);
        Assert.Equal(!expectNg, conveyor.GetNextStep(io.GetOutput(OutputIo.MainConveyorRun)) == MainConveyorState.DischargingInspectionCarrier);
        await machine.ShutdownAsync();
    }

    [Theory]
    [InlineData(FasteningHead.Pickup)]
    [InlineData(FasteningHead.Shooting)]
    public async Task ManualBoltTestIsAvailableAfterInterruptedProduction(FasteningHead selected)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.BoltFastening);
        settings.Units.PickupBoltFeeder = selected == FasteningHead.Pickup;
        settings.Units.ShootingBoltFeeder = false; // Supply is outside this result-ownership test.
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var station = services.GetRequiredService<BoltFasteningStation>();
        var recipe = services.GetRequiredService<RecipeManager>().Current;
        recipe.Pcb.BoltPoints = [new() { Number = 1, Head = selected, X = 0, Y = 0 }];
        var bus = services.GetRequiredKeyedService<IAdcBus>(selected);
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
            (InputIo.ShootingHeadVacuumDetected, true),
            (InputIo.PickupTableDown, selected == FasteningHead.Pickup),
            (InputIo.PickupTableUp, selected == FasteningHead.Shooting));
        using var stop = new CancellationTokenSource();
        void StopWhenStarted(OutputIo output, bool on)
        {
            if (on && output == (selected == FasteningHead.Pickup ? OutputIo.PickupBoltStart : OutputIo.ShootingBoltStart))
                stop.Cancel();
        }

        io.OutputChanged += StopWhenStarted;
        try
        {
            await station.RunAsync(stop.Token).WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            stop.Cancel();
            io.OutputChanged -= StopWhenStarted;
        }
        var interruptedEvent = (await bus.ReadFasteningResultAsync(slave)).EventCount;

        settings.Units.PickupBoltFeeder = false;
        settings.Units.ShootingBoltFeeder = false;

        using var diagnostics = new AdcProtocolViewModel(
            services.GetRequiredKeyedService<IAdcBus>(FasteningHead.Pickup),
            services.GetRequiredKeyedService<IAdcBus>(FasteningHead.Shooting), services.GetRequiredService<IIoService>(), settings.Hantas, machine, state)
        {
            SelectedHead = selected,
            SlaveText = slave.ToString(),
        };
        await diagnostics.StartCommand.ExecuteAsync(null);
        Assert.StartsWith("OK", diagnostics.ResultMessage);
        Assert.True(services.GetRequiredService<MachineState>().ManualSetupEnabled);
        Assert.True(machine.IsUseAdcProtocolAllowed);
        Assert.False(state.IsRunning);
        Assert.Equal(MachineAlarm.None, state.Alarm);
        Assert.Equal(interruptedEvent + 1, (await bus.ReadFasteningResultAsync(slave)).EventCount);

        state.SetError(MachineAlarm.BoltFastening);
        await machine.ResetAsync();
        Assert.Equal(MachineAlarm.None, state.Alarm);
        Assert.Equal(StartBlockReason.None, machine.StartBlock);
        Assert.True(services.GetRequiredService<MachineState>().ManualSetupEnabled);
        Assert.Equal(interruptedEvent + 1, (await bus.ReadFasteningResultAsync(slave)).EventCount);
    }

    [Fact]
    public async Task AdcDiagnosticCancellationTurnsStartOffAndReleasesMachineLock()
    {
        var settings = new MachineSettings { Units = EnableOnly(MachineUnit.NgConveyor) };
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        await machine.InitializeAsync();
        var io = new VirtualIoService(VirtualTest.Outputs(), new());
        var bus = new AdcControllerStub { SuppressCompletion = true };
        bus.BindIo(io, FasteningHead.Pickup);
        using var diagnostics = new AdcProtocolViewModel(bus, new VirtualAdcBus(), io, settings.Hantas, machine, state);
        var testing = diagnostics.StartCommand.ExecuteAsync(null);
        Assert.True(state.IsRunning);
        Assert.Throws<InvalidOperationException>(() => diagnostics.SelectedHead = FasteningHead.Shooting);
        Assert.True(await VirtualTest.WaitUntilAsync(
            () => io.GetOutput(OutputIo.PickupBoltStart), TimeSpan.FromSeconds(2)));
        Assert.True(io.GetOutput(OutputIo.PickupBoltStart));
        machine.Stop();
        await testing.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
        Assert.Equal(1, bus.StopWrites);
        Assert.False(state.IsRunning);
    }

    [Fact]
    public async Task AdcDiagnosticsSelectsTheMatchingPortAndDisconnectsOnlyThatHead()
    {
        var settings = new MachineSettings { Units = EnableOnly(MachineUnit.NgConveyor) };
        settings.Hantas.PickupBaudRate = 19200;
        settings.Hantas.ShootingBaudRate = 38400;
        settings.Hantas.PickupSlaveAddress = 1;
        settings.Hantas.ShootingSlaveAddress = 1;
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var pickup = services.GetRequiredKeyedService<IAdcBus>(FasteningHead.Pickup);
        var shooting = services.GetRequiredKeyedService<IAdcBus>(FasteningHead.Shooting);
        await machine.InitializeAsync();
        using var diagnostics = new AdcProtocolViewModel(
            pickup, shooting, services.GetRequiredService<IIoService>(), settings.Hantas, machine, services.GetRequiredService<MachineState>());
        try
        {
            diagnostics.SelectedPort = "Virtual";
            await diagnostics.ToggleConnectionCommand.ExecuteAsync(null);
            Assert.True(pickup.IsOpen);
            Assert.False(shooting.IsOpen);
            Assert.Equal(19200, pickup.BaudRate);
            await pickup.WriteRegisterAsync(1, (ushort)AdcRemoteRegister.Preset, 7);
            diagnostics.SelectedHead = FasteningHead.Shooting;
            Assert.Equal("Connect", diagnostics.ConnectionAction);
            Assert.Equal(38400, diagnostics.SelectedBaudRate);
            Assert.Equal("1", diagnostics.SlaveText);
            diagnostics.SelectedPort = "Virtual";
            await diagnostics.ToggleConnectionCommand.ExecuteAsync(null);
            await diagnostics.SelectPresetCommand.ExecuteAsync(null);
            await diagnostics.ReadResultCommand.ExecuteAsync(null);
            Assert.StartsWith("Last result:", diagnostics.ResultMessage);
            Assert.Equal((ushort)1, (await shooting.ReadControllerStatusAsync(1)).Preset);
            Assert.Equal((ushort)7, (await pickup.ReadControllerStatusAsync(1)).Preset);
            Assert.True(pickup.IsOpen);
            Assert.Equal(38400, shooting.BaudRate);
            await diagnostics.ToggleConnectionCommand.ExecuteAsync(null);
            Assert.False(shooting.IsOpen);
            Assert.True(pickup.IsOpen);
            diagnostics.SelectedHead = FasteningHead.Pickup;
            Assert.Equal("Disconnect", diagnostics.ConnectionAction);
            Assert.Equal(19200, diagnostics.SelectedBaudRate);
        }
        finally
        {
            await machine.ShutdownAsync();
        }
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
        Assert.True(services.GetRequiredService<BoltFasteningStation>().Station.CarrierSeated);

        var run = machine.StartAsync();
        try
        {
            Assert.True(
                await VirtualTest.WaitUntilAsync(() => head.Started.Task.IsCompleted, TimeSpan.FromSeconds(3)),
                $"Fastening did not start: block={machine.StartBlock}, alarm={state.Alarm}, "
                    + $"station={services.GetRequiredService<BoltFasteningStation>().GetNextStep()}. {state.AlarmDetail}");
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
            Units = EnableOnly(MachineUnit.NgConveyor),
        };
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<VirtualIoService>();
        var shuttle = services.GetRequiredService<NgCarrierConveyor>();
        var gantry = services.GetRequiredService<InspectionStation>();
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
            Assert.Equal(NgConveyorState.WaitingForTransferRelease, shuttle.Step);
            Assert.False(io.GetOutput(OutputIo.NgShuttleDown));
            Assert.False(gantry.Motion.Feedback.GetAxisState(MotionAxis.X).ServoOn);
            Assert.False(gantry.Motion.Feedback.GetAxisState(MotionAxis.X).Homed);

            io.SetInput(InputIo.NgCarrierGripperClosed, false);
            io.SetInput(InputIo.NgCarrierGripperOpen, true);
            Assert.Equal(NgConveyorState.WaitingForTransferRelease, shuttle.Step);
            Assert.False(io.GetOutput(OutputIo.NgShuttleDown));

            io.SetInput(InputIo.NgCarrierPickupDown, false);
            io.SetInput(InputIo.NgCarrierPickupUp, true);
            await ((IIoService)io).WaitForInputAsync(InputIo.NgShuttleDown, true);
            Assert.True(io.GetInput(InputIo.NgCarrierDetected));
            Assert.False(gantry.Motion.Feedback.GetAxisState(MotionAxis.X).ServoOn);
            Assert.False(gantry.Motion.Feedback.GetAxisState(MotionAxis.X).Homed);
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
        settings.Units.Inspection = true;
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
            feederStarted |= output == OutputIo.ShootingFeederOff && !value;
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
        Assert.True(io.GetOutput(OutputIo.ShootingFeederOff));
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
        Assert.True(state.FeedbackReadiness.Homed);

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
        Assert.False(state.FeedbackReadiness.ServosOn);

        io.SetInput(InputIo.AutoMode, true);
        io.SetInput(InputIo.ResetButton, true);
        await WaitUntilAsync(() => !state.IsError);
        io.SetInput(InputIo.ResetButton, false);

        Assert.True(state.FeedbackReadiness.ServosOn);
        Assert.True(state.FeedbackReadiness.Homed);

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
        Assert.True(io.GetOutput(OutputIo.ShootingFeederOff));
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
        Assert.False(state.FeedbackReadiness.Faulted);
        Assert.True(state.FeedbackReadiness.ServosOn);
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

    [Fact]
    public async Task SupplyServoChangeDoesNotRefreshUnrelatedStationTargets()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.PcbSupply);
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var view = services.GetRequiredService<OperationViewModel>();
        var motion = (VirtualMotionService)services.GetRequiredKeyedService<IXyMotion>(MotionGroup.PcbSupply);
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        await WaitUntilAsync(() => state.FeedbackReadiness.Homed && !state.IsRunning);
        view.Activate();
        var notifications = new ConcurrentQueue<string?>();
        view.PropertyChanged += (_, e) => notifications.Enqueue(e.PropertyName);
        try
        {
            motion.SetServo(MotionAxis.X, false);
            await WaitUntilAsync(() => !state.FeedbackReadiness.ServosOn
                && notifications.Contains(nameof(OperationViewModel.MachineDisplayState)));
            Assert.Equal(MachineDisplayState.ServoOff, view.MachineDisplayState);
            Assert.Contains(nameof(OperationViewModel.SupplyDisplayState), notifications);
            Assert.DoesNotContain(nameof(OperationViewModel.BoltTargets), notifications);
            Assert.DoesNotContain(nameof(OperationViewModel.InspectionTargets), notifications);
            Assert.DoesNotContain(nameof(OperationViewModel.ModeText), notifications);
            Assert.DoesNotContain(nameof(OperationViewModel.Alarm), notifications);
        }
        finally
        {
            view.Deactivate();
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task UnchangedFeedbackDoesNotRefreshTheViewAndInputChangesNotifyImmediately()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.MainConveyor);
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var feedback = services.GetRequiredService<MachineFeedbackMonitor>();
        var view = services.GetRequiredService<OperationViewModel>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        view.Activate();
        await view.LoadOlderPcbsCommand.ExecutionTask!;
        var notifications = 0;
        var samples = 0;
        void OnViewChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            Interlocked.Increment(ref notifications);
        }
        void OnSampled(MotionGroup group, MotionFeedbackSample sample)
        {
            Interlocked.Increment(ref samples);
        }
        view.PropertyChanged += OnViewChanged;
        feedback.Sampled += OnSampled;
        try
        {
            await Task.Delay(650);
            Assert.True(Volatile.Read(ref samples) > 0);
            Assert.Equal(0, Volatile.Read(ref notifications));
            io.SetInput(InputIo.MainConveyorEntryCarrierDetected, true);
            Assert.True(Volatile.Read(ref notifications) > 0);
            Assert.True(view.Signals.Inputs[InputIo.MainConveyorEntryCarrierDetected].IsOn);

            await machine.StopAsync();
            var stopped = Volatile.Read(ref notifications);
            io.SetInput(InputIo.MainConveyorEntryCarrierDetected, false);
            Assert.True(Volatile.Read(ref notifications) > stopped);
            Assert.False(view.Signals.Inputs[InputIo.MainConveyorEntryCarrierDetected].IsOn);
        }
        finally
        {
            view.PropertyChanged -= OnViewChanged;
            feedback.Sampled -= OnSampled;
            view.Deactivate();
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task DisplayUsesAcquiredFeedbackForAllStationsAndNeverFallsBackToHardware()
    {
        var settings = FlowSettings();
        var readingDisplay = new AsyncLocal<bool>();
        var unexpectedRead = new InvalidOperationException("Display attempted a hardware read.");
        void BeforeHardwareRead()
        {
            if (readingDisplay.Value)
                throw unexpectedRead;
        }

        await using var services = CreateMotionScopeServices(settings, out var probes, registrations =>
            registrations.AddSingleton<IIoService>(provider =>
            {
                var wrapper = System.Reflection.DispatchProxy.Create<IIoService, IoTests.OutputReadProbe>();
                var probe = (IoTests.OutputReadProbe)wrapper;
                probe.Io = provider.GetRequiredService<VirtualIoService>();
                probe.BeforeRead = BeforeHardwareRead;
                return wrapper;
            }));
        PrepareCarrierTeaching(settings, services.GetRequiredService<RecipeManager>().Current);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        await WaitUntilAsync(() => state.FeedbackReadiness.Homed);
        await services.GetRequiredService<MachineFeedbackMonitor>().StopAsync();
        foreach (var probe in probes.Values)
            probe.BeforeHardwareRead = BeforeHardwareRead;
        try
        {
            foreach (var station in new (InputIo Carrier, InputIo PlateDown, InputIo PlateUp,
                InputIo StopperUp, InputIo StopperDown, InputIo HeatSink)[]
            {
                (InputIo.PcbPlacementCarrierPresent,
                    InputIo.PcbPlacementBackupPlateDown, InputIo.PcbPlacementBackupPlateUp,
                    InputIo.PcbPlacementStopperUp, InputIo.PcbPlacementStopperDown,
                    InputIo.PcbPlacementHeatSink1Present),
                (InputIo.BoltFasteningCarrierPresent,
                    InputIo.BoltFasteningBackupPlateDown, InputIo.BoltFasteningBackupPlateUp,
                    InputIo.BoltFasteningStopperUp, InputIo.BoltFasteningStopperDown,
                    InputIo.BoltFasteningHeatSink1Present),
                (InputIo.InspectionCarrierPresent,
                    InputIo.InspectionBackupPlateDown, InputIo.InspectionBackupPlateUp,
                    InputIo.InspectionStopperUp, InputIo.InspectionStopperDown,
                    InputIo.InspectionHeatSink1Present),
            })
            {
                io.SetInput(station.Carrier, true);
                io.SetInput(station.PlateDown, false);
                io.SetInput(station.PlateUp, true);
                io.SetInput(station.StopperUp, false);
                io.SetInput(station.StopperDown, true);
                io.SetInput(station.HeatSink, true);
            }

            Assert.True(machine.TeachingReady);
            Assert.True(services.GetRequiredService<BoltFasteningStation>().Station.CarrierSeated);
            Assert.True(services.GetRequiredService<InspectionStation>().Station.CarrierSeated);
            state.AutomaticRunning = true;
            readingDisplay.Value = true;
            var display = services.GetRequiredService<OperationViewModel>();
            Assert.True(state.Available);
            // A machine-level flag cannot invent an executing step in an idle unit.
            Assert.Null(display.FasteningState);
            Assert.Null(display.InspectionState);
            Assert.Null(display.Placement.ActivePcb);
            Assert.Null(display.BoltFasteningActiveBolt);
            Assert.Null(services.GetRequiredService<BoltFasteningStation>().ActiveBolt);
            Assert.Null(services.GetRequiredService<InspectionStation>().ActiveBolt);
            Assert.Null(services.GetRequiredService<InspectionStation>().ActivePcb);
            Assert.Same(unexpectedRead, Assert.Throws<InvalidOperationException>(() =>
                MotionService.IsAt(services.GetRequiredService<PcbSupplier>().Motion.Feedback, settings.PcbSupply.HandoffPosition)));
            Assert.Same(unexpectedRead, Assert.Throws<InvalidOperationException>(
                () => services.GetRequiredService<IIoService>().GetOutput(OutputIo.MainConveyorRun)));

            readingDisplay.Value = false;
            io.SetInput(InputIo.InspectionBackupPlateUp, false);
            io.SetInput(InputIo.InspectionBackupPlateDown, true);
            io.SetInput(InputIo.InspectionStopperDown, false);
            io.SetInput(InputIo.InspectionStopperUp, true);
            var work = services.GetRequiredService<InspectionStation>();
            work.RequestInspection(work.Station.CurrentJob);
            readingDisplay.Value = true;
            Assert.Null(display.ConveyorState);
            Assert.Null(display.InspectionState);
            Assert.Null(display.InspectionActivePcb);
            _ = display.InspectionActiveBolt;

            // Unavailable sampled feedback must remain unknown instead of reading the SDK.
            services.GetRequiredService<PcbPlacer>().Motion.InvalidateFeedback(new IOException("Lost sample."));
            Assert.Null(display.PlacementState);
        }
        finally
        {
            readingDisplay.Value = false;
            state.AutomaticRunning = false;
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(MotionFeedbackFault.Alarm)]
    [InlineData(MotionFeedbackFault.ServoOff)]
    [InlineData(MotionFeedbackFault.HomeLost)]
    [InlineData(MotionFeedbackFault.ReadFailure)]
    public async Task AutomaticFeedbackStopsOnSilentMotionFaultWithoutAView(MotionFeedbackFault fault)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.Inspection);
        settings.Units.MainConveyor = true;
        await using var services = CreateMotionScopeServices(settings, out var probes);
        PrepareCarrierTeaching(settings, services.GetRequiredService<RecipeManager>().Current);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var active = probes[MotionGroup.InspectionGantry];
        foreach (var probe in probes.Where(item => item.Key != MotionGroup.InspectionGantry).Select(
            item => item.Value))
        {
            probe.ReportReady = true;
            probe.AllowStop = true;
            probe.FailHardwareCalls = true; // Disabled hardware must not be sampled, even while AUTO polls.
        }

        Task? run = null;
        try
        {
            await machine.InitializeAsync();
            await machine.HomeAsync(CancellationToken.None);
            io.SetInput(InputIo.AutoMode, false);
            Assert.True(
                await VirtualTest.WaitUntilAsync(() => machine.IsStartAllowed, TimeSpan.FromSeconds(2)),
                $"START blocked: {machine.StartBlock}; busy={state.IsRunning}; alarm={state.AlarmDetail}");
            run = machine.StartAsync();
            Assert.True(
                await VirtualTest.WaitUntilAsync(() => state.AutomaticRunning, TimeSpan.FromSeconds(2)),
                $"AUTO did not start: block={machine.StartBlock}, alarm={state.Alarm}. {state.AlarmDetail}");
            Assert.False(run.IsCompleted);
            Assert.Equal(MachineAlarm.None, state.Alarm);
            if (fault == MotionFeedbackFault.ReadFailure)
                // Isolate the monitor: a simultaneous command read failure has its own unit alarm.
                active.DiagnosticReadError = new IOException("Unavailable diagnostic feedback.");
            else
                active.OverrideState = value => fault switch
                {
                    MotionFeedbackFault.Alarm => value with { Alarm = true },
                    MotionFeedbackFault.HomeLost => value with { Homed = false },
                    MotionFeedbackFault.ServoOff => value with { ServoOn = false },
                    _ => throw new ArgumentOutOfRangeException(nameof(fault)),
                };
            // No StateChanged, DI changes, UI timer or explicit refresh request accompanies this fault.
            await run.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(MachineAlarm.MotionUnavailable, state.Alarm);
            Assert.False(state.AutomaticRunning);
            Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
            Assert.False(io.GetOutput(OutputIo.MainConveyorReadyToFront2));
            Assert.False(io.GetOutput(OutputIo.MainConveyorAvailableToRear));
            await WaitUntilAsync(
                () => !services.GetRequiredService<OperationCancellation>().HasActiveOperations);
            Assert.All(
                probes.Where(item => item.Key != MotionGroup.InspectionGantry),
                item => Assert.Equal(0, item.Value.HardwareCalls));
            active.DiagnosticReadError = null;
            active.OverrideState = null;
            await WaitUntilAsync(() => !state.FeedbackReadiness.Faulted);
            Assert.Equal(MachineAlarm.MotionUnavailable, state.Alarm); // Recovery never restarts AUTO.
        }
        finally
        {
            active.DiagnosticReadError = null;
            active.OverrideState = null;
            // Shutdown verifies every axis, including disabled groups.
            foreach (var probe in probes.Values)
                probe.FailHardwareCalls = false;
            await machine.ShutdownAsync();
            if (run is not null)
                await run.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task HomeAndAutomaticStartIgnorePreStartSample()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.Inspection);
        await using var services = CreateDisplayServices(out var motion, settings);
        PrepareCarrierTeaching(settings, services.GetRequiredService<RecipeManager>().Current);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var feedback = services.GetRequiredService<MachineFeedbackMonitor>();
        await machine.InitializeAsync();
        Assert.False(state.FeedbackReadiness.Homed);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var released = new ManualResetEventSlim();
        var blocked = 0;
        motion.AfterDiagnosticStateRead = axis =>
        {
            if (axis != MotionAxis.X || Interlocked.Exchange(ref blocked, 1) != 0)
                return;
            entered.TrySetResult();
            released.Wait();
        };
        var sampled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void ObserveAutomaticSample(MotionGroup group, MotionFeedbackSample sample)
        {
            if (state.AutomaticRunning
                && group == MotionGroup.InspectionGantry
                && sample.Readiness.Homed)
                sampled.TrySetResult();
        }

        Task? run = null;
        feedback.Sampled += ObserveAutomaticSample;
        try
        {
            // The X sample says not homed, but its delivery is delayed across Home and Start.
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await machine.HomeAsync(CancellationToken.None);
            Assert.False(machine.IsStartAllowed);
            run = machine.StartAsync();
            await WaitUntilAsync(() => state.AutomaticRunning);
            released.Set();
            await sampled.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(state.AutomaticRunning);
            Assert.False(run.IsCompleted);
            Assert.Equal(MachineAlarm.None, state.Alarm);
            Assert.True(state.FeedbackReadiness.Homed);
        }
        finally
        {
            released.Set();
            motion.AfterDiagnosticStateRead = null;
            feedback.Sampled -= ObserveAutomaticSample;
            await machine.ShutdownAsync();
            if (run is not null)
                await run.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task DisplayReadFailureIsVisibleAndDoesNotReplaceLiveAdmissionChecks()
    {
        await using var services = CreateDisplayServices(out var feedback);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        await machine.InitializeAsync();
        Assert.True(state.Available);
        var view = services.GetRequiredService<OperationViewModel>();
        var messages = new ConcurrentQueue<string?>();
        var modes = new ConcurrentQueue<string>();
        view.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(OperationViewModel.AlarmMessage))
                messages.Enqueue(view.AlarmMessage);
            if (e.PropertyName == nameof(OperationViewModel.ModeText))
                modes.Enqueue(view.ModeText);
        };
        var error = new IOException("Display feedback unavailable.");
        feedback.BeforeRead = () => throw error;
        feedback.DiagnosticReadError = error;
        // A ready display is not permission to operate when the actual read fails.
        Assert.Throws<IOException>(() => state.MotionReadiness);
        await WaitUntilAsync(() => !state.Available && messages.Contains(error.Message)
            && modes.Contains("UNKNOWN"));
        Assert.Same(error, state.ReadError);
        Assert.Equal(
            MachineDisplayState.Unavailable,
            view.ConveyorStatus);
        Assert.False(machine.IsHomeAllowed);
        Assert.False(machine.IsStartAllowed);
        Assert.Equal(MachineAlarm.None, state.Alarm);
        Assert.All(
            services.GetRequiredService<InspectionStation>().Motion.Axes.Values,
            axis => Assert.Equal(AxisCondition.Unavailable, axis.Condition));

        var nextError = new IOException(error.Message, new InvalidOperationException());
        feedback.DiagnosticReadError = nextError;
        feedback.BeforeRead = () => throw nextError;
        await WaitUntilAsync(() => ReferenceEquals(nextError, state.ReadError));

        feedback.BeforeRead = null;
        feedback.DiagnosticReadError = null;
        await WaitUntilAsync(() => state.Available && messages.Contains(null)
            && modes.Contains("MANUAL"));
        Assert.Null(state.ReadError);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManualServoFailureStaysAtTheCommandBoundary(bool emergencyStop)
    {
        await using var services = CreateServices(FlowSettings());
        await services.GetRequiredService<MachineController>().InitializeAsync();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var manual = services.GetRequiredService<MotionWindowViewModel>();
        var row = manual.Axes.Single(
            axis => axis.Group == MotionGroup.InspectionGantry && axis.Axis == MotionAxis.X);
        var motion = services.GetRequiredService<InspectionStation>().Motion.Feedback;
        void FailOnce()
        {
            motion.StateChanged -= FailOnce;
            if (emergencyStop)
            {
                io.SetInput(InputIo.EmergencyStop1Pressed, true);
                Assert.Equal(MachineAlarm.EmergencyStop, state.Alarm);
            }

            throw new IOException("Servo feedback failed.");
        }

        motion.StateChanged += FailOnce;

        Assert.True(row.ToggleServoCommand.CanExecute(null));
        row.ToggleServoCommand.Execute(null);
        Assert.Equal(emergencyStop ? MachineAlarm.EmergencyStop : MachineAlarm.MotionUnavailable, state.Alarm);
        Assert.Contains("Servo feedback failed", state.AlarmDetail);
        await WaitUntilAsync(() => row.Diagnostics.Snapshot.State?.ServoOn == false);
        Assert.False(row.Diagnostics.Snapshot.State?.ServoOn);
    }

    [Fact]
    public async Task ManualCommandAfterShutdownDoesNotEscapeTheBoundary()
    {
        await using var services = CreateServices(FlowSettings());
        var teaching = services.GetRequiredService<TeachingViewModel>();
        teaching.SelectedTeachingUnit = HardwareArea.PcbSupply;
        var operations = services.GetRequiredService<OperationCancellation>();
        await operations.ShutdownAsync();
        await teaching.JogCommand.ExecuteAsync(TeachingDirection.XPlus);

        Assert.False(services.GetRequiredService<PcbSupplier>().Motion.Feedback.IsMoving);
        Assert.False(operations.HasActiveOperations);
        Assert.Equal(MachineAlarm.None, services.GetRequiredService<MachineState>().Alarm);
    }

    public enum MotionFeedbackFault
    {
        Alarm,
        ServoOff,
        HomeLost,
        ReadFailure,
    }

    [Theory]
    [InlineData(InputIo.PickupFeederBoltDetected, MachineAlarm.PickupBoltFeeder)]
    [InlineData(InputIo.ShootingFeederBoltDetected, MachineAlarm.ShootingBoltFeeder)]
    public async Task SharedFeederReportsEmptySideDespiteOtherFeederChanges(InputIo emptyInput, MachineAlarm alarm)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.PickupBoltFeeder);
        settings.Units.ShootingBoltFeeder = true;
        settings.BoltFeeder.PickupTimeoutMilliseconds = 200;
        settings.BoltFeeder.ShootingTimeoutMilliseconds = 200;
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        io.AutoResponseEnabled = false;
        io.SetInput(InputIo.ShootingEscapeForward, false);
        io.SetInput(InputIo.ShootingEscapeBackward, true);
        var otherInput = emptyInput == InputIo.PickupFeederBoltDetected
            ? InputIo.ShootingFeederBoltDetected : InputIo.PickupFeederBoltDetected;
        io.SetInput(emptyInput, false);
        io.SetInput(otherInput, true);
        Assert.True(machine.IsStartAllowed, machine.StartBlock.ToString());
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var run = machine.StartAsync(stop.Token);
        try
        {
            // Repeated edges from one feeder cannot extend the other feeder's empty deadline.
            for (var change = 0; change < 20 && !state.IsError; change++)
            {
                io.SetInput(otherInput, false);
                io.SetInput(otherInput, true);
                await Task.Delay(30);
            }
            Assert.Equal(alarm, state.Alarm);
            await run.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(io.GetOutput(OutputIo.ShootingFeederOff));
            Assert.Contains(emptyInput.GetDescription(), state.AlarmDetail);
        }
        finally
        {
            machine.Stop();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    [InlineData(true, true, true)]
    public async Task FeedersOffOrRepeatKeepPickupMotionAndStartBothIoHeads(
        bool pickupEnabled, bool shootingEnabled, bool repeat)
    {
        var settings = FlowSettings();
        settings.Drivers.Bolt = BoltDriver.Virtual;
        settings.Units = EnableOnly(MachineUnit.BoltFastening);
        settings.Units.PickupBoltFeeder = pickupEnabled;
        settings.Units.ShootingBoltFeeder = shootingEnabled;
        var pickupFeeding = pickupEnabled && !repeat;
        var shootingFeeding = shootingEnabled && !repeat;
        await using var services = CreateServices(settings);
        var recipe = services.GetRequiredService<RecipeManager>().Current;
        recipe.Pcb.BoltPoints = [
            new() { Number = 1, Head = FasteningHead.Shooting, X = 10, Y = 10,
                FasteningX = 10.5, FasteningY = 9.5, FasteningZOffset = 0.25 },
            new() { Number = 2, Head = FasteningHead.Pickup, X = 20, Y = 10,
                FasteningX = 21, FasteningY = 11, FasteningZOffset = -0.5 },
            new() { Number = 3, Head = FasteningHead.Pickup, X = 30, Y = 10 },
        ];
        settings.BoltFastening.ShootingHead.FasteningZ = 8;
        settings.BoltFastening.PickupHead.FasteningZ = 12;
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var work = services.GetRequiredService<BoltFasteningStation>().Station;
        var gantry = services.GetRequiredService<BoltFasteningStation>();
        var outputs = new ConcurrentQueue<(OutputIo Output, bool On)>();
        var starts = new ConcurrentQueue<(FasteningHead Head, double X, double Y, double Z)>();
        var descents = new ConcurrentQueue<(FasteningHead Head, double X, double Y, double Z)>();
        var pickups = new ConcurrentQueue<(double X, double Y, double Z)>();
        var visitedPickupFeeder = false;
        var feederRan = false;
        services.GetRequiredService<BoltFeederUnit>().Trace += message => feederRan = true;
        await machine.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(3));
        await machine.HomeAsync(CancellationToken.None);
        io.SetInput(InputIo.PickupFeederBoltDetected, pickupFeeding);
        io.SetInput(InputIo.ShootingFeederBoltDetected, shootingFeeding);
        if (!shootingFeeding)
            io.SetInput(InputIo.ShootingTubeBoltDetected, true); // Disabled supply does not wait for the tube.
        io.SetInput(InputIo.AutoMode, repeat);
        state.RepeatEnabled = repeat;
        io.SetInput(InputIo.BoltFasteningHeatSink1Present, true);
        await work.SeatAsync(CancellationToken.None);
        // Standalone fastening starts from plate UP even if the stopper is still UP.
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.BoltFasteningStopperUp, true);
        gantry.Motion.Feedback.PositionChanged += (x, y, _) =>
        {
            if (Math.Abs(x - settings.BoltFastening.PickupPosition.X) < 0.01
                && Math.Abs(y - settings.BoltFastening.PickupPosition.Y) < 0.01)
                visitedPickupFeeder = true;
        };
        io.OutputChanged += (output, on) =>
        {
            outputs.Enqueue((output, on));
            if (on && output == OutputIo.PickupHeadVacuumPump)
            {
                Assert.True(MotionService.IsAt(gantry.Motion.Feedback, settings.BoltFastening.PickupPosition));
                Assert.Equal(StationCylinderState.Up, gantry.PickupHeadPosition);
                Assert.Equal(StationCylinderState.Up, gantry.ShootingHeadPosition);
                Assert.Equal(StationCylinderState.Down, gantry.PickupTablePosition);
                var position = gantry.Motion.Feedback.Position;
                pickups.Enqueue((position.X, position.Y, position.Z));
            }
            if (on && output is OutputIo.ShootingBoltStart or OutputIo.PickupBoltStart)
            {
                Assert.True(gantry.IsHorizontalMoveAllowed); // START precedes cylinder descent.
                var shooting = output == OutputIo.ShootingBoltStart;
                Assert.True(io.GetOutput(shooting ? OutputIo.ShootingBoltPreset1 : OutputIo.PickupBoltPreset1));
                Assert.False(io.GetOutput(shooting ? OutputIo.ShootingBoltPreset2 : OutputIo.PickupBoltPreset2));
                Assert.False(io.GetOutput(shooting ? OutputIo.ShootingBoltPreset3 : OutputIo.PickupBoltPreset3));
                var position = gantry.Motion.Feedback.Position;
                starts.Enqueue((output == OutputIo.ShootingBoltStart ? FasteningHead.Shooting : FasteningHead.Pickup,
                    position.X, position.Y, position.Z));
            }
            if (on && output is OutputIo.ShootingHeadDown or OutputIo.PickupHeadDown)
            {
                var position = gantry.Motion.Feedback.Position;
                Assert.True(io.GetOutput(output == OutputIo.ShootingHeadDown
                    ? OutputIo.ShootingBoltStart : OutputIo.PickupBoltStart));
                descents.Enqueue((output == OutputIo.ShootingHeadDown ? FasteningHead.Shooting : FasteningHead.Pickup,
                    position.X, position.Y, position.Z));
            }
        };
        work.Changed += () =>
        {
            if (work.Completed)
                machine.Stop();
        };
        try
        {
            Assert.Equal(repeat, state.RepeatEnabled);
            Assert.True(machine.IsStartAllowed, machine.StartBlock.ToString());
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await machine.StartAsync(timeout.Token).WaitAsync(TimeSpan.FromSeconds(12));
            Assert.True(state.Alarm == MachineAlarm.None, state.AlarmDetail);
            Assert.True(work.Completed, services.GetRequiredService<BoltFasteningStation>().GetNextStep().ToString());
            var assembly = Assert.Single(work.Assemblies);
            Assert.Equal(shootingFeeding ? BoltResultSource.Controller : BoltResultSource.DryRun,
                Assert.Single(assembly.PcbBoltResults).Value.Source);
            Assert.Equal(2, assembly.PickupBoltResults.Count);
            Assert.All(assembly.PickupBoltResults.Values, result =>
                Assert.Equal(pickupFeeding ? BoltResultSource.Controller : BoltResultSource.DryRun, result.Source));
            Assert.All(assembly.PcbBoltResults.Values.Concat(assembly.PickupBoltResults.Values), result =>
                Assert.Equal(result.Source == BoltResultSource.Controller, result.Torque is not null));
            Assert.Equal(AssemblyResult.Ok, assembly.FasteningResult);
            var positions = new[]
            {
                (FasteningHead.Shooting, 10.5, 9.5, 8.25),
                (FasteningHead.Pickup, 21d, 11d, 11.5),
                (FasteningHead.Pickup, 30d, 10d, 12d),
            };
            Assert.Equal(positions, descents.ToArray());
            Assert.Equal(positions, starts.ToArray());
            var operation = services.GetRequiredService<OperationViewModel>();
            Assert.All(operation.BoltTargets, bolt => Assert.Equal(BoltTargetState.Ok, bolt.State));
            Assert.True(visitedPickupFeeder);
            if (pickupFeeding)
                Assert.Equal(new[] { (100d, 50d, 10d), (100d, 50d, 10d) }, pickups.ToArray());
            else
                Assert.Empty(pickups);
            Assert.Equal(pickupEnabled, settings.Units.PickupBoltFeeder);
            Assert.Equal(shootingEnabled, settings.Units.ShootingBoltFeeder);
            Assert.Equal(pickupFeeding || shootingFeeding, feederRan);
            if (!pickupFeeding)
            {
                Assert.False(io.GetInput(InputIo.PickupFeederBoltDetected));
                Assert.False(io.GetInput(InputIo.PickupHeadVacuumDetected));
            }
            if (!shootingFeeding)
                Assert.DoesNotContain(outputs, command =>
                    command.On && command.Output is OutputIo.ShootingHeadVacuumPump
                        or OutputIo.ShootBolt or OutputIo.ShootingEscapeForward
                    || !command.On && command.Output == OutputIo.ShootingFeederOff);
            Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
            Assert.False(io.GetOutput(OutputIo.PickupHeadVacuumPump));
            Assert.False(io.GetOutput(OutputIo.ShootingBoltStart));
            Assert.True(io.GetInput(InputIo.PickupHeadUp));
            Assert.True(io.GetInput(InputIo.ShootingHeadUp));
            recipe.Pcb.BoltPoints[0].FasteningX = null;
            Assert.False(machine.TeachingReady); // Feeder OFF still requires taught fastening coordinates.
        }
        finally
        {
            machine.Stop();
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(FasteningHead.Shooting, BoltDriver.Virtual, DryRunEnd.Completed)]
    [InlineData(FasteningHead.Shooting, BoltDriver.Virtual, DryRunEnd.Cancelled)]
    [InlineData(FasteningHead.Shooting, BoltDriver.Virtual, DryRunEnd.MissingUpFeedback)]
    [InlineData(FasteningHead.Pickup, BoltDriver.Virtual, DryRunEnd.Completed)]
    public async Task FasteningWithoutDownFeedbackStillRequiresUpFeedbackAndStopsOnCancellation(
        FasteningHead head, BoltDriver driver, DryRunEnd end)
    {
        var settings = FlowSettings();
        settings.Drivers.Bolt = driver;
        settings.Units = EnableOnly(MachineUnit.BoltFastening);
        var stopDuringDescent = end == DryRunEnd.Cancelled;
        var missingUpFeedback = end == DryRunEnd.MissingUpFeedback;
        await using var services = CreateServices(settings);
        var recipe = services.GetRequiredService<RecipeManager>().Current;
        recipe.Pcb.BoltPoints = [new() { Number = 1, Head = head, X = 10, Y = 10 }];
        var machine = services.GetRequiredService<MachineController>();
        var station = services.GetRequiredService<BoltFasteningStation>();
        var work = services.GetRequiredService<BoltFasteningStation>().Station;
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        io.SetInput(InputIo.BoltFasteningHeatSink1Present, true);
        await work.SeatAsync(CancellationToken.None);
        if (head == FasteningHead.Pickup)
            await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.PickupTableDown, true, CancellationToken.None);
        settings.Options.TimeoutMilliseconds = 100;
        io.AutoResponseEnabled = false;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var descended = false;
        var raising = false;
        var (start, cylinder, up, down) = head == FasteningHead.Pickup
            ? (OutputIo.PickupBoltStart, OutputIo.PickupHeadDown,
                InputIo.PickupHeadUp, InputIo.PickupHeadDown)
            : (OutputIo.ShootingBoltStart, OutputIo.ShootingHeadDown,
                InputIo.ShootingHeadUp, InputIo.ShootingHeadDown);
        io.OutputChanged += (output, on) =>
        {
            if (output != cylinder)
                return;
            if (on)
            {
                Assert.True(io.GetOutput(start));
                descended = true;
                io.SetInputs((up, false), (down, false));
                if (stopDuringDescent)
                    stop.Cancel();
            }
            else if (descended)
            {
                raising = true;
                io.SetInput(up, !missingUpFeedback);
            }
        };
        work.Changed += () =>
        {
            if (work.Completed)
                stop.Cancel();
        };
        try
        {
            var run = station.RunAsync(stop.Token);
            if (missingUpFeedback)
                await Assert.ThrowsAsync<IoTimeoutException>(() => run);
            else
                await run.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(descended);
            Assert.False(io.GetInput(down));
            Assert.Equal(!stopDuringDescent, raising);
            Assert.Equal(!stopDuringDescent && !missingUpFeedback, work.Completed);
            var assembly = Assert.Single(work.Assemblies);
            var results = head == FasteningHead.Pickup ? assembly.PickupBoltResults : assembly.PcbBoltResults;
            if (stopDuringDescent)
                Assert.Empty(results);
            else
                Assert.Equal(BoltResultSource.DryRun,
                    Assert.Single(results).Value.Source);
            Assert.False(io.GetOutput(start));
        }
        finally
        {
            stop.Cancel();
            await machine.ShutdownAsync();
        }
    }

    public enum DryRunEnd
    {
        Completed,
        Cancelled,
        MissingUpFeedback,
    }

    [Fact]
    public async Task DisabledPickupFeederKeepsLiftInterlockBeforeNewCarrier()
    {
        var settings = FlowSettings();
        settings.Drivers.Bolt = BoltDriver.Virtual;
        settings.Units = EnableOnly(MachineUnit.BoltFastening);
        await using var services = CreateServices(settings);
        var recipe = services.GetRequiredService<RecipeManager>().Current;
        recipe.Pcb.BoltPoints = [new() { Number = 1, Head = FasteningHead.Pickup, X = 20, Y = 10 }];
        var machine = services.GetRequiredService<MachineController>();
        var station = services.GetRequiredService<BoltFasteningStation>();
        var work = services.GetRequiredService<BoltFasteningStation>().Station;
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        io.SetInput(InputIo.PickupFeederBoltDetected, false);
        io.SetInput(InputIo.BoltFasteningHeatSink1Present, true);
        await work.SeatAsync(CancellationToken.None);
        var pickups = 0;
        var starts = 0;
        var vacuumRequested = false;
        io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.PickupBoltStart && on)
            {
                Assert.True(station.IsHorizontalMoveAllowed);
                starts++;
            }
            vacuumRequested |= output == OutputIo.PickupHeadVacuumPump && on;
        };
        var atPickup = false;
        station.Motion.Feedback.StateChanged += () =>
        {
            var wasAtPickup = atPickup;
            atPickup = MotionService.IsAt(station.Motion.Feedback, settings.BoltFastening.PickupPosition);
            if (atPickup && !wasAtPickup)
            {
                Assert.Equal(StationCylinderState.Up, station.PickupHeadPosition);
                pickups++;
                if (pickups == 1)
                {
                    settings.Options.TimeoutMilliseconds = 100;
                    io.AutoResponseEnabled = false;
                    // External loss of UP feedback must still block travel after pickup.
                    io.SetInputs((InputIo.PickupHeadUp, false), (InputIo.PickupHeadDown, true));
                }
            }
        };
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<IoTimeoutException>(() => station.RunAsync(timeout.Token));
            Assert.Equal(1, pickups);
            Assert.Equal(0, starts);
            Assert.False(vacuumRequested);
            Assert.True(station.IsAtPickupXY);
            Assert.True(station.IsAtSafeZ);
            Assert.Equal(StationCylinderState.Down, station.PickupHeadPosition);
            Assert.False(io.GetInput(InputIo.PickupHeadVacuumDetected));
            Assert.Empty(work.GetAssembly(HeatSinkSlot.HeatSink1).PickupBoltResults);

            settings.Options.TimeoutMilliseconds = 2_000;
            io.AutoResponseEnabled = true;
            VirtualTest.SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, false);
            await station.SetVacuumAsync(FasteningHead.Pickup, false, CancellationToken.None);
            VirtualTest.SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, true);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            work.Changed += () =>
            {
                if (work.Completed)
                    stop.Cancel();
            };
            await station.RunAsync(stop.Token);
            Assert.True(work.Completed);
            Assert.Equal(2, pickups);
            Assert.False(vacuumRequested);
            Assert.False(io.GetInput(InputIo.PickupHeadVacuumDetected));
            Assert.Equal(1, starts); // Only the new carrier reaches fastening START.
            Assert.Equal(AssemblyResult.Ok, work.GetAssembly(HeatSinkSlot.HeatSink1).FasteningResult);
            Assert.True(station.IsHorizontalMoveAllowed);
            Assert.False(io.GetOutput(OutputIo.PickupHeadVacuumPump));
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }

    [Trait("Category", "MachineFlow")]
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Station3ConfigurationRunsWithoutUpstreamHardware(bool missingBolts)
    {
        var settings = new MachineSettings
        {
            Units = new()
            {
                MainConveyor = true,
                Inspection = true,
                NgConveyor = true,
                PcbSupply = false,
                PcbPlacement = false,
                BoltFastening = false,
                PickupBoltFeeder = false,
                ShootingBoltFeeder = false,
            },
        };
        FastHomes(settings);
        settings.InspectionGantry.Motion = FastMotion();
        settings.CarrierReference.UpperLeftLocatingPin = new() { X = 0, Y = 0 };
        settings.CarrierReference.LowerRightLocatingPin = new() { X = 100, Y = 100 };
        settings.NgCarrierTransfer.CarrierPickupPosition = new() { X = 5, Y = 20 };
        settings.NgCarrierTransfer.ShuttlePlacePosition = new() { X = 100, Y = 20 };
        settings.NgCarrierTransfer.WaitingPosition = new() { X = 5, Y = 20 };
        await using var services = CreateServices(settings);
        var recipe = services.GetRequiredService<RecipeManager>().Current;
        recipe.Pcb.BoltPoints = [new() { Number = 1, X = 10, Y = 10 },];
        TeachInspectionFovs(recipe);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var inspection = services.GetRequiredService<InspectionStation>();
        IMotionFeedback[] disabledMotions = [
            services.GetRequiredService<PcbSupplier>().Motion.Feedback,
            services.GetRequiredService<PcbPlacer>().Motion.Feedback,
            services.GetRequiredService<BoltFasteningStation>().Motion.Feedback,
        ];
        var disabledOutputs = settings.PcbSupplyHardware.Outputs.Keys.Concat(
            settings.PcbPlacementHandlerHardware.Outputs.Keys)
            .Concat(settings.BoltFasteningHardware.Outputs.Keys)
            .Concat(settings.BoltFeederHardware.Outputs.Keys)
            .ToHashSet();
        var unexpectedOutputs = new ConcurrentBag<OutputIo>();
        var adcFrames = 0;
        foreach (var head in new[] { FasteningHead.Pickup, FasteningHead.Shooting })
            services.GetRequiredKeyedService<IAdcBus>(head).FrameTransferred += (_, _) => Interlocked.Increment(
                ref adcFrames);
        io.OutputChanged += (output, value) =>
        {
            if (value && disabledOutputs.Contains(output))
                unexpectedOutputs.Add(output);
        };
        var arrived = 0;
        var entered = false;
        var exited = false;
        io.InputChanged += (input, value) =>
        {
            if (value)
            {
                Interlocked.Or(
                    ref arrived,
                    input switch
                    {
                        InputIo.PcbPlacementHeatSink1Present => 1,
                        InputIo.BoltFasteningHeatSink1Present => 2,
                        InputIo.InspectionHeatSink1Present => 4,
                        _ => 0,
                    });
            }

            if (input == InputIo.MainConveyorEntryCarrierDetected && value)
                entered = true;
            if (input == InputIo.MainConveyorAvailableFromFront2 && value && entered)
                io.SetInput(input, false);
            if (input == InputIo.MainConveyorReadyFromRear && !value && entered)
                exited = true;
        };
        if (missingBolts)
        {
            var camera = services.GetRequiredService<VirtualCamera>();
            camera.BoltsPresent = false;
        }

        await machine.InitializeAsync();
        Assert.True(machine.IsHomeAllowed);
        await machine.HomeAsync(CancellationToken.None);
        io.SetInput(InputIo.MainConveyorReadyFromRear, false);
        io.SetInput(InputIo.AutoMode, false);
        Assert.True(machine.IsStartAllowed);
        var run = machine.StartAsync();
        try
        {
            Assert.True(
                await VirtualTest.WaitUntilAsync(() => inspection.Station.Completed, TimeSpan.FromSeconds(10)));
            Assert.Equal(7, arrived);
            Assert.Equal(missingBolts, inspection.HasNg);
            Assert.Equal(2, inspection.Station.Assemblies.Count());
            Assert.All(
                inspection.Station.Assemblies,
                assembly =>
                {
                    Assert.Equal(
                        assembly.HeatSink == HeatSinkSlot.HeatSink1 ? "PCB-1" : "PCB-2",
                        assembly.PcbBarcode);
                    Assert.Empty(assembly.PcbBoltResults);
                    Assert.Empty(assembly.PickupBoltResults);
                    Assert.Equal(!missingBolts, Assert.Single(assembly.BoltPresenceResults).Value);
                });
            if (missingBolts)
            {
                Assert.True(
                    await VirtualTest.WaitUntilAsync(
                        () => io.GetInput(InputIo.NgConveyorPosition1Occupied)
                            && io.GetInput(InputIo.NgShuttleUp)
                            && !io.GetOutput(OutputIo.NgConveyorRun),
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
        Assert.All(
            disabledMotions,
            motion =>
                Assert.All(
                    motion.Axes,
                    axis =>
                    {
                        Assert.False(motion.GetAxisState(axis).ServoOn);
                        Assert.False(motion.GetAxisState(axis).Homed);
                    }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlacementHomeReportsHorizontalFailureAfterZHome(bool allUnits)
    {
        var settings = FlowSettings();
        if (!allUnits)
            settings.Units = EnableOnly(MachineUnit.PcbPlacement);
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var motion = services.GetRequiredKeyedService<IXyMotion>(MotionGroup.PcbPlacementHandler);
        var teaching = services.GetRequiredService<TeachingViewModel>();
        teaching.SelectedTeachingUnit = HardwareArea.PcbPlacementHandler;
        await machine.InitializeAsync();
        try
        {
            settings.PcbPlacementHandler.Motion.HorizontalHome.SearchSpeed = 0;
            await WaitUntilAsync(() => teaching.HomeCommand.CanExecute(null));

            if (allUnits)
                await machine.HomeAsync(CancellationToken.None);
            else
                await teaching.HomeCommand.ExecuteAsync(null);

            Assert.True(motion.GetAxisState(MotionAxis.Z).Homed);
            Assert.False(motion.GetAxisState(MotionAxis.X).Homed);
            Assert.False(motion.GetAxisState(MotionAxis.Y).Homed);
            if (allUnits)
            {
                foreach (var (group, other) in services.GetRequiredService<IReadOnlyDictionary<MotionGroup, IXyMotion>>())
                {
                    if (group != MotionGroup.PcbPlacementHandler)
                        Assert.All(other.Axes, axis => Assert.False(other.GetAxisState(axis).Homed));
                }
            }
            Assert.Equal(MachineAlarm.HomeFailed, state.Alarm);
            Assert.Contains(nameof(ArgumentOutOfRangeException), state.AlarmDetail);
            Assert.False(state.IsHoming);
            Assert.False(motion.IsMoving);
            Assert.False(services.GetRequiredService<OperationCancellation>().HasActiveOperations);
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task SelectedAxisHomeDoesNotRequireZHomeOrServo()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.PcbSupply);
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var motion = services.GetRequiredKeyedService<IXyMotion>(MotionGroup.PcbSupply);
        await machine.InitializeAsync();
        try
        {
            motion.SetServo(MotionAxis.Z, false);
            await machine.HomeAsync(MotionGroup.PcbSupply, default, MotionAxis.X);

            Assert.True(motion.GetAxisState(MotionAxis.X).Homed);
            Assert.False(motion.GetAxisState(MotionAxis.Z).Homed);
            Assert.False(motion.GetAxisState(MotionAxis.Z).ServoOn);
            Assert.Equal(MachineAlarm.None, services.GetRequiredService<MachineState>().Alarm);
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task AllUnitsHomeFinishesPlacementFirstAndEndsWithoutMovingToWorkHeights()
    {
        var settings = FlowSettings();
        settings.PcbPlacementHandler.HandoffPosition.Z = 8;
        settings.BoltFastening.SafeZ = 12;
        settings.PcbSupply.RotationZ = 16;
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        await machine.InitializeAsync();
        var outputs = new ConcurrentQueue<OutputIo>();
        services.GetRequiredService<VirtualIoService>().OutputChanged += (output, _) => outputs.Enqueue(output);
        var motions = services.GetRequiredService<IReadOnlyDictionary<MotionGroup, IXyMotion>>();
        var placement = motions[MotionGroup.PcbPlacementHandler];
        var starts = new ConcurrentQueue<(MotionGroup Group, bool PlacementHomed)>();
        foreach (var (group, motion) in motions)
        {
            if (group != MotionGroup.PcbPlacementHandler)
                motion.MovingChanged += moving =>
                {
                    if (moving)
                        starts.Enqueue((group, placement.Axes.All(axis => placement.GetAxisState(axis).Homed)));
                };
        }
        try
        {
            await machine.HomeAsync(default);

            Assert.Equal(MachineAlarm.None, state.Alarm);
            Assert.Empty(outputs);
            Assert.Equal(motions.Count - 1, starts.Select(start => start.Group).Distinct().Count());
            Assert.All(starts, start => Assert.True(start.PlacementHomed, $"{start.Group} started before Placement HOME completed."));
            foreach (var motion in motions.Values)
            {
                Assert.Equal((0, 0, 0), motion.Position);
                Assert.All(motion.Axes, axis => Assert.True(motion.GetAxisState(axis).Homed));
            }
            Assert.False(state.IsHoming);
            Assert.False(services.GetRequiredService<OperationCancellation>().HasActiveOperations);
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task StopAtPlacementHomeCompletionPreventsRemainingHomeAndAllowsRestart()
    {
        await using var services = CreateServices(FlowSettings());
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var operations = services.GetRequiredService<OperationCancellation>();
        var motions = services.GetRequiredService<IReadOnlyDictionary<MotionGroup, IXyMotion>>();
        var placement = motions[MotionGroup.PcbPlacementHandler];
        await machine.InitializeAsync();
        var starts = new ConcurrentQueue<MotionGroup>();
        foreach (var (group, motion) in motions)
            motion.MovingChanged += moving =>
            {
                if (moving)
                    starts.Enqueue(group);
            };

        void StopAfterPlacementHome()
        {
            if (!placement.Axes.All(axis => placement.GetAxisState(axis).Homed))
                return;
            placement.StateChanged -= StopAfterPlacementHome;
            machine.Stop();
        }

        placement.StateChanged += StopAfterPlacementHome;
        try
        {
            await machine.HomeAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));

            Assert.Equal(new[] { MotionGroup.PcbPlacementHandler, MotionGroup.PcbPlacementHandler }, starts);
            Assert.False(state.IsHoming);
            Assert.False(operations.HasActiveOperations);
            Assert.Equal(MachineAlarm.None, state.Alarm);

            starts.Clear();
            await WaitUntilAsync(() => machine.IsHomeAllowed);
            await machine.HomeAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));

            Assert.Equal(new[] { MotionGroup.PcbPlacementHandler, MotionGroup.PcbPlacementHandler }, starts.Take(2));
            foreach (var motion in motions.Values)
                Assert.All(motion.Axes, axis => Assert.True(motion.GetAxisState(axis).Homed));
            Assert.False(state.IsHoming);
            Assert.False(operations.HasActiveOperations);
            Assert.Equal(MachineAlarm.None, state.Alarm);
        }
        finally
        {
            placement.StateChanged -= StopAfterPlacementHome;
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task SynchronousHomeStartFailureWaitsForAlreadyStartedAxes()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.PcbSupply);
        settings.Units.BoltFastening = true;
        var failure = new MotionException("Start fastening HOME", new InvalidOperationException("SDK start failed."));
        var results = new Dictionary<MotionGroup, HomeResultMotion>();
        IXyMotion Wrap(IServiceProvider provider, MotionGroup group)
        {
            var motion = DispatchProxy.Create<IXyMotion, HomeResultMotion>();
            var result = (HomeResultMotion)motion;
            result.Motion = provider.GetRequiredKeyedService<IXyMotion>(group);
            result.AwaitCleanupAfterCancellation = true;
            if (group == MotionGroup.BoltFastening)
                result.StartFailure = failure;
            results.Add(group, result);
            return motion;
        }

        await using var services = new ServiceCollection().AddSingleton(_ => VirtualTest.OpenMachineStore())
            .AddIbtmApplication(settings)
            .AddSingleton<IReadOnlyDictionary<MotionGroup, IXyMotion>>(provider =>
                Enum.GetValues<MotionGroup>().ToDictionary(group => group,
                    group => group is MotionGroup.PcbSupply or MotionGroup.BoltFastening
                        ? Wrap(provider, group) : provider.GetRequiredKeyedService<IXyMotion>(group)))
            .BuildServiceProvider();
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        await machine.InitializeAsync();
        var homing = machine.HomeAsync(default);
        var supply = results[MotionGroup.PcbSupply];
        try
        {
            await results[MotionGroup.BoltFastening].Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await WaitUntilAsync(() => supply.HomeCancellation.IsCancellationRequested);
            Assert.False(homing.IsCompleted);
            Assert.True(state.IsHoming);
            Assert.True(services.GetRequiredService<OperationCancellation>().HasActiveOperations);

            supply.Result.SetCanceled();
            await homing.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Contains("SDK start failed.", state.AlarmDetail);
            Assert.False(state.IsHoming);
            Assert.False(services.GetRequiredService<OperationCancellation>().HasActiveOperations);
            Assert.All(results.Values, result => Assert.Equal(0, result.HorizontalHomeCalls));
        }
        finally
        {
            supply.Result.TrySetCanceled();
            machine.Stop();
            await homing;
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(HomeCommandTarget.Axis)]
    [InlineData(HomeCommandTarget.TeachingUnit)]
    [InlineData(HomeCommandTarget.AllUnits)]
    public async Task HomeCommandKeepsDispatcherResponsiveDuringSlowHardwareCall(HomeCommandTarget target)
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            _ = dispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    var settings = FlowSettings();
                    settings.Units = EnableOnly(MachineUnit.Inspection);
                    await using var services = CreateDisplayServices(out var feedback, settings);
                    var machine = services.GetRequiredService<MachineController>();
                    using var release = new ManualResetEventSlim();
                    var entered = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var homing = Task.CompletedTask;
                    await machine.InitializeAsync();
                    try
                    {
                        var uiThread = Environment.CurrentManagedThreadId;
                        IAsyncRelayCommand command;
                        if (target == HomeCommandTarget.AllUnits)
                        {
                            command = services.GetRequiredService<OperationViewModel>().HomeCommand;
                        }
                        else if (target == HomeCommandTarget.TeachingUnit)
                        {
                            var teaching = services.GetRequiredService<TeachingViewModel>();
                            teaching.SelectedTeachingUnit = HardwareArea.InspectionGantry;
                            command = teaching.HomeCommand;
                        }
                        else
                        {
                            var manual = services.GetRequiredService<MotionWindowViewModel>();
                            var axis = manual.Axes.Single(
                                row => row.Group == MotionGroup.InspectionGantry && row.Axis == MotionAxis.X);
                            command = axis.HomeCommand;
                        }

                        await WaitUntilAsync(() => command.CanExecute(null));
                        var button = new Button();
                        button.SetBinding(Button.CommandProperty, new Binding { Source = command });
                        Assert.True(button.IsEnabled);
                        feedback.BeforeRead = () => Assert.NotEqual(uiThread, Environment.CurrentManagedThreadId);
                        feedback.BeforePositionRead = feedback.BeforeRead;
                        feedback.BeforeHome = () =>
                        {
                            entered.TrySetResult(Environment.CurrentManagedThreadId);
                            if (!release.Wait(TimeSpan.FromSeconds(5)))
                                throw new TimeoutException("The UI could not release the simulated hardware call.");
                        };

                        homing = command.ExecuteAsync(null);
                        var hardwareThread = await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
                        Assert.NotEqual(Environment.CurrentManagedThreadId, hardwareThread);
                        Assert.False(homing.IsCompleted);
                        command.NotifyCanExecuteChanged();
                        Assert.False(button.IsEnabled);
                        var responded = false;
                        await dispatcher.InvokeAsync(() => responded = true, DispatcherPriority.Input);
                        Assert.True(responded);

                        release.Set();
                        await homing.WaitAsync(TimeSpan.FromSeconds(2));
                        feedback.BeforeRead = null;
                        feedback.BeforePositionRead = null;
                        Assert.True(feedback.Motion.GetAxisState(MotionAxis.X).Homed);
                        Assert.Equal(MachineAlarm.None, services.GetRequiredService<MachineState>().Alarm);
                    }
                    finally
                    {
                        release.Set();
                        feedback.BeforeHome = null;
                        feedback.BeforeRead = null;
                        feedback.BeforePositionRead = null;
                        await homing;
                        await machine.ShutdownAsync();
                    }
                    finished.TrySetResult();
                }
                catch (Exception exception)
                {
                    finished.TrySetException(exception);
                }
                finally
                {
                    dispatcher.InvokeShutdown();
                }
            }));
            Dispatcher.Run();
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task MotionWindowCloseCancelsAndAwaitsItsAxisHome()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.Inspection);
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var motion = services.GetRequiredKeyedService<IXyMotion>(MotionGroup.InspectionGantry);
        var monitor = services.GetRequiredService<MotionWindowViewModel>();
        await machine.InitializeAsync();
        try
        {
            await machine.HomeAsync(CancellationToken.None);
            await motion.MoveAxisAsync(MotionAxis.X, 20, 10_000);
            settings.InspectionGantry.Motion.HorizontalHome.SearchSpeed = 1;
            var axis = monitor.Axes.Single(
                row => row.Group == MotionGroup.InspectionGantry && row.Axis == MotionAxis.X);
            await WaitUntilAsync(() => axis.HomeCommand.CanExecute(null));
            var homing = axis.HomeCommand.ExecuteAsync(null);
            await WaitUntilAsync(() => motion.IsMoving);

            Assert.True(await monitor.TryCloseAsync());

            Assert.True(homing.IsCompletedSuccessfully);
            Assert.False(motion.IsMoving);
            Assert.False(motion.GetAxisState(MotionAxis.X).Homed);
            Assert.False(services.GetRequiredService<OperationCancellation>().HasActiveOperations);
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }

    public enum HomeCommandTarget
    {
        Axis,
        TeachingUnit,
        AllUnits,
    }

    [Fact]
    public async Task InspectionSeatsAtPickupThenUsesSeparateWaitingPosition()
    {
        await using var services = CreateInspectionServices(enableConveyor: true);
        services.GetRequiredService<UnitSettings>().BoltFastening = true;
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<VirtualIoService>();
        var work = services.GetRequiredService<InspectionStation>();
        var inspection = services.GetRequiredService<InspectionStation>();
        var gantry = services.GetRequiredService<InspectionStation>();
        var recipe = services.GetRequiredService<RecipeManager>().Current;
        var transferSettings = services.GetRequiredService<NgCarrierTransferSettings>();
        transferSettings.WaitingPosition = new() { X = 15, Y = 35 };
        var waitingPosition = transferSettings.WaitingPosition;
        var seatedAtPickup = false;
        io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.InspectionBackupPlateUp && on)
            {
                Assert.True(MotionService.IsAt(gantry.Motion.Feedback, transferSettings.CarrierPickupPosition!));
                Assert.False(MotionService.IsAt(gantry.Motion.Feedback, waitingPosition));
                seatedAtPickup = true;
            }
        };
        var barcodePosition = recipe.CarrierImages.Single(image => image.IsBarcode
            && image.HeatSink == HeatSinkSlot.HeatSink1).Center!;
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        io.SetInput(InputIo.InspectionHeatSink1Present, true);
        await work.Station.PrepareToReceiveAsync(CancellationToken.None);
        await gantry.MoveToAsync(barcodePosition);
        Assert.False(MotionService.IsAt(gantry.Motion.Feedback, waitingPosition));
        var waitedAfterSeating = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        inspection.Trace += message =>
        {
            if (message.Contains(": WaitingForConveyor ", StringComparison.Ordinal))
                waitedAfterSeating.TrySetResult();
        };
        var barcodeCaptured = false;
        inspection.InspectionCaptured += (image, pcb, bolt) =>
        {
            if (bolt is null)
            {
                Assert.True(MotionService.IsAt(gantry.Motion.Feedback, barcodePosition));
                barcodeCaptured = true;
            }
        };
        work.RequestCarrierSeating(work.Station.CurrentJob);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var run = inspection.RunAsync(stop.Token);
        try
        {
            await waitedAfterSeating.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(StationCylinderState.Up, work.Station.BackupPlate);
            Assert.True(seatedAtPickup);
            Assert.True(MotionService.IsAt(gantry.Motion.Feedback, waitingPosition));
            Assert.False(barcodeCaptured);
            Assert.False(io.GetOutput(OutputIo.NgCarrierPickupDown));
            Assert.False(io.GetOutput(OutputIo.NgCarrierGripperClose));

            await work.Station.PrepareToReceiveAsync(stop.Token);
            work.RequestInspection(work.Station.CurrentJob);
            Assert.True(await VirtualTest.WaitUntilAsync(() => work.Station.Completed, TimeSpan.FromSeconds(2)));
            Assert.True(barcodeCaptured);
            Assert.True(MotionService.IsAt(gantry.Motion.Feedback, waitingPosition));
        }
        finally
        {
            stop.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task InspectionSeatingCancelledDuringPickupTravelDoesNotRaisePlate()
    {
        await using var services = CreateInspectionServices(enableConveyor: true);
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<VirtualIoService>();
        var work = services.GetRequiredService<InspectionStation>();
        var station = services.GetRequiredService<InspectionStation>();
        var transfer = services.GetRequiredService<InspectionStation>();
        var settings = services.GetRequiredService<InspectionGantrySettings>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        io.SetInput(InputIo.InspectionHeatSink1Present, true);
        await work.Station.PrepareToReceiveAsync(CancellationToken.None);
        await transfer.MoveToAsync(new() { X = 100, Y = 100 }, 10_000);
        settings.Motion.HorizontalSpeed = 1;
        var raised = false;
        io.OutputChanged += (output, on) => raised |= output == OutputIo.InspectionBackupPlateUp && on;
        using var stop = new CancellationTokenSource();
        work.RequestCarrierSeating(work.Station.CurrentJob);
        var run = station.RunAsync(stop.Token);
        try
        {
            Assert.True(await VirtualTest.WaitUntilAsync(
                () => transfer.Motion.Feedback.IsMoving, TimeSpan.FromSeconds(2)));
            Assert.Equal(InspectionStationState.SeatingCarrier, station.GetNextStep());
            Assert.False(raised);
            stop.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(raised);
            Assert.False(work.CarrierSeatingRequested);
            Assert.Equal(StationCylinderState.Down, work.Station.BackupPlate);
        }
        finally
        {
            stop.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InspectionStartRepeatsEveryPointFromTheFirstPcb(bool stopAfterCompletion)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.Inspection);
        settings.PcbHistory.Directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"PCB-inspection-{Guid.NewGuid():N}");
        await using var services = CreateServices(settings);
        var recipe = services.GetRequiredService<RecipeManager>().Current;
        recipe.Pcb.BoltPoints = [
            new() { Number = 4, HeatSink = HeatSinkSlot.HeatSink2, X = 30, Y = 20 },
            new() { Number = 3, HeatSink = HeatSinkSlot.HeatSink1, X = 10, Y = 20 },
            new() { Number = 2, HeatSink = HeatSinkSlot.HeatSink2, X = 30, Y = 10 },
            new() { Number = 1, HeatSink = HeatSinkSlot.HeatSink1, X = 10, Y = 10 },
        ];
        TeachInspectionFovs(recipe);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var work = services.GetRequiredService<InspectionStation>();
        var inspection = services.GetRequiredService<InspectionStation>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        io.SetInputs((InputIo.InspectionHeatSink1Present, true), (InputIo.InspectionHeatSink2Present, true));
        await work.Station.PrepareToReceiveAsync(CancellationToken.None);
        io.SetInput(InputIo.AutoMode, false);
        var assembly = work.Station.GetAssembly(HeatSinkSlot.HeatSink1);
        assembly.RecordBolt(FasteningHead.Shooting, 1, new(false, 0.5, Error: "Existing fastening NG"));
        await machine.PcbHistory.FlushAsync();
        var number = assembly.PcbNumber;
        var captures = new ConcurrentQueue<(HeatSinkSlot Pcb, int? Bolt)>();
        using var firstStop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var firstRun = true;
        inspection.InspectionCaptured += (image, pcb, bolt) =>
        {
            captures.Enqueue((pcb, bolt));
            if (firstRun && !stopAfterCompletion && pcb == HeatSinkSlot.HeatSink2 && bolt is null)
                firstStop.Cancel();
        };
        var run = machine.StartAsync(firstStop.Token);
        if (stopAfterCompletion)
        {
            Assert.True(await VirtualTest.WaitUntilAsync(() => work.Station.Completed, TimeSpan.FromSeconds(3)));
            firstStop.Cancel();
        }
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(stopAfterCompletion, work.Station.Completed);
        Assert.Equal(2, assembly.BoltPresenceResults.Count);
        Assert.Equal(MachineAlarm.None, state.Alarm);

        firstRun = false;
        captures.Clear();
        using var secondStop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        run = machine.StartAsync(secondStop.Token);
        try
        {
            Assert.True(await VirtualTest.WaitUntilAsync(
                () => captures.Count == 6 && work.Station.Completed, TimeSpan.FromSeconds(3)), state.AlarmDetail);
            Assert.Equal(new (HeatSinkSlot, int?)[] {
                (HeatSinkSlot.HeatSink1, null), (HeatSinkSlot.HeatSink1, 1), (HeatSinkSlot.HeatSink1, 3),
                (HeatSinkSlot.HeatSink2, null), (HeatSinkSlot.HeatSink2, 2), (HeatSinkSlot.HeatSink2, 4),
            }, captures);
            Assert.Same(assembly, work.Station.GetAssembly(HeatSinkSlot.HeatSink1));
            Assert.Equal(number, assembly.PcbNumber);
            Assert.Equal("Existing fastening NG", assembly.PcbBoltResults[1].Error);
            Assert.Equal(AssemblyResult.Ng, assembly.FasteningResult);
            Assert.Equal(MachineAlarm.None, state.Alarm);
        }
        finally
        {
            secondStop.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task InspectionParksThenDischargesOrRaisesBeforeOtherTransfers(bool ng, bool rearReady)
    {
        await using var services = CreateInspectionServices(enableConveyor: true);
        var machine = services.GetRequiredService<MachineController>();
        var machineState = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var work = services.GetRequiredService<InspectionStation>();
        var station = services.GetRequiredService<InspectionStation>();
        var conveyor = services.GetRequiredService<MainConveyor>();
        var pickup = services.GetRequiredService<InspectionStation>();
        var transferSettings = services.GetRequiredService<NgCarrierTransferSettings>();
        transferSettings.WaitingPosition = new() { X = 15, Y = 35 };
        var waitingPosition = transferSettings.WaitingPosition;
        services.GetRequiredService<VirtualCamera>().BoltsPresent = !ng;
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        io.SetInput(InputIo.AutoMode, false);
        io.SetInput(InputIo.MainConveyorReadyFromRear, rearReady);
        io.SetInput(InputIo.MainConveyorAvailableFromFront2, false);
        // Start with an occupied, raised S3; the main sequence must lower it for inspection.
        io.SetInput(InputIo.InspectionHeatSink1Present, true);
        await work.Station.SeatAsync(CancellationToken.None);
        var inspectedAssembly = work.Station.GetAssembly(HeatSinkSlot.HeatSink1);
        inspectedAssembly.PcbBarcode = "PCB-1";

        var inspected = false;
        var returned = false;
        var raises = 0;
        var conveyorSteps = new ConcurrentQueue<string>();
        conveyor.Trace += conveyorSteps.Enqueue;
        var beltStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var discharged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        station.Trace += message =>
        {
            if (!inspected && message.Contains(": InspectingBolt ", StringComparison.Ordinal))
            {
                Assert.True(work.IsAtInspectionPosition);
                Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
                Assert.Equal(MainConveyorState.WaitingForInspection, conveyor.Step);
                inspected = true;
                // A carrier arriving after inspection was requested must wait for it to finish.
                io.SetInput(InputIo.PcbPlacementHeatSink2Present, true);
            }
            if (inspected && message.Contains(": CompletingInspection ", StringComparison.Ordinal))
            {
                Assert.False(work.Station.Completed);
                Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
                returned = true;
            }
        };
        io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.InspectionBackupPlateUp && on)
            {
                Assert.True(work.Station.Completed);
                Assert.Equal(InspectionStationState.SeatingCarrier, station.GetNextStep());
                Assert.Equal(MainConveyorState.WaitingForInspectionTransfer, conveyor.Step);
                Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
                Assert.True(MotionService.IsAt(pickup.Motion.Feedback, services.GetRequiredService<NgCarrierTransferSettings>().CarrierPickupPosition!));
                Interlocked.Increment(ref raises);
            }
            if (output == OutputIo.NgCarrierPickupDown && on && work.Station.CarrierPresent)
            {
                Assert.True(ng);
                Assert.True(work.Station.Completed);
                Assert.True(work.Station.CarrierSeated);
            }
            if (output != OutputIo.MainConveyorRun || !on)
                return;
            Assert.True(inspected);
            Assert.True(returned);
            // NG pickup can already have removed the completed carrier from S3.
            Assert.Equal(ng ? AssemblyResult.Ng : AssemblyResult.Ok, inspectedAssembly.InspectionResult);
            if (!ng)
                Assert.True(work.Station.Completed);
            if (conveyor.Step is MainConveyorState.DischargingInspectionCarrier)
            {
                Assert.False(ng);
                Assert.True(work.IsTransferAtWaitingPosition);
                Assert.True(MotionService.IsAt(pickup.Motion.Feedback, waitingPosition));
                discharged.TrySetResult();
            }
            else
            {
                Assert.Equal(MainConveyorState.MovingPcbPlacementToBoltFastening, conveyor.Step);
                Assert.Equal(StationCylinderState.Up, work.Station.BackupPlate);
            }
            beltStarted.TrySetResult();
        };

        var run = machine.StartAsync();
        try
        {
            Assert.True(await VirtualTest.WaitUntilAsync(
                () => beltStarted.Task.IsCompleted, TimeSpan.FromSeconds(5)),
                $"State={conveyor.Step}; alarm={machineState.AlarmMessage}; "
                    + string.Join(" | ", conveyorSteps));
            if (!ng && rearReady)
            {
                Assert.Equal(0, Volatile.Read(ref raises));
                Assert.True(discharged.Task.IsCompletedSuccessfully);
            }
            else
            {
                Assert.Equal(1, Volatile.Read(ref raises));
                if (ng)
                {
                    Assert.True(await VirtualTest.WaitUntilAsync(
                        () => io.GetInput(InputIo.NgCarrierDetected) && pickup.IsRaised,
                        TimeSpan.FromSeconds(3)));
                }
                else
                {
                    io.SetInput(InputIo.MainConveyorReadyFromRear, true);
                    Assert.True(await VirtualTest.WaitUntilAsync(
                        () => discharged.Task.IsCompleted, TimeSpan.FromSeconds(5)),
                        $"State={conveyor.Step}; completed={work.Station.Completed}; seated={work.Station.CarrierSeated}; "
                            + $"parked={work.IsTransferAtWaitingPosition}; ready={conveyor.DownstreamReady}; "
                            + $"alarm={machineState.AlarmMessage}; "
                            + string.Join(" | ", conveyorSteps));
                }
            }
            Assert.Equal(MachineAlarm.None, machineState.Alarm);
        }
        finally
        {
            machine.Stop();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InspectionArrivalYieldsToStation2RefillAndFrontReceiving(bool carrierWaitingAtS1)
    {
        await using var services = CreateInspectionServices(enableConveyor: true);
        var machine = services.GetRequiredService<MachineController>();
        var machineState = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var conveyor = services.GetRequiredService<MainConveyor>();
        var inspection = services.GetRequiredService<InspectionStation>();
        var work = services.GetRequiredService<InspectionStation>();
        var fastening = services.GetRequiredService<BoltFasteningStation>().Station;
        var placement = services.GetRequiredService<PcbPlacer>().Station;
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        io.SetInput(InputIo.AutoMode, false);
        io.SetInput(InputIo.MainConveyorReadyFromRear, false);
        io.SetInput(InputIo.BoltFasteningHeatSink2Present, true);
        await fastening.SeatAsync(CancellationToken.None);
        var arrivingJob = fastening.CurrentJob;
        var arrivingAssembly = fastening.GetAssembly(HeatSinkSlot.HeatSink2);
        arrivingAssembly.PcbBarcode = "S2-CARRIER";
        if (carrierWaitingAtS1)
        {
            io.SetInput(InputIo.PcbPlacementHeatSink2Present, true);
            await placement.SeatAsync(CancellationToken.None);
        }
        io.SetInput(InputIo.MainConveyorAvailableFromFront2, true);

        var expectedMoves = carrierWaitingAtS1
            ? new[]
            {
                MainConveyorState.MovingBoltFasteningToInspection,
                MainConveyorState.MovingPcbPlacementToBoltFastening,
                MainConveyorState.ReceivingFrontCarrier,
            }
            : new[]
            {
                MainConveyorState.MovingBoltFasteningToInspection,
                MainConveyorState.ReceivingFrontCarrier,
                MainConveyorState.MovingPcbPlacementToBoltFastening,
            };
        var moves = new ConcurrentQueue<MainConveyorState>();
        var trace = new ConcurrentQueue<string>();
        conveyor.Trace += trace.Enqueue;
        inspection.Trace += trace.Enqueue;
        var plateMovesBeforeInspection = new ConcurrentQueue<bool>();
        var inspected = false;
        inspection.Trace += message =>
        {
            if (inspected || !message.Contains(": InspectingBolt ", StringComparison.Ordinal))
                return;
            Assert.True(work.InspectionRequested);
            Assert.True(work.IsAtInspectionPosition);
            Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
            Assert.True(fastening.CarrierSeated);
            Assert.Equal(carrierWaitingAtS1, placement.CarrierSeated);
            Assert.Equal(arrivingJob.Id, work.Station.CurrentJob.Id);
            Assert.Same(arrivingAssembly, work.Station.GetAssembly(HeatSinkSlot.HeatSink2));
            Assert.Equal("PCB-2", arrivingAssembly.PcbBarcode);
            Assert.Equal(new[] { true, false }, plateMovesBeforeInspection);
            Assert.Equal(expectedMoves, moves);
            inspected = true;
        };
        io.OutputChanged += (output, on) =>
        {
            if (inspected)
                return;
            if (output == OutputIo.InspectionBackupPlateUp)
            {
                Assert.False(work.InspectionRequested);
                Assert.False(work.Station.Completed);
                if (on)
                {
                    Assert.Equal(InspectionStationState.SeatingCarrier, inspection.GetNextStep());
                    Assert.Equal(MainConveyorState.WaitingForInspectionTransfer, conveyor.Step);
                    Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
                    Assert.True(MotionService.IsAt(
                        services.GetRequiredService<InspectionStation>().Motion.Feedback,
                        services.GetRequiredService<NgCarrierTransferSettings>().CarrierPickupPosition!));
                }
                plateMovesBeforeInspection.Enqueue(on);
            }
            if (output != OutputIo.MainConveyorRun || !on)
                return;
            var state = Assert.IsType<MainConveyorState>(conveyor.Step);
            moves.Enqueue(state);
            if (state == MainConveyorState.MovingBoltFasteningToInspection)
                return;
            Assert.True(work.Station.CarrierSeated);
            Assert.False(work.InspectionRequested);
            Assert.False(work.Station.Completed);
        };

        var run = machine.StartAsync();
        try
        {
            Assert.True(await VirtualTest.WaitUntilAsync(
                () => work.Station.Completed, TimeSpan.FromSeconds(8)),
                $"Alarm={machineState.AlarmMessage}; " + string.Join(" | ", trace));
            Assert.True(inspected);
            Assert.Equal(MachineAlarm.None, machineState.Alarm);
        }
        finally
        {
            machine.Stop();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
            await machine.ShutdownAsync();
        }
        Assert.False(work.InspectionRequested);
    }

    [Fact]
    public async Task InspectionParksWhileIdleAndRejectsResultsIfConveyorStarts()
    {
        await using var services = CreateInspectionServices(enableConveyor: false);
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<VirtualIoService>();
        var work = services.GetRequiredService<InspectionStation>();
        var station = services.GetRequiredService<InspectionStation>();
        var gantry = services.GetRequiredService<InspectionStation>();
        var waitingPosition = services.GetRequiredService<NgCarrierTransferSettings>().WaitingPosition!;
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        await gantry.MoveToAsync(new() { X = 12, Y = 7 }, 10_000);
        var interrupted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var beltForcedOn = false;
        station.Trace += message =>
        {
            if (message.Contains(": InspectingBolt ", StringComparison.Ordinal))
            {
                beltForcedOn = true;
                io.SetOutput(OutputIo.MainConveyorRun, true);
            }
            if (beltForcedOn && message.Contains(": Waiting ", StringComparison.Ordinal))
                interrupted.TrySetResult();
        };
        using var stop = new CancellationTokenSource();
        var run = station.RunAsync(stop.Token);
        try
        {
            Assert.True(await VirtualTest.WaitUntilAsync(
                () => work.IsTransferAtWaitingPosition, TimeSpan.FromSeconds(2)));
            Assert.True(MotionService.IsAt(gantry.Motion.Feedback, waitingPosition));
            Assert.Equal(InspectionStationState.Waiting, station.GetNextStep());
            io.SetInput(InputIo.InspectionHeatSink1Present, true);
            var assembly = work.Station.GetAssembly(HeatSinkSlot.HeatSink1);
            assembly.PcbBarcode = "PCB-1";
            await work.Station.PrepareToReceiveAsync(CancellationToken.None);
            await interrupted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(beltForcedOn);
            Assert.False(work.Station.Completed);
            Assert.Empty(assembly.BoltPresenceResults);
        }
        finally
        {
            stop.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
            await machine.ShutdownAsync();
        }
    }

    private static ServiceProvider CreateInspectionServices(bool enableConveyor)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.Inspection);
        settings.Units.MainConveyor = enableConveyor;
        settings.Conveyor.CarrierStopDelaySeconds = 0;
        var services = CreateServices(settings);
        PrepareCarrierTeaching(settings, services.GetRequiredService<RecipeManager>().Current);
        return services;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResetPreservesSeatedCarrierAndAllowsStartingItsTransfer(bool stoppedAutomatically)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.Inspection);
        await using var services = CreateServices(settings);
        PrepareCarrierTeaching(settings, services.GetRequiredService<RecipeManager>().Current);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var work = services.GetRequiredService<PcbPlacer>().Station;
        await machine.InitializeAsync();
        try
        {
            await machine.HomeAsync(CancellationToken.None);
            io.SetInput(InputIo.PcbPlacementHeatSink1Present, true);
            await work.SeatAsync(CancellationToken.None);
            var job = work.CurrentJob;
            var assembly = work.GetAssembly(HeatSinkSlot.HeatSink1);
            var completed = work.Completed;
            if (stoppedAutomatically)
            {
                var run = machine.StartAsync();
                try
                {
                    await WaitUntilAsync(() => state.AutomaticRunning);
                }
                finally
                {
                    machine.Stop();
                    await run.WaitAsync(TimeSpan.FromSeconds(3));
                }
            }

            state.SetError(MachineAlarm.MainConveyor);
            var plateWrites = 0;
            io.OutputChanged += (output, _) =>
            {
                if (output is OutputIo.PcbPlacementBackupPlateUp or OutputIo.PcbPlacementStopperUp)
                    plateWrites++;
            };
            Assert.True(machine.IsResetAllowed);
            await machine.ResetAsync();

            Assert.Equal(MachineAlarm.None, state.Alarm);
            Assert.Null(state.AlarmDetail);
            Assert.True(work.CarrierSeated);
            Assert.True(io.GetOutput(OutputIo.PcbPlacementBackupPlateUp));
            Assert.Equal(0, plateWrites);
            Assert.Same(job, work.CurrentJob);
            Assert.Same(assembly, Assert.Single(work.Assemblies));
            Assert.Equal(completed, work.Completed);
            Assert.False(state.AutomaticRunning);
            Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
            Assert.Equal(StartBlockReason.None, machine.StartBlock);
            Assert.True(machine.IsStartAllowed);

            settings.Units.Inspection = false;
            settings.Units.MainConveyor = true;
            var restarted = machine.StartAsync();
            try
            {
                await VirtualTest.WaitForOutputAsync(io, OutputIo.MainConveyorRun, true);
                Assert.Equal(StationCylinderState.Down, work.BackupPlate);
                Assert.True(state.AutomaticRunning);
            }
            finally
            {
                machine.Stop();
                await restarted.WaitAsync(TimeSpan.FromSeconds(3));
            }
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task MaterialInsideMachineDoesNotBlockStartOrAlarmReset()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.NgConveyor);
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        Assert.True(machine.IsStartAllowed, machine.StartBlock.ToString());
        var run = machine.StartAsync();
        try
        {
            await WaitUntilAsync(() => state.AutomaticRunning);
            machine.Stop();
            await run.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(StartBlockReason.None, machine.StartBlock);

            io.SetInput(InputIo.NgCarrierDetected, true);
            io.SetInputs(
                (InputIo.PcbPlacementHeatSink1Present, true),
                (InputIo.BoltFasteningHeatSink1Present, true),
                (InputIo.InspectionHeatSink1Present, true),
                (InputIo.NgConveyorPosition1Occupied, true),
                (InputIo.PcbPlacementPcbDetected, true),
                (InputIo.PcbPlacementVacuumDetected, true));
            Assert.Equal(StartBlockReason.None, machine.StartBlock);
            Assert.True(machine.IsStartAllowed);

            state.SetError(MachineAlarm.NgCarrierTransfer);
            await machine.ResetAsync();
            Assert.False(state.IsError);
            Assert.Equal(StartBlockReason.None, machine.StartBlock);
            // A waiting upstream carrier is normal material, not interrupted work.
            io.SetInput(InputIo.PcbSupplyAvailableFromFront1, true);
            Assert.True(machine.IsStartAllowed, machine.StartBlock.ToString());
            io.SetInput(InputIo.PcbSupplyAvailableFromFront1, false);
            Assert.False(state.AutomaticRunning);
            Assert.True(machine.IsStartAllowed, state.AlarmDetail);

            using var nextStop = new CancellationTokenSource();
            run = machine.StartAsync(nextStop.Token);
            await WaitUntilAsync(() => state.AutomaticRunning);
            Assert.True(io.GetInput(InputIo.NgCarrierDetected));
            Assert.True(io.GetInput(InputIo.PcbPlacementPcbDetected));
            Assert.True(io.GetInput(InputIo.PcbPlacementVacuumDetected));
            Assert.True(io.GetInput(InputIo.PcbPlacementHeatSink1Present));
            Assert.True(io.GetInput(InputIo.BoltFasteningHeatSink1Present));
            Assert.True(io.GetInput(InputIo.InspectionHeatSink1Present));
            Assert.True(io.GetInput(InputIo.NgConveyorPosition1Occupied));
            Assert.Equal(MachineAlarm.None, state.Alarm);
            nextStop.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            machine.Stop();
            await run.WaitAsync(TimeSpan.FromSeconds(3));
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task EmergencyInputStopsConveyorBeforeReadingUnrelatedMotionFeedback()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.MainConveyor);
        settings.Units.Inspection = true;
        await using var services = CreateDisplayServices(out var feedback, settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        PrepareCarrierTeaching(settings, services.GetRequiredService<RecipeManager>().Current);
        await machine.InitializeAsync();
        await machine.HomeAsync(default);
        var run = machine.StartAsync();
        var checkingTrip = new AsyncLocal<bool>();
        var readsBeforeStop = 0;
        try
        {
            await WaitUntilAsync(() => state.AutomaticRunning || run.IsCompleted);
            Assert.True(state.AutomaticRunning, $"{machine.StartBlock}: {state.AlarmDetail}");
            io.SetOutput(OutputIo.MainConveyorRun, true);
            feedback.BeforeRead = () =>
            {
                if (checkingTrip.Value && io.GetOutput(OutputIo.MainConveyorRun))
                    Interlocked.Increment(ref readsBeforeStop);
            };
            checkingTrip.Value = true;
            io.SetInput(InputIo.EmergencyStop1Pressed, true);
            checkingTrip.Value = false;
            await run.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(0, readsBeforeStop);
            Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
            Assert.Equal(MachineAlarm.EmergencyStop, state.Alarm);
        }
        finally
        {
            checkingTrip.Value = false;
            feedback.BeforeRead = null;
            machine.Stop();
            await run.WaitAsync(TimeSpan.FromSeconds(3));
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task ConcurrentManualAdmissionOnlyStartsOneDeviceCommand()
    {
        await using var services = CreateDisplayServices(out var feedback);
        var machine = services.GetRequiredService<MachineController>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        var settings = services.GetRequiredService<InspectionGantrySettings>();
        await feedback.Motion.MoveToXYAsync(10, 0, 10_000);
        settings.Motion.HorizontalHome.SearchSpeed = 10_000;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var pauseAdmission = new AsyncLocal<bool>();
        var starts = 0;
        feedback.BeforeRead = () =>
        {
            if (pauseAdmission.Value)
            {
                entered.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
            }
        };
        void CountStart(bool moving)
        {
            if (moving)
                Interlocked.Increment(ref starts);
        }
        feedback.Motion.MovingChanged += CountStart;

        var first = Task.Run(() =>
        {
            pauseAdmission.Value = true;
            return machine.HomeAsync(MotionGroup.InspectionGantry, default, MotionAxis.X);
        });
        Task second = Task.CompletedTask;
        try
        {
            Assert.True(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(3))));
            second = machine.HomeAsync(MotionGroup.InspectionGantry, default, MotionAxis.X);
            await second.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(0, Volatile.Read(ref starts));
            release.Set();
            await first.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(1, Volatile.Read(ref starts));
        }
        finally
        {
            release.Set();
            feedback.BeforeRead = null;
            feedback.Motion.MovingChanged -= CountStart;
            machine.Stop();
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(3));
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task ManualInspectionGantryIoFailureUsesInspectionUnitAlarm()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.Inspection);
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        try
        {
            Assert.True(machine.IsManualMotionReady(MotionGroup.InspectionGantry));
            machine.ReportManualFailure(
                machine.GetMotionAlarm(MotionGroup.InspectionGantry),
                new IOException("Manual gantry I/O failure."));
            Assert.Equal(MachineAlarm.Inspection, state.Alarm);
            Assert.Equal("Manual gantry I/O failure.", state.AlarmMessage);
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(ManualCommandFailure.Motion)]
    [InlineData(ManualCommandFailure.Canceled)]
    [InlineData(ManualCommandFailure.Safety)]
    [InlineData(ManualCommandFailure.Programming)]
    public async Task ManualCommandHandlesCombinedDeviceFailuresWithoutHidingProgrammingErrors(ManualCommandFailure failureKind)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.Inspection);
        await using var services = CreateServices(settings);
        using var cancellation = new CancellationTokenSource();
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        Exception operationFailure = failureKind switch
        {
            ManualCommandFailure.Motion or ManualCommandFailure.Safety => new MotionException("Manual move", new IOException("Motion feedback failed.")),
            ManualCommandFailure.Canceled => new OperationCanceledException(cancellation.Token),
            _ => new InvalidOperationException("Invalid command state."),
        };
        Exception cleanupFailure = failureKind == ManualCommandFailure.Programming
            ? new InvalidOperationException("Invalid cleanup state.")
            : new IOException("Cleanup output failed.");
        var failure = new AggregateException(operationFailure, new AggregateException(cleanupFailure));
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        try
        {
            Assert.True(machine.IsManualMotionReady(MotionGroup.InspectionGantry));
            if (failureKind == ManualCommandFailure.Programming)
            {
                Assert.False(MachineController.IsDeviceFailure(failure));
                Assert.Equal(MachineAlarm.None, state.Alarm);
            }
            else
            {
                Assert.True(MachineController.IsDeviceFailure(failure));
                io.SetOutput(OutputIo.MainConveyorReadyToFront2, true);
                if (failureKind == ManualCommandFailure.Canceled)
                    cancellation.Cancel();
                if (failureKind == ManualCommandFailure.Safety)
                    io.SetInput(InputIo.EmergencyStop1Pressed, true);
                machine.ReportManualFailure(machine.GetMotionAlarm(MotionGroup.InspectionGantry), failure);
                Assert.Equal(
                    failureKind switch
                    {
                        ManualCommandFailure.Safety => MachineAlarm.EmergencyStop,
                        ManualCommandFailure.Motion => MachineAlarm.MotionUnavailable,
                        _ => MachineAlarm.Inspection,
                    },
                    state.Alarm);
                Assert.Contains(operationFailure.ToString(), state.AlarmDetail);
                Assert.Contains(cleanupFailure.ToString(), state.AlarmDetail);
                Assert.False(io.GetOutput(OutputIo.MainConveyorReadyToFront2));
            }
            Assert.False(services.GetRequiredService<OperationCancellation>().HasActiveOperations);
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManualBoltTestReportsLateFailureWithoutReplacingEmergencyStop(bool emergencyStop)
    {
        var settings = new MachineSettings { Units = EnableOnly(MachineUnit.NgConveyor) };
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var failure = new IOException("Bolt head STOP output failed.");
        await machine.InitializeAsync();
        try
        {
            var bus = new AdcControllerStub
            {
                StopWriteFailure = failure,
                Started = () =>
                {
                    Assert.True(state.BoltTestRunning);
                    if (emergencyStop)
                        io.SetInput(InputIo.EmergencyStop1Pressed, true);
                },
            };
            var headIo = new VirtualIoService(VirtualTest.Outputs(), new());
            bus.BindIo(headIo, FasteningHead.Pickup);
            using var diagnostics = new AdcProtocolViewModel(bus, new VirtualAdcBus(), headIo, settings.Hantas, machine, state);
            await diagnostics.StartCommand.ExecuteAsync(null);
            Assert.Contains("failed", diagnostics.ResultMessage);
            Assert.Equal(emergencyStop ? MachineAlarm.EmergencyStop : MachineAlarm.BoltFastening, state.Alarm);
            Assert.Contains(failure.Message, state.AlarmDetail);
            Assert.False(state.BoltTestRunning);
            Assert.False(state.IsRunning);
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BoltTestStartupFailureClearsRunningState(bool reverse)
    {
        var settings = new MachineSettings { Units = EnableOnly(MachineUnit.NgConveyor) };
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        await machine.InitializeAsync();
        var failure = new IOException("Feedback failed while entering bolt test.");
        void FailWhenTestingStarts()
        {
            if (!state.BoltTestRunning)
                return;
            state.Changed -= FailWhenTestingStarts;
            throw failure;
        }

        var bus = new AdcControllerStub();
        using var diagnostics = new AdcProtocolViewModel(bus, new VirtualAdcBus(), services.GetRequiredService<IIoService>(), settings.Hantas, machine, state);
        state.Changed += FailWhenTestingStarts;
        try
        {
            var command = reverse ? diagnostics.ReverseCommand : diagnostics.StartCommand;
            await command.ExecuteAsync(null);

            Assert.False(state.BoltTestRunning);
            Assert.False(state.IsRunning);
            Assert.False(services.GetRequiredService<OperationCancellation>().HasActiveOperations);
            Assert.Equal(MachineAlarm.BoltFastening, state.Alarm);
            Assert.Contains(failure.Message, state.AlarmDetail);
            Assert.Equal(0, bus.StartWrites);
        }
        finally
        {
            state.Changed -= FailWhenTestingStarts;
            state.BoltTestRunning = false;
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(MotionAxis.X)]
    [InlineData(MotionAxis.Z)]
    public async Task PlacementStopsWhenHandlerUpFeedbackIsLost(MotionAxis axis)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.PcbPlacement);
        settings.PcbPlacementHandler.Motion.ZSpeed = 10;
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var placement = services.GetRequiredService<PcbPlacer>();
        await machine.InitializeAsync();
        await machine.HomeAsync(default);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            var move = axis == MotionAxis.Z
                ? placement.MoveAxisAsync(axis, 10, stop.Token)
                : placement.JogAsync(axis, 10, stop.Token);
            await WaitUntilAsync(() => placement.Motion.Feedback.IsMoving);
            io.SetInput(InputIo.PcbPlacementHandlerUp, false);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => move);
            Assert.Equal(MachineAlarm.PcbPlacement, state.Alarm);
            Assert.False(placement.Motion.Feedback.IsMoving);
            await Assert.ThrowsAsync<MotionInterlockException>(() => placement.MoveAxisAsync(MotionAxis.Z, 10));
            await Assert.ThrowsAsync<MotionInterlockException>(() => placement.JogAsync(MotionAxis.X, 10));
            await Assert.ThrowsAsync<MotionInterlockException>(() => placement.AdjustAxisAsync(MotionAxis.X, 10, 10));
            await Assert.ThrowsAsync<MotionInterlockException>(
                () => placement.MoveAxisAsync(MotionAxis.Z, settings.PcbPlacementHandler.HandoffPosition.Z));
        }
        finally
        {
            stop.Cancel();
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task CylinderInterlockDuringEmergencyStopKeepsTheEmergencyAlarm()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.PcbPlacement);
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var placement = services.GetRequiredService<PcbPlacer>();
        await machine.InitializeAsync();
        await machine.HomeAsync(default);
        void TripWhileMotionHasNotFinished(bool moving)
        {
            if (!moving || !placement.Motion.Feedback.IsMovingHorizontal)
                return;
            io.SetInput(InputIo.EmergencyStop1Pressed, true);
            Assert.Equal(MachineAlarm.EmergencyStop, state.Alarm);
            Assert.True(placement.Motion.Feedback.IsMovingHorizontal);
            io.SetInput(InputIo.PcbPlacementHandlerUp, false);
        }

        placement.Motion.Feedback.MovingChanged += TripWhileMotionHasNotFinished;
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                placement.MoveToXYAsync(new() { X = 20, Y = 20 }));

            Assert.Equal(MachineAlarm.EmergencyStop, state.Alarm);
            Assert.Contains("handler lift Up", state.AlarmDetail);
            Assert.False(placement.Motion.Feedback.IsMoving);
            Assert.False(services.GetRequiredService<OperationCancellation>().HasActiveOperations);
        }
        finally
        {
            placement.Motion.Feedback.MovingChanged -= TripWhileMotionHasNotFinished;
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShutdownRequiresCurrentStoppedFeedbackEvenWithoutAnOwnedMotion(bool unreadable)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.MainConveyor);
        await using var services = CreateMotionScopeServices(settings, out var probes);
        var machine = services.GetRequiredService<MachineController>();
        var feedback = services.GetRequiredService<MachineFeedbackMonitor>();
        await machine.InitializeAsync();
        var probe = probes[MotionGroup.BoltFastening];
        if (unreadable)
            probe.FailHardwareCalls = true;
        else
            probe.OverrideState = state => state with { InMotion = true };
        Assert.False(services.GetRequiredService<OperationCancellation>().HasActiveOperations);
        try
        {
            var failure = await Record.ExceptionAsync(machine.ShutdownAsync);

            Assert.NotNull(failure);
            Assert.Contains(nameof(MotionGroup.BoltFastening), failure.ToString());
            Assert.True(feedback.Completion.IsCompleted);
        }
        finally
        {
            probe.FailHardwareCalls = false;
            probe.OverrideState = null;
            // A retry must read the now-stopped hardware, even though acquisition has ended.
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledStartAndHomeFinishWithoutEscapingOrTouchingHardware(bool home)
    {
        await using var services = CreateServices(FlowSettings());
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var writes = 0;
        io.OutputChanged += (output, value) => writes++;
        using var stop = new CancellationTokenSource();
        stop.Cancel();

        var escaped = await Record.ExceptionAsync(() => home
            ? machine.HomeAsync(stop.Token) : machine.StartAsync(stop.Token));

        Assert.Null(escaped);
        Assert.Equal(0, writes);
        Assert.False(state.IsHoming);
        Assert.False(state.AutomaticRunning);
        Assert.False(services.GetRequiredService<OperationCancellation>().HasActiveOperations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopDuringCommandAdmissionPreventsStartOrHome(bool home)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.Inspection);
        await using var services = CreateDisplayServices(out var feedback, settings);
        PrepareCarrierTeaching(settings, services.GetRequiredService<RecipeManager>().Current);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        await machine.InitializeAsync();
        if (!home)
            await machine.HomeAsync(CancellationToken.None);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var started = false;
        var homeStarted = false;
        state.Changed += () =>
        {
            if (state.AutomaticRunning)
            {
                started = true;
                stop.Cancel();
            }
        };
        feedback.BeforeHome = () => homeStarted = true;
        feedback.BeforeRead = () =>
        {
            feedback.BeforeRead = null;
            machine.Stop();
        };
        try
        {
            await (home ? machine.HomeAsync(stop.Token) : machine.StartAsync(stop.Token));

            Assert.False(started);
            Assert.False(homeStarted);
            Assert.False(state.IsError);
            Assert.False(services.GetRequiredService<OperationCancellation>().HasActiveOperations);
        }
        finally
        {
            feedback.BeforeRead = null;
            feedback.BeforeHome = null;
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MachineStartAndHomeReportAdmissionReadFailureWithoutStarting(bool home)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.Inspection);
        await using var services = CreateDisplayServices(out var feedback, settings);
        PrepareCarrierTeaching(settings, services.GetRequiredService<RecipeManager>().Current);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        await machine.InitializeAsync();
        if (!home)
            await machine.HomeAsync(CancellationToken.None);
        Assert.True(
            await VirtualTest.WaitUntilAsync(
                () => home ? machine.IsHomeAllowed : machine.IsStartAllowed, TimeSpan.FromSeconds(2)),
            $"Command blocked: home={machine.HomeBlock}, start={machine.StartBlock}, alarm={state.AlarmDetail}");
        var failure = new IOException("Motion feedback failed while admitting the command.");
        feedback.BeforeRead = () =>
        {
            feedback.BeforeRead = null;
            throw failure;
        };
        try
        {
            var escaped = await Record.ExceptionAsync(() => home
                ? machine.HomeAsync(CancellationToken.None)
                : machine.StartAsync());

            Assert.Null(escaped);
            Assert.Equal(MachineAlarm.MotionUnavailable, state.Alarm);
            Assert.Contains(failure.Message, state.AlarmDetail);
            Assert.False(state.IsHoming);
            Assert.False(state.AutomaticRunning);
            Assert.False(state.IsRunning);
            Assert.False(feedback.Motion.IsMoving);
            Assert.True(machine.IsResetAllowed);
        }
        finally
        {
            feedback.BeforeRead = null;
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task IndividualHomeReportsReadFailureBeforeMotionStarts()
    {
        await using var services = CreateDisplayServices(out var feedback);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        await machine.InitializeAsync();
        var manual = services.GetRequiredService<MotionWindowViewModel>();
        var axis = manual.Axes.Single(
            row => row.Group == MotionGroup.InspectionGantry && row.Axis == MotionAxis.X);
        feedback.BeforeRead = () => throw new IOException("Home feedback read failed.");

        await axis.HomeCommand.ExecuteAsync(null);

        Assert.Equal(MachineAlarm.HomeFailed, state.Alarm);
        Assert.Contains("Home feedback read failed.", state.AlarmDetail);
        Assert.False(state.IsHoming);
        Assert.False(services.GetRequiredService<OperationCancellation>().HasActiveOperations);
        Assert.False(services.GetRequiredService<InspectionStation>().Motion.Feedback.IsMoving);
    }

    [Fact]
    public async Task StopDuringServoFeedbackReadPreventsServoEnable()
    {
        await using var services = CreateDisplayServices(out var feedback);
        var machine = services.GetRequiredService<MachineController>();
        await machine.InitializeAsync();
        feedback.Motion.SetServo(MotionAxis.X, false);
        feedback.BeforeRead = () =>
        {
            feedback.BeforeRead = null;
            machine.Stop();
        };
        try
        {
            machine.ToggleServo(MotionGroup.InspectionGantry, MotionAxis.X);

            Assert.False(feedback.Motion.GetAxisState(MotionAxis.X).ServoOn);
            Assert.False(services.GetRequiredService<MachineState>().IsError);
            Assert.False(services.GetRequiredService<OperationCancellation>().HasActiveOperations);
        }
        finally
        {
            feedback.BeforeRead = null;
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task IndividualHomeCancelsWhenAvailabilityReadFailsDuringNotification()
    {
        var settings = FlowSettings();
        settings.InspectionGantry.Motion.HorizontalHome.SearchSpeed = 1;
        await using var services = CreateDisplayServices(out var feedback, settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        await services.GetRequiredService<MachineFeedbackMonitor>().StopAsync();
        await feedback.Motion.MoveAxisAsync(MotionAxis.X, 10, 1_000);
        var homing = machine.HomeAsync(MotionGroup.InspectionGantry, CancellationToken.None, MotionAxis.X);
        await WaitUntilAsync(() => state.IsHoming && feedback.Motion.GetAxisState(MotionAxis.X).InMotion);
        var notificationThread = Environment.CurrentManagedThreadId;
        var failure = new IOException("Manual HOME availability read failed.");
        feedback.BeforeRead = () =>
        {
            if (Environment.CurrentManagedThreadId != notificationThread)
                return;
            feedback.BeforeRead = null;
            throw failure;
        };
        try
        {
            Assert.Null(Record.Exception(state.Refresh));
            await homing.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(MachineAlarm.IoCommunication, state.Alarm);
            Assert.Contains(failure.Message, state.AlarmDetail);
            Assert.False(feedback.Motion.IsMoving);
            Assert.False(services.GetRequiredService<OperationCancellation>().HasActiveOperations);
        }
        finally
        {
            feedback.BeforeRead = null;
            machine.Stop();
            await homing;
            await machine.ShutdownAsync();
        }
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
            ? await Assert.ThrowsAsync<InvalidOperationException>(() => motion.JogAsync(MotionAxis.X, 1))
            : await Assert.ThrowsAsync<InvalidOperationException>(() => motion.MoveAxisAsync(MotionAxis.X, 100, 1));

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

        var moving = jog ? motion.JogAsync(MotionAxis.X, 1) : motion.MoveAxisAsync(MotionAxis.X, 100, 1);

        var shutdown = Task.Run(() => operations.ShutdownAsync());
        try
        {
            await feedbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(motion.IsMoving);
            Assert.False(shutdown.IsCompleted);
            Assert.False(moving.IsCompleted);
        }
        finally
        {
            releaseFeedback.Set();
            await shutdown.WaitAsync(TimeSpan.FromSeconds(2));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => moving);
        }
    }

    [Fact]
    public async Task ManualJogFaultStopsTheMachineAndAllowsReset()
    {
        var settings = new MachineSettings
        {
            Units = EnableOnly(MachineUnit.Inspection),
        };
        FastHomes(settings);
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var gantry = services.GetRequiredService<InspectionStation>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);

        var fault = new InvalidOperationException("Jog feedback failed.");
        var failed = 0;
        gantry.Motion.Feedback.PositionChanged += (_, _, _) =>
        {
            if (gantry.Motion.Feedback.IsMoving && Interlocked.Exchange(ref failed, 1) == 0)
            {
                io.SetOutput(OutputIo.NgConveyorRun, true);
                throw fault;
            }
        };
        var teaching = services.GetRequiredService<TeachingViewModel>();
        teaching.SelectedTeachingUnit = HardwareArea.InspectionGantry;
        await WaitUntilAsync(() => teaching.JogCommand.CanExecute(TeachingDirection.XPlus));
        await teaching.JogCommand.ExecuteAsync(TeachingDirection.XPlus)
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(MachineAlarm.MotionUnavailable, state.Alarm);
        Assert.Contains(fault.Message, state.AlarmDetail);
        Assert.False(gantry.Motion.Feedback.IsMoving);
        Assert.False(io.GetOutput(OutputIo.NgConveyorRun));

        await machine.ResetAsync();
        Assert.Equal(MachineAlarm.None, state.Alarm);
        Assert.Null(state.AlarmDetail);
        using var stopped = new CancellationTokenSource();
        var jog = gantry.JogAsync(MotionAxis.X, 10, stopped.Token);
        Assert.True(gantry.Motion.Feedback.IsMoving);
        stopped.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => jog.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(gantry.Motion.Feedback.IsMoving);
        Assert.Equal(MachineAlarm.None, state.Alarm);
    }

    [Fact]
    public async Task HomeAdmissionAllowsPreparationButAxisMotionRequiresRaisedCylinders()
    {
        var settings = FlowSettings();
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var manual = services.GetRequiredService<MotionWindowViewModel>();
        await machine.InitializeAsync();
        Assert.True(machine.IsHomeAllowed);
        Assert.False(state.ManualControlsEnabled);
        Assert.True(state.ManualSetupEnabled);
        io.SetInput(InputIo.AutoMode, false);
        Assert.False(state.ManualSetupEnabled);
        io.SetInput(InputIo.AutoMode, true);
        using (services.GetRequiredService<OperationCancellation>().Link())
            Assert.False(state.ManualSetupEnabled);
        Assert.True(state.ManualSetupEnabled);
        var outputsChanged = 0;
        io.OutputChanged += (_, _) => outputsChanged++;

        foreach (var (up, down, reason) in new[]
        {
            (
                InputIo.PcbPlacementHandlerUp,
                InputIo.PcbPlacementHandlerDown,
                HomeBlockReason.PlacementNotRaised),
            (InputIo.PcbPlacementIpmUp, InputIo.PcbPlacementIpmDown, HomeBlockReason.PlacementNotRaised),
            (InputIo.PickupHeadUp, InputIo.PickupHeadDown, HomeBlockReason.FasteningNotRaised),
            (InputIo.ShootingHeadUp, InputIo.ShootingHeadDown, HomeBlockReason.FasteningNotRaised),
            (InputIo.NgCarrierPickupUp, InputIo.NgCarrierPickupDown, HomeBlockReason.NgPickupNotRaised),
        })
        {
            io.SetInput(up, false);
            Assert.Equal(HomeBlockReason.None, machine.HomeBlock);
            Assert.Equal(reason, machine.GetHomeBlock(requireRaised: true));
            Assert.True(machine.IsHomeAllowed);
            io.SetInput(down, true);
            io.SetInput(up, true);
            Assert.Equal(reason, machine.GetHomeBlock(requireRaised: true));
            io.SetInput(down, false);
            Assert.True(machine.IsHomeAllowed);
        }

        Assert.Equal(0, outputsChanged);

        settings.Units.PcbSupply = settings.Units.PcbPlacement = settings.Units.BoltFastening = false;
        io.SetInput(InputIo.PcbPlacementHandlerUp, false);
        io.SetInput(InputIo.PickupHeadUp, false);
        io.SetInput(InputIo.NgShuttleUp, false);
        io.SetInput(InputIo.NgShuttleDown, true);
        Assert.True(machine.IsHomeAllowed);
        services.GetRequiredService<MachineState>().Refresh();
        await WaitUntilAsync(() => manual.Axes[9].HomeCommand.CanExecute(null));
        Assert.False(manual.Axes[3].HomeCommand.CanExecute(null));
        Assert.True(manual.Axes[9].HomeCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HomeCanRepeatAndIgnoresCarrierInputs(bool individualAxis)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.Inspection);
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var gantry = services.GetRequiredService<InspectionStation>();
        var manual = services.GetRequiredService<MotionWindowViewModel>();
        await machine.InitializeAsync();
        try
        {
            io.SetInput(InputIo.MainConveyorEntryCarrierDetected, true);
            io.SetInput(InputIo.InspectionHeatSink1Present, true);
            io.SetInput(InputIo.NgConveyorPosition2Occupied, true);
            Assert.Equal(HomeBlockReason.None, machine.HomeBlock);
            Assert.True(machine.IsHomeAllowed);
            var axis = manual.Axes.Single(
                row => row.Group == MotionGroup.InspectionGantry && row.Axis == MotionAxis.X);
            await WaitUntilAsync(() => axis.HomeCommand.CanExecute(null));
            var moved = false;
            gantry.Motion.Feedback.MovingChanged += moving =>
            {
                if (moving)
                {
                    moved = true;
                    io.SetInput(InputIo.NgConveyorPosition1Occupied, true);
                    io.SetInput(InputIo.NgCarrierDetected, true);
                }
            };

            for (var run = 0; run < 2; run++)
            {
                moved = false;
                Assert.True(machine.IsHomeAllowed);
                if (individualAxis)
                    await axis.HomeCommand.ExecuteAsync(null);
                else
                    await machine.HomeAsync(CancellationToken.None);

                Assert.True(moved);
                Assert.True(io.GetInput(InputIo.NgConveyorPosition1Occupied));
                Assert.True(io.GetInput(InputIo.NgCarrierDetected));
                Assert.True(gantry.Motion.Feedback.GetAxisState(MotionAxis.X).Homed);
                Assert.Equal(MachineAlarm.None, state.Alarm);
                Assert.False(state.IsHoming);
            }
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task BoltHomeIgnoresStationaryNgPickupState()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.BoltFastening);
        settings.Units.Inspection = true;
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var gantry = services.GetRequiredService<InspectionStation>();
        var teaching = services.GetRequiredService<TeachingViewModel>();
        await machine.InitializeAsync();
        try
        {
            await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.NgCarrierPickupDown, true);
            io.SetInput(InputIo.NgCarrierDetected, true);
            var ngMoved = false;
            gantry.Motion.Feedback.MovingChanged += moving => ngMoved |= moving;
            teaching.SelectedTeachingUnit = HardwareArea.BoltFastening;
            await WaitUntilAsync(() => teaching.HomeCommand.CanExecute(null));

            await teaching.HomeCommand.ExecuteAsync(null);

            var fastening = services.GetRequiredService<BoltFasteningStation>();
            Assert.All(fastening.Motion.Feedback.Axes, axis => Assert.True(fastening.Motion.Feedback.GetAxisState(axis).Homed));
            Assert.False(ngMoved);
            Assert.True(io.GetInput(InputIo.NgCarrierPickupDown));
            Assert.True(io.GetInput(InputIo.NgCarrierDetected));
            Assert.Equal(MachineAlarm.None, state.Alarm);
            Assert.False(state.IsHoming);
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task HomeRaisesCylindersBeforeMovingAxesAndDisplaysHoming()
    {
        await using var services = CreateServices(FlowSettings());
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        IIoService signals = io;
        await machine.InitializeAsync();
        OutputIo[] cylinders = [
            OutputIo.PcbPlacementHandlerDown,
            OutputIo.PcbPlacementIpmDown,
            OutputIo.PickupHeadDown,
            OutputIo.ShootingHeadDown,
            OutputIo.NgCarrierPickupDown,
        ];
        await Task.WhenAll(cylinders.Select(
            output => signals.SetOutputAndWaitAsync(output, true)));
        var motions = new[]
        {
            services.GetRequiredService<PcbSupplier>().Motion.Feedback,
            services.GetRequiredService<PcbPlacer>().Motion.Feedback,
            services.GetRequiredService<BoltFasteningStation>().Motion.Feedback,
            services.GetRequiredService<InspectionStation>().Motion.Feedback,
        };
        var moved = false;
        var movedBeforeRaised = false;
        foreach (var motion in motions)
            motion.MovingChanged += moving =>
            {
                moved |= moving;
                movedBeforeRaised |= moving && machine.GetHomeBlock(requireRaised: true) != HomeBlockReason.None;
            };
        var outputChanges = new ConcurrentQueue<OutputIo>();
        io.OutputChanged += (output, _) => outputChanges.Enqueue(output);

        io.SetInput(InputIo.NgShuttleCarrierDetected, true);
        Assert.True(machine.IsHomeAllowed);
        io.SetInput(InputIo.PcbPlacementPcbDetected, true);
        Assert.False(machine.IsHomeAllowed);
        await machine.HomeAsync(CancellationToken.None);
        Assert.Empty(outputChanges);
        await WaitUntilAsync(() => !machine.IsHomeAllowed);
        io.SetInput(InputIo.PcbPlacementPcbDetected, false);
        Assert.True(machine.IsHomeAllowed);
        await WaitUntilAsync(() => machine.IsHomeAllowed);

        var homing = machine.HomeAsync(CancellationToken.None);
        await WaitUntilAsync(() => state.IsHoming);
        Assert.True(state.IsRunning);
        Assert.True(state.IsHoming);
        Assert.Equal(MachineDisplayState.Homing, services.GetRequiredService<OperationViewModel>().MachineDisplayState);
        Assert.False(state.ManualSetupEnabled);
        Assert.False(machine.IsHomeAllowed);
        await homing;
        await WaitUntilAsync(() => machine.IsHomeAllowed && state.FeedbackReadiness.Homed);
        Assert.True(machine.IsHomeAllowed);
        Assert.True(state.FeedbackReadiness.Homed);
        Assert.False(state.IsHoming);
        Assert.True(moved);
        Assert.False(movedBeforeRaised);
        Assert.Equal(cylinders.Order(), outputChanges.Order());
        Assert.All(cylinders, output => Assert.False(io.GetOutput(output)));
        Assert.True(io.GetInput(InputIo.PcbPlacementIpmUp));
        Assert.False(io.GetInput(InputIo.PcbPlacementIpmDown));
        await machine.HomeAsync(CancellationToken.None);
        Assert.True(machine.IsHomeAllowed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartRaisesCylindersBeforeAutomaticAndPreservesHeldPcb(bool holdingPcb)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.PcbPlacement);
        settings.Options.UseDoorInterlock = false;
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var placement = services.GetRequiredService<PcbPlacer>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        await placement.SetLiftDownAsync(true);
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.PcbPlacementIpmDown, true);
        io.SetInput(InputIo.PcbPlacementPcbDetected, holdingPcb);
        io.SetInput(InputIo.AutoMode, false);
        var started = false;
        var readyBeforeStarting = false;
        void StopAtAutomaticStart()
        {
            if (!state.AutomaticRunning || started)
                return;
            started = true;
            readyBeforeStarting = placement.Lift == StationCylinderState.Up
                && placement.IpmLift == (holdingPcb ? StationCylinderState.Down : StationCylinderState.Up);
            machine.Stop();
        }

        state.Changed += StopAtAutomaticStart;
        try
        {
            Assert.True(machine.IsStartAllowed);
            await machine.StartAsync().WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(started);
            Assert.True(readyBeforeStarting);
            Assert.Equal(holdingPcb, io.GetOutput(OutputIo.PcbPlacementIpmDown));
            Assert.Equal(MachineAlarm.None, state.Alarm);
            Assert.False(state.IsRunning);
        }
        finally
        {
            state.Changed -= StopAtAutomaticStart;
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task HomePreparationStopsBeforeLiftingIpmWhenPlacementDetectsPcb()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.PcbPlacement);
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        IIoService signals = io;
        await signals.SetOutputAndWaitAsync(OutputIo.PcbPlacementHandlerDown, true);
        await signals.SetOutputAndWaitAsync(OutputIo.PcbPlacementIpmDown, true);
        void DetectPcb(OutputIo output, bool on)
        {
            if (output == OutputIo.PcbPlacementHandlerDown && !on)
                io.SetInput(InputIo.PcbPlacementPcbDetected, true);
        }

        io.OutputChanged += DetectPcb;
        try
        {
            Assert.True(machine.IsHomeAllowed);
            await machine.HomeAsync(CancellationToken.None);
            Assert.True(io.GetInput(InputIo.PcbPlacementPcbDetected));
            Assert.True(io.GetOutput(OutputIo.PcbPlacementIpmDown));
            Assert.Equal(MachineAlarm.None, state.Alarm);
            Assert.False(state.IsRunning);
            Assert.False(machine.IsHomeAllowed);
        }
        finally
        {
            io.OutputChanged -= DetectPcb;
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HomeAndStartPreparationStopBeforeAxesOnStopOrTimeout(bool start)
    {
        var settings = new MachineSettings
        {
            Units = EnableOnly(MachineUnit.Inspection),
        };
        FastHomes(settings);
        settings.Options.TimeoutMilliseconds = 500;
        await using var services = CreateServices(settings);
        PrepareCarrierTeaching(settings, services.GetRequiredService<RecipeManager>().Current);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        if (start)
            await machine.HomeAsync(CancellationToken.None);
        io.AutoResponseEnabled = false;
        io.SetOutput(OutputIo.NgCarrierPickupDown, true);
        io.SetOutput(OutputIo.PcbPlacementHandlerDown, true);
        io.SetInput(InputIo.NgCarrierPickupUp, false);
        io.SetInput(InputIo.NgCarrierPickupDown, true);
        var operation = services.GetRequiredService<OperationViewModel>();
        var command = start ? operation.StartCommand : operation.HomeCommand;
        var running = command.ExecuteAsync(null);
        await WaitUntilAsync(() => state.IsRunning && !io.GetOutput(OutputIo.NgCarrierPickupDown));
        Assert.Equal(!start, state.IsHoming);
        Assert.False(state.AutomaticRunning);
        Assert.True(operation.StopCommand.CanExecute(null));
        await operation.StopCommand.ExecuteAsync(null);
        await running;
        Assert.False(state.IsHoming);
        Assert.Equal(start, state.FeedbackReadiness.Homed);
        Assert.Equal(MachineAlarm.None, state.Alarm);
        Assert.False(io.GetOutput(OutputIo.NgCarrierPickupDown));
        Assert.True(io.GetOutput(OutputIo.PcbPlacementHandlerDown));
        Assert.True(machine.IsHomeAllowed);

        await command.ExecuteAsync(null);
        Assert.Equal(MachineAlarm.NgCarrierTransfer, state.Alarm);
        Assert.False(io.GetOutput(OutputIo.NgCarrierPickupDown));
        Assert.False(state.IsRunning);
        Assert.Equal(start, state.FeedbackReadiness.Homed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HomePreparationFailureAfterStopIsReportedWithoutReplacingSafetyAlarm(bool safetyStop)
    {
        var settings = new MachineSettings { Units = EnableOnly(MachineUnit.Inspection) };
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var failure = new IOException("Cylinder output failed after STOP.");
        await machine.InitializeAsync();
        io.SetOutput(OutputIo.NgCarrierPickupDown, true);
        void FailAfterStop(OutputIo output, bool on)
        {
            if (output != OutputIo.NgCarrierPickupDown || on)
                return;
            io.OutputChanged -= FailAfterStop;
            if (safetyStop)
                io.SetInput(InputIo.AirPressureHigh, false);
            else
                machine.Stop();
            throw failure;
        }

        io.OutputChanged += FailAfterStop;
        try
        {
            Assert.True(machine.IsHomeAllowed);
            await machine.HomeAsync(CancellationToken.None);

            Assert.Equal(
                safetyStop ? MachineAlarm.AirPressureLow : MachineAlarm.NgCarrierTransfer,
                state.Alarm);
            if (safetyStop)
                Assert.Null(state.AlarmDetail);
            else
                Assert.Equal(failure.ToString(), state.AlarmDetail);
            var entry = Assert.Single(
                services.GetRequiredService<ApplicationLog>().Snapshot(),
                entry => entry.Detail == failure.ToString());
            Assert.Contains(nameof(MachineAlarm.NgCarrierTransfer), entry.Message);
            Assert.False(services.GetRequiredService<OperationCancellation>().HasActiveOperations);
            Assert.False(state.IsRunning);
        }
        finally
        {
            io.OutputChanged -= FailAfterStop;
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task InspectionHomeRaisesPickupAndIgnoresCarrierInput()
    {
        var settings = new MachineSettings
        {
            Units = EnableOnly(MachineUnit.Inspection),
        };
        FastHomes(settings);
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var gantry = services.GetRequiredService<InspectionStation>();
        IIoService signals = io;
        await machine.InitializeAsync();
        await signals.SetOutputAndWaitAsync(OutputIo.NgCarrierGripperClose, true);
        await signals.SetOutputAndWaitAsync(OutputIo.NgCarrierPickupDown, true);
        io.SetInput(InputIo.NgCarrierDetected, true);

        Assert.True(machine.IsHomeAllowed);
        await Assert.ThrowsAsync<MotionInterlockException>(() => gantry.HomeAxisAsync(MotionAxis.X));
        Assert.True(io.GetOutput(OutputIo.NgCarrierGripperClose));
        Assert.False(state.FeedbackReadiness.Homed);

        var transfer = services.GetRequiredService<InspectionStation>();
        Assert.False(transfer.IsRaised);
        Assert.True(io.GetOutput(OutputIo.NgCarrierPickupDown));

        var unsafeMovement = false;
        gantry.Motion.Feedback.MovingChanged += moving =>
        {
            if (moving && state.IsHoming)
            {
                unsafeMovement |= !transfer.IsRaised;
            }
        };
        await machine.HomeAsync(CancellationToken.None);
        Assert.True(state.FeedbackReadiness.Homed);
        Assert.False(unsafeMovement);
        Assert.True(transfer.IsRaised);
        Assert.True(io.GetInput(InputIo.NgCarrierDetected));
        Assert.True(io.GetOutput(OutputIo.NgCarrierGripperClose));

        io.SetInput(InputIo.NgCarrierPickupUp, false);
        io.SetInput(InputIo.NgCarrierPickupDown, true);
        await Assert.ThrowsAsync<MotionInterlockException>(() => gantry.JogAsync(MotionAxis.X, 10));
        await Assert.ThrowsAsync<MotionInterlockException>(
            async () => await gantry.MoveToAsync(new AxisPosition { X = 20, Y = 10 }, 100));
        io.SetInput(InputIo.NgCarrierPickupDown, false);
        io.SetInput(InputIo.NgCarrierPickupUp, true);
        var moving = gantry.MoveToAsync(new AxisPosition { X = 20, Y = 10 }, 10);
        await WaitUntilAsync(() => gantry.Motion.Feedback.IsMoving);
        io.SetInput(InputIo.NgCarrierPickupUp, false);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => moving);
        Assert.False(gantry.Motion.Feedback.IsMoving);
        Assert.Equal(MachineAlarm.NgCarrierTransfer, state.Alarm);
        Assert.Contains("pickup Up", state.AlarmDetail);
        Assert.Contains("Current lift: Between", state.AlarmDetail);

        io.SetInput(InputIo.NgCarrierPickupUp, true);
        await machine.ResetAsync();
        await gantry.MoveToAsync(new AxisPosition { X = 20, Y = 10 }, 1000);
        Assert.Equal(MachineAlarm.None, state.Alarm);
    }

    [Theory]
    [InlineData(MotionGroup.PcbPlacementHandler, InputIo.PcbPlacementHandlerUp, InputIo.PcbPlacementHandlerDown)]
    [InlineData(MotionGroup.BoltFastening, InputIo.PickupHeadUp, InputIo.PickupHeadDown)]
    [InlineData(MotionGroup.BoltFastening, InputIo.ShootingHeadUp, InputIo.ShootingHeadDown)]
    public async Task MotionRequiresRaisedCylindersWhileFasteningZCanRetract(
        MotionGroup group,
        InputIo up,
        InputIo down)
    {
        var settings = FlowSettings();
        settings.PcbPlacementHandler.Motion.HorizontalSpeed = 10;
        settings.BoltFastening.Motion.HorizontalSpeed = 10;
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var placement = services.GetRequiredService<PcbPlacer>();
        var fastening = services.GetRequiredService<BoltFasteningStation>();
        var isPlacement = group == MotionGroup.PcbPlacementHandler;
        var feedback = isPlacement ? placement.Motion.Feedback : fastening.Motion.Feedback;
        Task MoveXY()
        {
            return isPlacement
                ? placement.MoveToXYAsync(new() { X = 20, Y = 20 })
                : fastening.MoveToXYAsync(20, 20);
        }

        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        io.AutoResponseEnabled = false;

        io.SetInput(up, false);
        io.SetInput(down, true);
        if (isPlacement)
            await Assert.ThrowsAsync<MotionInterlockException>(() => placement.MoveAxisAsync(MotionAxis.Z, 1));
        else
            await fastening.MoveZAsync(1);
        Assert.Equal(isPlacement ? 0 : 1, feedback.Position.Z);
        Assert.Equal(MachineAlarm.None, state.Alarm);
        await Assert.ThrowsAsync<MotionInterlockException>(MoveXY);
        await Assert.ThrowsAsync<MotionInterlockException>(
            () => isPlacement
                ? placement.HomeAxisAsync(MotionAxis.X)
                : fastening.HomeAxisAsync(MotionAxis.X));
        if (isPlacement)
            await Assert.ThrowsAsync<MotionInterlockException>(() => placement.JogAsync(MotionAxis.Y, 10));

        io.SetInput(up, true);
        await Assert.ThrowsAsync<MotionInterlockException>(MoveXY);
        io.SetInput(down, false);
        var move = MoveXY();
        await WaitUntilAsync(() => feedback.IsMovingHorizontal);
        io.SetInput(up, false);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => move);
        Assert.False(feedback.IsMoving);
        Assert.False(feedback.IsMovingHorizontal);
        Assert.Equal(isPlacement ? MachineAlarm.PcbPlacement : MachineAlarm.BoltFastening, state.Alarm);
        Assert.Contains(isPlacement ? "axis movement" : "horizontal movement", state.AlarmDetail);
        Assert.Contains("Between", state.AlarmDetail);
    }

    [Fact]
    public async Task PlacementHoldingPcbBlocksIpmRaiseForHomeButNotHorizontalTravel()
    {
        var settings = FlowSettings();
        settings.PcbPlacementHandler.Motion.HorizontalSpeed = 100;
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var placement = services.GetRequiredService<PcbPlacer>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        Assert.True(state.FeedbackReadiness.Homed);
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.PcbPlacementIpmDown, true);
        io.SetInput(InputIo.PcbPlacementPcbDetected, true);
        Assert.False(machine.IsHomeAllowed);
        Assert.Equal(HomeBlockReason.PlacementHoldingPcb, machine.HomeBlock);
        Assert.Equal(StationCylinderState.Up, placement.Lift);
        Assert.True(io.GetInput(InputIo.PcbPlacementIpmDown));

        var move = placement.MoveToXYAsync(new() { X = 20, Y = 20 });
        await WaitUntilAsync(() => placement.Motion.Feedback.IsMovingHorizontal);
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.PcbPlacementIpmDown, false);
        await move;

        Assert.Equal(HomeBlockReason.None, machine.HomeBlock);
        Assert.Equal(
            (20, 20, settings.PcbPlacementHandler.HandoffPosition.Z),
            placement.Motion.Feedback.Position);
        Assert.Equal(MachineAlarm.None, state.Alarm);
    }

    [Fact]
    public async Task ManualBoltPickupAndReturnPreserveOrderAndVacuumOnStopAndRetry()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.BoltFastening);
        settings.BoltFastening.SafeZ = 5;
        settings.BoltFastening.PickupPosition = new() { X = 40, Y = 30, Z = 12 };
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var gantry = services.GetRequiredService<BoltFasteningStation>();
        var teaching = services.GetRequiredService<TeachingViewModel>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        await gantry.MoveZAsync(14);
        teaching.SelectedTeachingUnit = HardwareArea.BoltFastening;
        teaching.SelectedPoint = teaching.FilteredPoints.Single(
            point => point.Position.Target == TeachingTarget.BoltPickup);
        Assert.Equal(TeachingSaveBehavior.BoltPickup, teaching.SaveBehavior);
        await WaitUntilAsync(() => teaching.MoveToPointCommand.CanExecute(null));

        // Hold the table feedback; both heads must remain raised throughout pickup.
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.PickupTableDown, false);
        io.AutoResponseEnabled = false;
        io.SetOutput(OutputIo.PickupHeadVacuumPump, true);
        var vacuumChanged = false;
        var movedXyWithHeadDown = false;
        var loweredAt = new ConcurrentQueue<(double X, double Y, double Z)>();
        var tableDescents = 0;
        io.OutputChanged += (output, value) =>
        {
            if (output is OutputIo.PickupHeadVacuumPump or OutputIo.ShootingHeadVacuumPump)
                vacuumChanged = true;
            if (value && output is OutputIo.PickupHeadDown or OutputIo.ShootingHeadDown)
                loweredAt.Enqueue(gantry.Motion.Feedback.Position);
            if (output == OutputIo.PickupTableDown && value)
            {
                Assert.True(gantry.IsHorizontalMoveAllowed);
                Assert.Equal(settings.BoltFastening.SafeZ, gantry.Motion.Feedback.Position.Z);
                Interlocked.Increment(ref tableDescents);
            }
        };
        gantry.Motion.Feedback.PositionChanged += (_, _, _) =>
            movedXyWithHeadDown |= gantry.Motion.Feedback.IsMovingHorizontal
                && !gantry.IsHorizontalMoveAllowed;

        var move = teaching.MoveToPointCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => tableDescents == 1);
        Assert.Equal((0, 0, 5), gantry.Motion.Feedback.Position);
        Assert.False(move.IsCompleted);
        Assert.False(io.GetInput(InputIo.PickupTableDown));
        teaching.JogStopCommand.Execute(null);
        await move.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal((0, 0, 5), gantry.Motion.Feedback.Position);
        Assert.False(gantry.Motion.Feedback.IsMoving);
        Assert.True(io.GetOutput(OutputIo.PickupTableDown)); // Stop keeps pneumatic outputs.

        await WaitUntilAsync(() => teaching.MoveToPointCommand.CanExecute(null));
        var retry = teaching.MoveToPointCommand.ExecuteAsync(null);
        Assert.False(retry.IsCompleted);
        io.SetInputs((InputIo.PickupTableUp, false), (InputIo.PickupTableDown, true));
        await retry.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal((40, 30, 12), gantry.Motion.Feedback.Position);
        Assert.Equal(StationCylinderState.Up, gantry.PickupHeadPosition);
        Assert.Equal(StationCylinderState.Up, gantry.ShootingHeadPosition);
        await WaitUntilAsync(() => teaching.ReturnFromPickupCommand.CanExecute(null));
        var stopAtSafeZ = true;
        gantry.Motion.Feedback.PositionChanged += (_, _, z) =>
        {
            if (stopAtSafeZ
                && Math.Abs(z - settings.BoltFastening.SafeZ) <= MotionService.PositionToleranceMillimeters)
            {
                stopAtSafeZ = false;
                teaching.JogStopCommand.Execute(null);
            }
        };
        await teaching.ReturnFromPickupCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(stopAtSafeZ);
        Assert.Equal((40, 30, 5), gantry.Motion.Feedback.Position);
        Assert.True(gantry.IsHorizontalMoveAllowed);
        Assert.False(gantry.Motion.Feedback.IsMoving);

        var returning = teaching.ReturnFromPickupCommand.ExecuteAsync(null);
        await returning.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal((40, 30, 5), gantry.Motion.Feedback.Position);
        Assert.True(gantry.IsHorizontalMoveAllowed);
        await WaitUntilAsync(() => teaching.MoveToPointCommand.CanExecute(null));
        Assert.True(io.GetOutput(OutputIo.PickupHeadVacuumPump));
        Assert.False(io.GetOutput(OutputIo.ShootingHeadVacuumPump));
        Assert.False(vacuumChanged);
        Assert.False(movedXyWithHeadDown);
        Assert.Empty(loweredAt);
        Assert.Equal(MachineAlarm.None, state.Alarm);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManualPickupAndReturnReportCylinderTimeout(bool returning)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.BoltFastening);
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var gantry = services.GetRequiredService<BoltFasteningStation>();
        var teaching = services.GetRequiredService<TeachingViewModel>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        teaching.SelectedTeachingUnit = HardwareArea.BoltFastening;
        teaching.SelectedPoint = teaching.FilteredPoints.Single(
            point => point.Position.Target == TeachingTarget.BoltPickup);
        if (returning)
        {
            await gantry.MoveToTeachingPositionAsync(teaching.SelectedPoint.Position, settings.BoltFastening.PickupPosition);
            // Feedback can change outside the command; an UP request is not confirmation.
            io.SetInputs((InputIo.PickupHeadUp, false), (InputIo.PickupHeadDown, true));
        }
        io.AutoResponseEnabled = false;
        settings.Options.TimeoutMilliseconds = 50;

        await (returning ? teaching.ReturnFromPickupCommand : teaching.MoveToPointCommand).ExecuteAsync(null);

        Assert.Equal(MachineAlarm.BoltFastening, state.Alarm);
        Assert.False(gantry.Motion.Feedback.IsMoving);
        Assert.Equal(settings.BoltFastening.SafeZ, gantry.Motion.Feedback.Position.Z);
        Assert.False(io.GetOutput(OutputIo.PickupHeadVacuumPump));
        Assert.False(io.GetOutput(OutputIo.ShootingHeadVacuumPump));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FasteningAdjustmentStopsOnModeOrServoLoss(bool autoMode)
    {
        await using var services = CreateServices(FlowSettings());
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<VirtualIoService>();
        var gantry = services.GetRequiredService<BoltFasteningStation>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        await gantry.MoveZAsync(10);
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.ShootingHeadDown, true);
        var motion = services.GetRequiredKeyedService<IXyMotion>(MotionGroup.BoltFastening);
        var jog = motion.JogAsync(MotionAxis.X, 1);
        await WaitUntilAsync(() => gantry.Motion.Feedback.Position.X > 0);
        if (autoMode)
            io.SetInput(InputIo.AutoMode, false);
        else
            motion.SetServo(MotionAxis.X, false);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => jog.WaitAsync(TimeSpan.FromSeconds(2)));
        var stopped = gantry.Motion.Feedback.Position;
        Assert.Equal(MotionCommand.None, gantry.Motion.Feedback.Command);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => gantry.AdjustAxisAsync(MotionAxis.X, 20, 1));
        Assert.Equal(stopped, gantry.Motion.Feedback.Position);
        Assert.True(io.GetInput(InputIo.ShootingHeadDown));
    }

    [Fact]
    public async Task CylinderFeedbackIsRecheckedBetweenTravelZAndXy()
    {
        var settings = FlowSettings();
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<VirtualIoService>();
        var gantry = services.GetRequiredService<BoltFasteningStation>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        await gantry.MoveZAsync(10);
        var before = gantry.Motion.Feedback.Position;
        io.AutoResponseEnabled = false;
        gantry.Motion.Feedback.MovingChanged += moving =>
        {
            if (moving && !gantry.Motion.Feedback.IsMovingHorizontal)
                io.SetInput(InputIo.PickupHeadUp, false);
        };

        await Assert.ThrowsAsync<MotionInterlockException>(() => gantry.MoveToXYAsync(20, 20));

        var after = gantry.Motion.Feedback.Position;
        Assert.Equal(before.X, after.X);
        Assert.Equal(before.Y, after.Y);
        Assert.Equal(settings.BoltFastening.SafeZ, after.Z);
        Assert.False(gantry.Motion.Feedback.IsMoving);
    }

    [Theory]
    [InlineData(MachineUnit.PcbSupply, MotionGroup.PcbSupply)]
    [InlineData(MachineUnit.PcbPlacement, MotionGroup.PcbPlacementHandler)]
    [InlineData(MachineUnit.BoltFastening, MotionGroup.BoltFastening)]
    [InlineData(MachineUnit.Inspection, MotionGroup.InspectionGantry)]
    public async Task OnlyEnabledMotionsAreInitializedReadResetAndHomed(
        MachineUnit unit,
        MotionGroup group)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(unit);
        await using var services = CreateMotionScopeServices(settings, out var probes);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var manual = services.GetRequiredService<MotionWindowViewModel>();
        foreach (var (candidate, probe) in probes)
        {
            if (candidate == group)
                continue;
            // Also simulate an initialized drive that was subsequently disabled.
            probe.ReportReady = true;
            probe.FailHardwareCalls = true;
        }

        await machine.InitializeAsync();
        Assert.Equal(MachineAlarm.None, state.Alarm);
        Assert.True(state.Available);
        Assert.False(state.FeedbackReadiness.Faulted);
        Assert.True(machine.IsHomeAllowed);
        await machine.HomeAsync(CancellationToken.None);
        await WaitUntilAsync(() => state.Ready);
        Assert.True(state.Ready);
        await WaitUntilAsync(() => state.ManualControlsEnabled);

        probes[group].Motion.SetServo(MotionAxis.X, false);
        await WaitUntilAsync(() => machine.IsResetAllowed);
        await machine.ResetAsync();
        await WaitUntilAsync(() => state.Ready);
        Assert.True(state.Ready);
        Assert.Equal(1, probes[group].ResetCalls);

        if (group == MotionGroup.BoltFastening)
        {
            var teaching = services.GetRequiredService<TeachingViewModel>();
            teaching.SelectedTeachingUnit = HardwareArea.BoltFastening;
            teaching.StepDistance = 0.1;
            await WaitUntilAsync(() => teaching.StepCommand.CanExecute(TeachingDirection.XPlus));
            var before = probes[group].Motion.Position;
            await teaching.StepCommand.ExecuteAsync(TeachingDirection.XPlus);
            Assert.Equal(before.X + 0.1, probes[group].Motion.Position.X, precision: 6);
        }

        if (group is MotionGroup.PcbSupply or MotionGroup.PcbPlacementHandler)
        {
            var placer = services.GetRequiredService<PcbPlacer>();
            Assert.NotEqual(PcbPlacementState.ReceivingPcb,
                placer.Phase);
        }

        foreach (var row in manual.Axes.Where(row => row.Group != group))
        {
            Assert.False(row.ToggleServoCommand.CanExecute(null));
            Assert.False(row.HomeCommand.CanExecute(null));
            Assert.Null(row.Diagnostics.Snapshot.State);
            // Bypassing CanExecute still must not command a disabled drive.
            row.ToggleServoCommand.Execute(null);
            await row.HomeCommand.ExecuteAsync(null);
        }

        Assert.All(
            probes.Where(item => item.Key != group),
            item => Assert.Equal(0, item.Value.HardwareCalls));
        Assert.Equal(MachineAlarm.None, state.Alarm);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ResetDoesNotClearMachineAlarmWhenMotionFeedbackRemainsFaulted(bool alarmRemains)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.PcbSupply);
        await using var services = CreateMotionScopeServices(settings, out var probes);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var probe = probes[MotionGroup.PcbSupply];
        await machine.InitializeAsync();
        probe.OverrideState = feedback => feedback with { Alarm = alarmRemains, ServoOn = alarmRemains };
        await WaitUntilAsync(() => machine.IsResetAllowed);

        await machine.ResetAsync();

        Assert.Equal(1, probe.ResetCalls);
        Assert.Equal(MachineAlarm.MotionUnavailable, state.Alarm);
        Assert.Contains("not confirmed by hardware feedback", state.AlarmDetail);
        Assert.False(state.Ready);
    }

    [Fact]
    public async Task DisablingAFailedMotionAllowsResetWithoutHidingAnEnabledMotionFailure()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.PcbSupply);
        await using var services = CreateMotionScopeServices(settings, out var probes);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        probes[MotionGroup.PcbSupply].FailHardwareCalls = true;

        await machine.InitializeAsync();
        Assert.Equal(MachineAlarm.MotionUnavailable, state.Alarm);
        Assert.False(state.ManualControlsEnabled);
        var failedCalls = probes[MotionGroup.PcbSupply].HardwareCalls;

        settings.Units.PcbSupply = false;
        settings.Units.PcbPlacement = true;
        Assert.Equal(MachineAlarm.MotionUnavailable, state.Alarm);
        await machine.ResetAsync();
        await machine.HomeAsync(CancellationToken.None);
        Assert.Equal(MachineAlarm.None, state.Alarm);
        Assert.True(state.Ready);
        Assert.True(state.ManualControlsEnabled);
        Assert.Equal(failedCalls, probes[MotionGroup.PcbSupply].HardwareCalls);
        // Re-enabling the same faulty hardware makes it mandatory again.
        settings.Units.PcbSupply = true;
        Assert.True(state.FeedbackReadiness.Faulted);
        Assert.False(state.ManualControlsEnabled);
        var placementResets = probes[MotionGroup.PcbPlacementHandler].ResetCalls;
        await machine.ResetAsync();
        Assert.Equal(MachineAlarm.MotionUnavailable, state.Alarm);
        Assert.Equal(placementResets + 1, probes[MotionGroup.PcbPlacementHandler].ResetCalls);
    }

    [Theory]
    [InlineData(InputIo.PickupHeadUp, false, false)]
    [InlineData(InputIo.PcbPlacementIpmUp, false, false)]
    [InlineData(InputIo.PcbPlacementIpmUp, false, true)]
    public async Task HomeStopsWhenItsCylinderConditionChanges(
        InputIo input,
        bool value,
        bool teachingHome)
    {
        var settings = FlowSettings();
        foreach (var (motionSettings, _) in settings.MotionSections)
            motionSettings.ZHome.SearchSpeed = 20;
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var placement = services.GetRequiredService<PcbPlacer>();
        var fastening = services.GetRequiredService<BoltFasteningStation>();
        await machine.InitializeAsync();
        await Task.WhenAll(
            services.GetRequiredKeyedService<IXyMotion>(MotionGroup.PcbPlacementHandler)
                .MoveAxisAsync(MotionAxis.Z, 50, 10_000),
            services.GetRequiredKeyedService<IXyMotion>(MotionGroup.BoltFastening)
                .MoveAxisAsync(MotionAxis.Z, 50, 10_000));
        var teaching = services.GetRequiredService<TeachingViewModel>();
        teaching.SelectedTeachingUnit = HardwareArea.PcbPlacementHandler;
        await WaitUntilAsync(() => teaching.HomeCommand.CanExecute(null));
        var homing = teachingHome
            ? teaching.HomeCommand.ExecuteAsync(null)
            : machine.HomeAsync(CancellationToken.None);
        await WaitUntilAsync(() => placement.Motion.Feedback.IsMoving);
        Assert.False(fastening.Motion.Feedback.IsMoving);
        Assert.False(state.ManualSetupEnabled);
        io.SetInput(input, value);
        await homing.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(state.IsHoming);
        Assert.False(placement.Motion.Feedback.IsMoving);
        Assert.False(fastening.Motion.Feedback.IsMoving);
        Assert.False(placement.Motion.Feedback.GetAxisState(MotionAxis.Z).Homed);
        Assert.Equal(50, fastening.Motion.Feedback.Position.Z);
        Assert.NotEqual(HomeBlockReason.None, machine.GetHomeBlock(requireRaised: true));
        await WaitUntilAsync(() => machine.IsHomeAllowed);
        if (teachingHome)
        {
            await WaitUntilAsync(() => teaching.HomeCommand.CanExecute(null));
        }
    }

    [Fact]
    public async Task MotionAlarmBlocksHomeAndStopsAllHomingAxes()
    {
        var settings = FlowSettings();
        foreach (var (motionSettings, _) in settings.MotionSections)
            motionSettings.ZHome.SearchSpeed = 20;
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var supply = (VirtualMotionService)services.GetRequiredKeyedService<IXyMotion>(
            MotionGroup.PcbSupply);
        var fastening = (VirtualMotionService)services.GetRequiredKeyedService<IXyMotion>(
            MotionGroup.BoltFastening);
        await machine.InitializeAsync();

        fastening.SetAlarm(MotionAxis.X, true);
        await WaitUntilAsync(() => !machine.IsHomeAllowed);
        await WaitUntilAsync(() => machine.IsResetAllowed);
        await machine.ResetAsync();
        Assert.True(machine.IsHomeAllowed);

        await Task.WhenAll(
            supply.MoveAxisAsync(MotionAxis.Z, 50, 10_000),
            fastening.MoveAxisAsync(MotionAxis.Z, 50, 10_000));
        // The command can finish before the motion scan publishes stopped feedback.
        await WaitUntilAsync(() => machine.IsHomeAllowed);
        var homing = machine.HomeAsync(CancellationToken.None);
        Assert.True(await VirtualTest.WaitUntilAsync(
            () => supply.IsMoving && fastening.IsMoving, TimeSpan.FromSeconds(2)),
            $"Home completed={homing.IsCompleted}, IsHomeAllowed={machine.IsHomeAllowed}, "
                + $"block={machine.HomeBlock}, alarm={state.Alarm}, detail={state.AlarmDetail}");
        fastening.SetAlarm(MotionAxis.X, true);
        await homing.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(MachineAlarm.MotionUnavailable, state.Alarm);
        Assert.False(state.IsHoming);
        Assert.False(state.IsRunning);
        Assert.False(supply.IsMoving);
        Assert.False(fastening.IsMoving);
        Assert.False(supply.GetAxisState(MotionAxis.Z).Homed);
        Assert.False(fastening.GetAxisState(MotionAxis.Z).Homed);
    }

    [Fact]
    public async Task ParallelHomeReportsEachUnitsFailureAfterTheFirstFailureStopsHome()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.PcbSupply);
        settings.Units.BoltFastening = true;
        var results = new Dictionary<MotionGroup, HomeResultMotion>();
        IXyMotion Wrap(IServiceProvider provider, MotionGroup group)
        {
            var motion = DispatchProxy.Create<IXyMotion, HomeResultMotion>();
            var result = (HomeResultMotion)motion;
            result.Motion = provider.GetRequiredKeyedService<IXyMotion>(group);
            result.AwaitCleanupAfterCancellation = true;
            results.Add(group, result);
            return motion;
        }

        await using var services = new ServiceCollection().AddSingleton(_ => VirtualTest.OpenMachineStore())
            .AddIbtmApplication(settings)
            .AddSingleton<IReadOnlyDictionary<MotionGroup, IXyMotion>>(provider =>
                Enum.GetValues<MotionGroup>().ToDictionary(group => group,
                    group => group is MotionGroup.PcbSupply or MotionGroup.BoltFastening
                        ? Wrap(provider, group) : provider.GetRequiredKeyedService<IXyMotion>(group)))
            .BuildServiceProvider();
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var log = services.GetRequiredService<ApplicationLog>();
        await machine.InitializeAsync();
        var homing = machine.HomeAsync(default);
        var firstFailure = new MotionException("Supply home", new IOException("Supply home failed."));
        var stopFailure = new MotionException("Fastening STOP", new IOException("Fastening stop failed."));
        try
        {
            await Task.WhenAll(results.Values.Select(result => result.Started.Task))
                .WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(state.IsHoming);
            results[MotionGroup.PcbSupply].Result.SetException(firstFailure);
            await WaitUntilAsync(() => results[MotionGroup.BoltFastening].HomeCancellation.IsCancellationRequested);
            Assert.False(homing.IsCompleted);
            results[MotionGroup.BoltFastening].Result.SetException(stopFailure);
            await homing.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Contains("Supply home failed.", state.AlarmDetail);
            Assert.Contains(log.Snapshot(), entry => entry.Detail?.Contains("Fastening stop failed.") == true);
            Assert.False(state.IsHoming);
            Assert.All(results.Values, result => Assert.Equal(0, result.HorizontalHomeCalls));
            Assert.False(services.GetRequiredService<OperationCancellation>().HasActiveOperations);
        }
        finally
        {
            foreach (var result in results.Values)
                result.Result.TrySetCanceled();
            machine.Stop();
            await homing;
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(true, false, false, true)]
    [InlineData(false, false, false, true)]
    public async Task FailedHomeReportsCauseAndStopsOtherHomingAxes(
        bool exception,
        bool individual,
        bool teachingHome,
        bool safetyStop)
    {
        var settings = FlowSettings();
        foreach (var (motionSettings, _) in settings.MotionSections)
            motionSettings.ZHome.SearchSpeed = 1;
        HomeResultMotion? homeResult = null;
        await using var services = new ServiceCollection().AddSingleton(_ => VirtualTest.OpenMachineStore())
            .AddIbtmApplication(settings)
            .AddSingleton<IReadOnlyDictionary<MotionGroup, IXyMotion>>(provider =>
            {
                var motions = Enum.GetValues<MotionGroup>().ToDictionary(
                    group => group, group => provider.GetRequiredKeyedService<IXyMotion>(group));
                var motion = DispatchProxy.Create<IXyMotion, HomeResultMotion>();
                homeResult = (HomeResultMotion)motion;
                homeResult.AwaitCleanupAfterCancellation = safetyStop;
                homeResult.Motion = motions[MotionGroup.BoltFastening];
                motions[MotionGroup.BoltFastening] = motion;
                return motions;
            })
            .BuildServiceProvider();
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var placement = services.GetRequiredKeyedService<IXyMotion>(MotionGroup.PcbPlacementHandler);
        var supply = services.GetRequiredKeyedService<IXyMotion>(MotionGroup.PcbSupply);
        var manual = services.GetRequiredService<MotionWindowViewModel>();
        await machine.InitializeAsync();
        await supply.MoveAxisAsync(MotionAxis.Z, 50, 10_000);

        var axisRow = manual.Axes.Single(
            row => row.Group == MotionGroup.BoltFastening && row.Axis == MotionAxis.Z);
        var teaching = services.GetRequiredService<TeachingViewModel>();
        teaching.SelectedTeachingUnit = HardwareArea.BoltFastening;
        var homing = teachingHome
            ? teaching.HomeCommand.ExecuteAsync(null)
            : individual
                ? axisRow.HomeCommand.ExecuteAsync(null)
                : machine.HomeAsync(CancellationToken.None);
        try
        {
            await homeResult!.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await WaitUntilAsync(
                () => individual || teachingHome ? state.IsHoming : supply.IsMoving);
            if (safetyStop)
            {
                services.GetRequiredService<VirtualIoService>().SetInput(InputIo.EmergencyStop1Pressed, true);
            }
            if (exception)
            {
                homeResult!.Result.SetException(
                    new MotionException("Home", new InvalidOperationException("Home command failed.")));
            }
            else
            {
                homeResult!.Result.SetResult(false);
            }

            await homing.WaitAsync(TimeSpan.FromSeconds(2));

            var expectedAlarm = safetyStop ? MachineAlarm.EmergencyStop : MachineAlarm.HomeFailed;
            Assert.Equal(expectedAlarm, state.Alarm);
            if (exception)
                Assert.Contains("Home command failed.", state.AlarmDetail);
            await WaitUntilAsync(() => state.Alarm == expectedAlarm && !state.IsRunning);
            // A latched home failure does not block a retry while the axis feedback remains healthy.
            Assert.Equal(!safetyStop, axisRow.HomeCommand.CanExecute(null));
            Assert.False(state.IsHoming);
            Assert.False(placement.IsMoving);
            Assert.False(supply.IsMoving);
            Assert.Equal(!individual && !teachingHome, placement.GetAxisState(MotionAxis.Z).Homed);
            Assert.False(supply.GetAxisState(MotionAxis.Z).Homed);
            Assert.Equal(0, homeResult.HorizontalHomeCalls);
        }
        finally
        {
            axisRow.HomeCommand.Cancel();
            teaching.HomeCommand.Cancel();
            machine.Stop();
            await homing;
        }
    }

    public enum ManualCommandFailure
    {
        Motion,
        Canceled,
        Safety,
        Programming,
    }

    [Theory]
    [InlineData(MotionGroup.PcbSupply, HardwareArea.PcbSupply, MachineUnit.PcbSupply)]
    [InlineData(MotionGroup.PcbPlacementHandler, HardwareArea.PcbPlacementHandler, MachineUnit.PcbPlacement)]
    public async Task TeachingHandlerJogAndStepKeepCurrentZ(
        MotionGroup group, HardwareArea area, MachineUnit unit)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(unit);
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var motion = services.GetRequiredKeyedService<IXyMotion>(group);
        var teaching = services.GetRequiredService<TeachingViewModel>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        teaching.SelectedTeachingUnit = area;
        await motion.MoveAxisAsync(MotionAxis.Z, 7, 1_000);
        try
        {
            await WaitUntilAsync(() => teaching.Motion.Position.Z == 7
                && teaching.StepCommand.CanExecute(TeachingDirection.XPlus));
            Assert.Equal(TeachingMotionHint.None, teaching.MotionHint);
            var before = motion.Position;
            await teaching.StepCommand.ExecuteAsync(TeachingDirection.XPlus);
            Assert.Equal(before.X + teaching.StepDistance, motion.Position.X, 3);
            Assert.Equal(before.Y, motion.Position.Y);
            Assert.Equal(7, motion.Position.Z);

            await WaitUntilAsync(() => teaching.JogCommand.CanExecute(TeachingDirection.YPlus));
            var jog = teaching.JogCommand.ExecuteAsync(TeachingDirection.YPlus);
            try
            {
                await WaitUntilAsync(() => motion.Position.Y > before.Y);
                Assert.Equal(7, motion.Position.Z);
            }
            finally
            {
                teaching.JogStopCommand.Execute(null);
                await jog.WaitAsync(TimeSpan.FromSeconds(2));
            }
            Assert.False(motion.IsMoving);
            Assert.Equal(MachineAlarm.None, state.Alarm);
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task TeachingPlacementZAdjustsWithHandlerDownWhileXyAndMoveToStayBlocked()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.PcbPlacement);
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var placement = services.GetRequiredService<PcbPlacer>();
        var teaching = services.GetRequiredService<TeachingViewModel>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        teaching.SelectedTeachingUnit = HardwareArea.PcbPlacementHandler;
        try
        {
            await placement.SetLiftDownAsync(true);
            await WaitUntilAsync(() => teaching.StepCommand.CanExecute(TeachingDirection.ZPlus));
            Assert.Equal(StationCylinderState.Down, placement.Lift);
            foreach (var direction in new[] { TeachingDirection.XPlus, TeachingDirection.YPlus })
            {
                Assert.False(teaching.JogCommand.CanExecute(direction));
                Assert.False(teaching.StepCommand.CanExecute(direction));
            }
            Assert.False(teaching.MoveToHorizontalZCommand.CanExecute(null));
            foreach (var target in new[] { TeachingTarget.PlacementHandoff, TeachingTarget.PlacementReceiveZ })
            {
                teaching.SelectedPoint = teaching.FilteredPoints.Single(point => point.Position.Target == target);
                Assert.False(teaching.MoveToPointCommand.CanExecute(null));
            }

            var before = placement.Motion.Feedback.Position;
            await teaching.StepCommand.ExecuteAsync(TeachingDirection.ZPlus);
            Assert.Equal(before.Z + teaching.StepDistance, placement.Motion.Feedback.Position.Z, 6);
            await WaitUntilAsync(() => teaching.StepCommand.CanExecute(TeachingDirection.ZMinus));
            await teaching.StepCommand.ExecuteAsync(TeachingDirection.ZMinus);
            Assert.Equal(before, placement.Motion.Feedback.Position);

            await WaitUntilAsync(() => teaching.JogCommand.CanExecute(TeachingDirection.ZPlus));
            var jog = teaching.JogCommand.ExecuteAsync(TeachingDirection.ZPlus);
            try
            {
                await WaitUntilAsync(() => placement.Motion.Feedback.Position.Z > before.Z);
                Assert.Equal(MotionCommand.Adjustment, placement.Motion.Feedback.Command);
                Assert.Equal(StationCylinderState.Down, placement.Lift);
                Assert.Equal(MachineAlarm.None, state.Alarm);
            }
            finally
            {
                teaching.JogStopCommand.Execute(null);
                await jog.WaitAsync(TimeSpan.FromSeconds(2));
            }

            Assert.False(placement.Motion.Feedback.IsMoving);
            Assert.Equal(before.X, placement.Motion.Feedback.Position.X);
            Assert.Equal(before.Y, placement.Motion.Feedback.Position.Y);
            Assert.Equal(StationCylinderState.Down, placement.Lift);
            Assert.Equal(MachineAlarm.None, state.Alarm);
            await Assert.ThrowsAsync<MotionInterlockException>(
                () => placement.PrepareReceiptAsync());
            await placement.SetLiftDownAsync(false);
            await WaitUntilAsync(() => teaching.StepCommand.CanExecute(TeachingDirection.XPlus));
            Assert.True(teaching.MoveToHorizontalZCommand.CanExecute(null));
            Assert.True(teaching.MoveToPointCommand.CanExecute(null));
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TeachingHomeUsesSharedAxesAndCancelsTheWholeUnit(bool stopButton)
    {
        var settings = FlowSettings();
        settings.InspectionGantry.Motion.HorizontalHome.SearchSpeed = 1;
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var gantry = services.GetRequiredService<InspectionStation>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        io.SetInput(InputIo.Door1Open, false);
        Assert.True(machine.IsHomeAllowed);
        await machine.HomeAsync(CancellationToken.None);
        Assert.True(state.FeedbackReadiness.Homed);
        io.SetInput(InputIo.Door1Open, true);
        var teaching = services.GetRequiredService<TeachingViewModel>();
        teaching.SelectedTeachingUnit = HardwareArea.InspectionGantry;
        await gantry.MoveToAsync(new() { X = 10, Y = 7 }, 10_000);
        await WaitUntilAsync(() => teaching.HomeCommand.CanExecute(null));

        var home = teaching.HomeCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => gantry.Motion.Feedback.IsMoving);
        io.SetInput(InputIo.Door1Open, false);
        Assert.Equal(HomeBlockReason.None, teaching.HomeBlock);
        Assert.True(gantry.Motion.Feedback.IsMoving);
        if (stopButton)
            teaching.JogStopCommand.Execute(null);
        else
            teaching.SelectedTeachingUnit = HardwareArea.BoltFastening;
        await home.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(gantry.Motion.Feedback.IsMoving);
        Assert.False(state.IsHoming);
        Assert.False(gantry.Motion.Feedback.GetAxisState(MotionAxis.X).Homed);
        Assert.False(services.GetRequiredService<OperationCancellation>().HasActiveOperations);
        Assert.NotEqual((0, 0, 0), gantry.Motion.Feedback.Position);

        settings.InspectionGantry.Motion.HorizontalHome.SearchSpeed = 10_000;
        teaching.SelectedTeachingUnit = HardwareArea.InspectionGantry;
        await WaitUntilAsync(() => teaching.HomeCommand.CanExecute(null));
        await teaching.HomeCommand.ExecuteAsync(null);
        Assert.True(gantry.Motion.Feedback.GetAxisState(MotionAxis.X).Homed);
        Assert.True(gantry.Motion.Feedback.GetAxisState(MotionAxis.Y).Homed);
        Assert.Equal((0, 0, 0), gantry.Motion.Feedback.Position);

        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.NgCarrierPickupDown, true);
        var homeStarted = false;
        gantry.Motion.Feedback.MovingChanged += moving => homeStarted |= moving;
        await teaching.HomeCommand.ExecuteAsync(null);
        Assert.True(homeStarted);
        Assert.True(io.GetInput(InputIo.NgCarrierPickupUp));
        Assert.Equal((0, 0, 0), gantry.Motion.Feedback.Position);
        await WaitUntilAsync(() => teaching.HomeCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(HardwareArea.PcbSupply, MotionGroup.PcbSupply)]
    [InlineData(HardwareArea.PcbPlacementHandler, MotionGroup.PcbPlacementHandler)]
    [InlineData(HardwareArea.BoltFastening, MotionGroup.BoltFastening)]
    public async Task TeachingHomeCompletesZBeforeXYWithoutMovingToTravelHeight(
        HardwareArea unit,
        MotionGroup group)
    {
        var settings = FlowSettings();
        settings.PcbSupply.RotationZ = 8;
        settings.PcbPlacementHandler.HandoffPosition.Z = 8;
        settings.BoltFastening.SafeZ = 8;
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        await machine.InitializeAsync();
        var motion = services.GetRequiredKeyedService<IXyMotion>(group);
        await machine.HomeAsync(group, CancellationToken.None, MotionAxis.Z);
        await motion.MoveToXYAsync(10, 7, 10_000);
        await motion.MoveAxisAsync(MotionAxis.Z, 20, 10_000);
        var positions = new ConcurrentQueue<(double X, double Y, double Z, bool ZHomed)>();
        motion.PositionChanged += (x, y, z) =>
            positions.Enqueue((x, y, z, motion.GetAxisState(MotionAxis.Z).Homed));
        var outputs = new ConcurrentQueue<OutputIo>();
        services.GetRequiredService<VirtualIoService>().OutputChanged += (output, _) => outputs.Enqueue(output);
        var teaching = services.GetRequiredService<TeachingViewModel>();
        teaching.SelectedTeachingUnit = unit;
        await WaitUntilAsync(() => teaching.HomeCommand.CanExecute(null));

        await teaching.HomeCommand.ExecuteAsync(null);

        Assert.Empty(outputs);
        var samples = positions.ToArray();
        var firstHorizontal = Array.FindIndex(samples, position => position.X != 10 || position.Y != 7);
        Assert.True(firstHorizontal > 0);
        Assert.Contains(samples.Take(firstHorizontal), position => position.Z == 0);
        Assert.All(samples.Skip(firstHorizontal), position =>
        {
            Assert.True(position.ZHomed);
            Assert.Equal(0, position.Z);
        });
        Assert.Equal((0, 0, 0), motion.Position);
        Assert.All(motion.Axes, axis => Assert.True(motion.GetAxisState(axis).Homed));
        var otherGroup = group == MotionGroup.BoltFastening
            ? MotionGroup.PcbPlacementHandler
            : MotionGroup.BoltFastening;
        var otherMotion = services.GetRequiredKeyedService<IXyMotion>(otherGroup);
        Assert.All(otherMotion.Axes, axis => Assert.False(otherMotion.GetAxisState(axis).Homed));
        Assert.False(state.IsHoming);
        Assert.False(services.GetRequiredService<OperationCancellation>().HasActiveOperations);
    }

    [Fact]
    public async Task InspectionTeachingIncludesCarrierTransferPositionsAndIo()
    {
        await using var services = CreateDisplayServices(out var feedback);
        var transferSettings = services.GetRequiredService<NgCarrierTransferSettings>();
        var motionSettings = services.GetRequiredService<InspectionGantrySettings>().Motion;
        motionSettings.HorizontalSpeed = 1_234;
        transferSettings.CarrierPickupPosition = null;
        transferSettings.WaitingPosition = null;
        var machine = services.GetRequiredService<MachineController>();
        var gantry = services.GetRequiredService<InspectionStation>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        var teaching = services.GetRequiredService<TeachingViewModel>();
        var inspectionMotion = teaching.Motion;
        Assert.DoesNotContain(HardwareArea.NgCarrierTransfer, teaching.TeachingUnits);
        Assert.Contains(teaching.FilteredPoints, point => point.Position.Target == TeachingTarget.DataMatrix);

        teaching.SelectedTeachingUnit = HardwareArea.InspectionGantry;

        Assert.Same(inspectionMotion, teaching.Motion);
        Assert.Equal(MotionGroup.InspectionGantry, teaching.ActiveMotionGroup);
        var teachingTargets = new[]
        {
            TeachingTarget.InspectionWaiting, TeachingTarget.NgCarrierPickup, TeachingTarget.NgShuttlePlace,
        };
        Assert.Equal(
            teachingTargets.Order(),
            teaching.FilteredPoints.Where(point => point.Group == TeachingPointGroup.CarrierTransfer)
                .Select(point => point.Position.Target).Order());
        Assert.True(teaching.IsInspectionSelected);
        Assert.True(teaching.BoltPointEditorVisible);
        Assert.False(teaching.IsFasteningSelected);
        Assert.Contains(teaching.TeachingIoGroups, group => group.Area == HardwareArea.NgCarrierTransfer);
        Assert.Contains(teaching.TeachingIoGroups, group => group.Area == HardwareArea.NgShuttle);

        teaching.SelectedPoint = teaching.FilteredPoints.Single(
            point => point.Position.Target == TeachingTarget.NgCarrierPickup);
        Assert.False(teaching.SelectedPoint.Position.HasPosition);
        Assert.False(teaching.MoveToPointCommand.CanExecute(null));

        foreach (var target in teachingTargets)
        {
            var point = teaching.FilteredPoints.Single(point => point.Position.Target == target);
            teaching.SelectedPoint = point;
            Assert.Equal(TeachMode.XYOnly, point.Position.Mode);
            var x = (point.Coordinates?.X ?? 0) + 1;
            var y = (point.Coordinates?.Y ?? 0) + 2;
            await gantry.MoveToAsync(new() { X = x, Y = y }, 10_000);
            await WaitUntilAsync(() => teaching.TeachCurrentPositionCommand.CanExecute(null));
            await teaching.TeachCurrentPositionCommand.ExecuteAsync(null);
            var saved = services.GetRequiredService<MachineStore>().LoadSettings().Get<NgCarrierTransferSettings>();
            var position = point.Position.Target switch
            {
                TeachingTarget.NgCarrierPickup => saved.CarrierPickupPosition!,
                TeachingTarget.InspectionWaiting => saved.WaitingPosition!,
                _ => saved.ShuttlePlacePosition,
            };
            Assert.Equal(x, position.X);
            Assert.Equal(y, position.Y);

            await gantry.MoveToAsync(new() { X = x + 5, Y = y + 5 }, 10_000);
            await WaitUntilAsync(() => teaching.MoveToPointCommand.CanExecute(null));
            feedback.AxisMoves.Clear();
            await teaching.MoveToPointCommand.ExecuteAsync(null);
            Assert.Equal((x, y, 0), gantry.Motion.Feedback.Position);
            Assert.Empty(feedback.AxisMoves);
            Assert.Equal(motionSettings.HorizontalSpeed, feedback.LastMoveVelocity);
        }

        var beforeStep = gantry.Motion.Feedback.Position;
        teaching.StepDistance = 0.1;
        await WaitUntilAsync(() => teaching.StepCommand.CanExecute(TeachingDirection.XPlus));
        await teaching.StepCommand.ExecuteAsync(TeachingDirection.XPlus);
        Assert.Equal(motionSettings.HorizontalSpeed, feedback.LastMoveVelocity);
        Assert.Equal(beforeStep.X + 0.1, gantry.Motion.Feedback.Position.X, 6);
        Assert.Equal(beforeStep.Y, gantry.Motion.Feedback.Position.Y);

        var shuttle = TeachingRows(teaching)[OutputIo.NgShuttleDown];
        await WaitUntilAsync(() => shuttle.ToggleOutputCommand.CanExecute(null));
        await shuttle.ToggleOutputCommand.ExecuteAsync(null);
        Assert.True(io.GetInput(InputIo.NgShuttleDown));
        await shuttle.ToggleOutputCommand.ExecuteAsync(null);
        Assert.True(io.GetInput(InputIo.NgShuttleUp));

        teaching.SelectedPoint = teaching.FilteredPoints.Single(
            point => point.Position.Target == TeachingTarget.DataMatrix);
        Assert.Same(inspectionMotion, teaching.Motion);
        Assert.Contains(OutputIo.NgShuttleDown, TeachingRows(teaching).Keys);
        Assert.Contains(teaching.TeachingIoGroups, group => group.Area == HardwareArea.NgShuttle);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TeachingRechecksFeedbackBeforeJogOrSavingPosition(bool savePosition)
    {
        await using var services = CreateDisplayServices(out var feedback);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        var teaching = services.GetRequiredService<TeachingViewModel>();
        teaching.SelectedTeachingUnit = HardwareArea.InspectionGantry;
        teaching.SelectedPoint = teaching.FilteredPoints.Single(
            point => point.Position.Target == TeachingTarget.CarrierUpperLeftLocatingPin);
        await WaitUntilAsync(() => teaching.TeachCurrentPositionCommand.CanExecute(null));
        var point = teaching.SelectedPoint;
        Assert.True(state.Alarm == MachineAlarm.None, state.AlarmDetail);
        var before = (point.Coordinates?.X, point.Coordinates?.Y, point.Coordinates?.Z);
        if (savePosition)
            feedback.BeforePositionRead = () => throw new IOException("Teaching feedback read failed.");
        else
            feedback.BeforeRead = () => throw new IOException("Teaching feedback read failed.");

        if (savePosition)
            await teaching.TeachCurrentPositionCommand.ExecuteAsync(null);
        else
            await teaching.JogCommand.ExecuteAsync(TeachingDirection.XPlus);

        Assert.Equal(
            savePosition ? MachineAlarm.IoCommunication : MachineAlarm.Inspection,
            state.Alarm);
        Assert.Contains("Teaching feedback read failed.", state.AlarmDetail);
        Assert.Equal(before, (point.Coordinates?.X, point.Coordinates?.Y, point.Coordinates?.Z));
        Assert.False(teaching.Motion.IsMoving);
        Assert.False(services.GetRequiredService<OperationCancellation>().HasActiveOperations);
    }

    [Theory]
    [InlineData(HardwareArea.InspectionGantry, InputIo.NgCarrierPickupUp, MachineAlarm.Inspection)]
    [InlineData(HardwareArea.PcbPlacementHandler, InputIo.PcbPlacementHandlerUp, MachineAlarm.PcbPlacement)]
    public async Task TeachingJogReportsALateCylinderInterlockWithoutMoving(
        HardwareArea unit,
        InputIo raised,
        MachineAlarm expectedAlarm)
    {
        await using var services = CreateServices(FlowSettings());
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<VirtualIoService>();
        var state = services.GetRequiredService<MachineState>();
        var operations = services.GetRequiredService<OperationCancellation>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        if (unit == HardwareArea.PcbPlacementHandler)
            await services.GetRequiredService<PcbPlacer>().MoveAxisAsync(
                MotionAxis.Z, services.GetRequiredService<PcbPlacementHandlerSettings>().HandoffPosition.Z);
        var teaching = services.GetRequiredService<TeachingViewModel>();
        teaching.SelectedTeachingUnit = unit;
        await WaitUntilAsync(() => teaching.JogCommand.CanExecute(TeachingDirection.XPlus));
        var before = teaching.Motion.Feedback.Position;
        io.AutoResponseEnabled = false;
        void LoseCylinderFeedbackAfterAdmission()
        {
            if (!operations.HasActiveOperations)
                return;
            operations.ActivityChanged -= LoseCylinderFeedbackAfterAdmission;
            io.SetInput(raised, false);
        }

        operations.ActivityChanged += LoseCylinderFeedbackAfterAdmission;
        try
        {
            await teaching.JogCommand.ExecuteAsync(TeachingDirection.XPlus);
            Assert.Equal(expectedAlarm, state.Alarm);
            Assert.Contains(unit == HardwareArea.InspectionGantry ? "pickup" : "placement handler", state.AlarmDetail);
            Assert.Equal(before, teaching.Motion.Feedback.Position);
            Assert.False(teaching.Motion.Feedback.IsMoving);
            Assert.False(operations.HasActiveOperations);
        }
        finally
        {
            operations.ActivityChanged -= LoseCylinderFeedbackAfterAdmission;
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task TeachingJogKeepsExclusiveControlUntilStopped()
    {
        await using var services = CreateServices(FlowSettings());
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var operations = services.GetRequiredService<OperationCancellation>();
        var motion = services.GetRequiredService<InspectionStation>().Motion.Feedback;
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        var teaching = services.GetRequiredService<TeachingViewModel>();
        teaching.JogSpeed = 1;
        var jog = teaching.JogCommand.ExecuteAsync(TeachingDirection.XPlus);
        try
        {
            await WaitUntilAsync(() => motion.IsMoving);
            Assert.True(operations.HasActiveOperations);
            using var secondOperation = operations.TryBegin();

            Assert.Null(secondOperation);
            Assert.False(jog.IsCompleted);
            Assert.True(motion.IsMoving);
            Assert.Equal(MachineAlarm.None, state.Alarm);
        }
        finally
        {
            teaching.JogStopCommand.Execute(null);
            await jog.WaitAsync(TimeSpan.FromSeconds(2));
        }

        Assert.False(motion.IsMoving);
        Assert.False(operations.HasActiveOperations);
    }

    [Theory]
    [InlineData(TeachingStopAction.Stop)]
    [InlineData(TeachingStopAction.ChangeUnit)]
    [InlineData(TeachingStopAction.Close)]
    public async Task TeachingStopReportsDriverCancellationFailureAndDrainsJog(TeachingStopAction action)
    {
        await using var services = CreateServices(FlowSettings());
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        var teaching = services.GetRequiredService<TeachingViewModel>();
        teaching.SelectedTeachingUnit = HardwareArea.InspectionGantry;
        await WaitUntilAsync(() => teaching.JogCommand.CanExecute(TeachingDirection.XPlus));
        var motion = teaching.Motion.Feedback;
        // Simulate a native STOP failure in the cancellation callback without loading the SDK.
        var viewCancellation = (CancellationTokenSource)typeof(TeachingViewModel)
            .GetField("_viewCancellation", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(teaching)!;
        var viewToken = viewCancellation.Token;
        var stopError = new MotionException("Stop axes", new IOException("Axis STOP write failed."));
        using var registration = viewToken.Register(() => throw stopError);
        var jog = teaching.JogCommand.ExecuteAsync(TeachingDirection.XPlus);
        await WaitUntilAsync(() => motion.IsMoving);
        try
        {
            var failure = await Record.ExceptionAsync(async () =>
            {
                if (action == TeachingStopAction.Close)
                    await teaching.ShutdownAsync();
                else if (action == TeachingStopAction.ChangeUnit)
                    teaching.SelectedTeachingUnit = HardwareArea.PcbPlacementHandler;
                else
                    teaching.JogStopCommand.Execute(null);
            });
            await jog.WaitAsync(TimeSpan.FromSeconds(2));
            if (action == TeachingStopAction.Close)
            {
                var failures = Assert.IsType<AggregateException>(failure).Flatten().InnerExceptions;
                Assert.Contains(stopError, failures);
            }
            else
            {
                Assert.Null(failure);
                Assert.Equal(MachineAlarm.StopFailed, state.Alarm);
                Assert.Contains(stopError.ToString(), state.AlarmDetail);
            }
            Assert.False(motion.IsMoving);
            Assert.False(services.GetRequiredService<OperationCancellation>().HasActiveOperations);
        }
        finally
        {
            registration.Dispose();
            teaching.JogStopCommand.Execute(null);
            await jog;
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(TeachingDirection.XPlus, MotionAxis.X, 10.1, 20)]
    [InlineData(TeachingDirection.YPlus, MotionAxis.Y, 10, 20.1)]
    public async Task InspectionTeachingStepMovesOnlyTheSelectedAxis(
        TeachingDirection direction,
        MotionAxis expectedAxis,
        double x,
        double y)
    {
        await using var services = CreateDisplayServices(out var feedback);
        var machine = services.GetRequiredService<MachineController>();
        var gantry = services.GetRequiredService<InspectionStation>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        var teaching = services.GetRequiredService<TeachingViewModel>();
        await gantry.MoveToAsync(new() { X = 10, Y = 20 }, 10_000);
        teaching.StepDistance = 0.1;
        await WaitUntilAsync(() => teaching.StepCommand.CanExecute(direction));

        await teaching.StepCommand.ExecuteAsync(direction);

        Assert.Equal(expectedAxis, feedback.LastMovedAxis);
        Assert.Equal((x, y, 0), gantry.Motion.Feedback.Position);

        await services.GetRequiredService<IIoService>()
            .SetOutputAndWaitAsync(OutputIo.NgCarrierPickupDown, true);
        await WaitUntilAsync(() => !teaching.StepCommand.CanExecute(direction));
        await Assert.ThrowsAsync<MotionInterlockException>(
            () => gantry.MoveAxisAsync(MotionAxis.X, 30, 1_000));
        Assert.Equal((x, y, 0), gantry.Motion.Feedback.Position);
    }

    [Theory]
    [InlineData(MotionGroup.PcbSupply)]
    [InlineData(MotionGroup.PcbPlacementHandler)]
    [InlineData(MotionGroup.InspectionGantry)]
    public async Task TeachingJogStopsWhenTeachingContextChanges(MotionGroup group)
    {
        await using var services = CreateServices(FlowSettings());
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        if (group == MotionGroup.PcbPlacementHandler)
            await services.GetRequiredService<PcbPlacer>().MoveAxisAsync(
                MotionAxis.Z, services.GetRequiredService<PcbPlacementHandlerSettings>().HandoffPosition.Z);
        var teaching = services.GetRequiredService<TeachingViewModel>();
        teaching.SelectedTeachingUnit = group switch
        {
            MotionGroup.PcbSupply => HardwareArea.PcbSupply,
            MotionGroup.PcbPlacementHandler => HardwareArea.PcbPlacementHandler,
            _ => HardwareArea.InspectionGantry,
        };
        IMotionFeedback feedback = group switch
        {
            MotionGroup.PcbSupply => services.GetRequiredService<PcbSupplier>().Motion.Feedback,
            MotionGroup.PcbPlacementHandler => services.GetRequiredService<PcbPlacer>().Motion.Feedback,
            _ => services.GetRequiredService<InspectionStation>().Motion.Feedback,
        };

        teaching.JogSpeed = 1;
        Action[] stops = [
            () => teaching.JogStopCommand.Execute(null),
            () => teaching.SelectNextPointCommand.Execute(null),
            teaching.Deactivate,
            machine.Stop,
            () => io.SetInput(InputIo.AutoMode, false),
        ];
        foreach (var stop in stops)
        {
            await WaitUntilAsync(() => teaching.JogCommand.CanExecute(TeachingDirection.XPlus));
            var jog = teaching.JogCommand.ExecuteAsync(TeachingDirection.XPlus);
            try
            {
                Assert.True(feedback.IsMoving);
                Assert.False(jog.IsCompleted);
                stop();
                await jog.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.False(feedback.IsMoving);
                await WaitUntilAsync(
                    () => !services.GetRequiredService<OperationCancellation>().HasActiveOperations);
                Assert.Equal(MachineAlarm.None, services.GetRequiredService<MachineState>().Alarm);
            }
            finally
            {
                machine.Stop();
                await jog.WaitAsync(TimeSpan.FromSeconds(2));
            }
        }
    }

    [Theory]
    [InlineData(FasteningHead.Shooting, HeatSinkSlot.HeatSink1, 280, 410, 12)]
    [InlineData(FasteningHead.Pickup, HeatSinkSlot.HeatSink2, -30, 240, 16)]
    public async Task InspectionRecordedBoltKeepsIndependentFasteningXyAndZOffset(
        FasteningHead head, HeatSinkSlot heatSink, double expectedX, double expectedY, double expectedZ)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.Inspection);
        settings.Units.BoltFastening = true;
        settings.CarrierReference.UpperLeftLocatingPin = new() { X = 100, Y = 200 };
        settings.CarrierReference.LowerRightLocatingPin = new() { X = 140, Y = 200 };
        settings.BoltFastening.ShootingHead = new()
        {
            UpperLeftLocatingPin = new() { X = 300, Y = 400 },
            LowerRightLocatingPin = new() { X = 300, Y = 440 },
            FasteningZ = 12,
        };
        settings.BoltFastening.PickupHead = new()
        {
            UpperLeftLocatingPin = new() { X = -50, Y = 250 },
            LowerRightLocatingPin = new() { X = -50, Y = 210 },
            FasteningZ = 16,
        };
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        var teaching = services.GetRequiredService<TeachingViewModel>();
        teaching.SelectedTeachingUnit = HardwareArea.InspectionGantry;
        teaching.SelectedPcb = heatSink;
        teaching.NewFasteningHead = head;
        teaching.AddBoltPointCommand.Execute(null);
        var bolt = teaching.SelectedPoint!.Position.Bolt!;
        await services.GetRequiredService<InspectionStation>().MoveToAsync(new() { X = 110, Y = 220 });
        await WaitUntilAsync(() => teaching.TeachCurrentPositionCommand.CanExecute(null));
        await teaching.TeachCurrentPositionCommand.ExecuteAsync(null);
        Assert.Null(teaching.CameraError);
        Assert.Equal((110d, 220d), (bolt.X, bolt.Y));
        Assert.Equal(0, bolt.FasteningZOffset);

        teaching.SelectedTeachingUnit = HardwareArea.BoltFastening;

        var position = teaching.SelectedPoint!;
        Assert.Same(position, teaching.FilteredPoints[0]);
        Assert.Equal(TeachingTarget.BoltPosition, position.Position.Target);
        Assert.Same(bolt, position.Position.Bolt);
        Assert.True(position.Position.HasPosition);
        Assert.Equal((expectedX, expectedY, expectedZ), (position.Coordinates!.X, position.Coordinates.Y, position.Coordinates.Z));
        Assert.False(teaching.AddBoltPointCommand.CanExecute(null));
        Assert.False(teaching.RemoveBoltPointCommand.CanExecute(null));

        await WaitUntilAsync(() => teaching.MoveToPointCommand.CanExecute(null));
        await teaching.MoveToPointCommand.ExecuteAsync(null);
        var fastening = services.GetRequiredService<BoltFasteningStation>();
        Assert.Equal((expectedX, expectedY, expectedZ), fastening.Motion.Feedback.Position);

        expectedX += 0.25;
        expectedY -= 0.5;
        await fastening.MoveToXYAsync(expectedX, expectedY);
        position.FasteningZOffset = -0.75;
        await WaitUntilAsync(() => teaching.TeachCurrentPositionCommand.CanExecute(null));
        await teaching.TeachCurrentPositionCommand.ExecuteAsync(null);
        Assert.Equal((110d, 220d), (bolt.X, bolt.Y));
        Assert.Equal(expectedZ, settings.BoltFastening.GetHead(head).FasteningZ);
        expectedZ -= 0.75;
        Assert.Equal((expectedX, expectedY, expectedZ), (position.Coordinates!.X, position.Coordinates.Y, position.Coordinates.Z));
        await teaching.SaveCommand.ExecuteAsync(null);
        Assert.Null(teaching.SaveError);
        Assert.Null(teaching.RecipeEditor.Error);
        var recipeBefore = JsonSerializer.Serialize(teaching.Recipes.Current);

        await teaching.MoveToPointCommand.ExecuteAsync(null);
        Assert.Equal((expectedX, expectedY, expectedZ), fastening.Motion.Feedback.Position);
        await fastening.MoveToXYAsync(250, 390);
        await fastening.MoveToBoltAsync(bolt);
        Assert.Equal((expectedX, expectedY, expectedZ), fastening.Motion.Feedback.Position);

        teaching.SelectedPoint = teaching.FilteredPoints.Single(
            point => point.Position.Target == (head == FasteningHead.Shooting
                ? TeachingTarget.ShootingHeadUpperLeftLocatingPin : TeachingTarget.PickupHeadUpperLeftLocatingPin));
        teaching.SelectedPoint = position;
        teaching.SelectedTeachingUnit = HardwareArea.InspectionGantry;
        Assert.Same(bolt, teaching.SelectedPoint!.Position.Bolt);
        Assert.Equal(TeachingTarget.BoltReference, teaching.SelectedPoint.Position.Target);
        Assert.Equal((110d, 220d), (teaching.SelectedPoint.Coordinates!.X, teaching.SelectedPoint.Coordinates!.Y));

        var recipes = services.GetRequiredService<RecipeManager>();
        await recipes.LoadAsync(teaching.RecipeEditor.ActiveName);
        teaching.SelectedPcb = heatSink;
        teaching.SelectedTeachingUnit = HardwareArea.BoltFastening;
        var loaded = teaching.SelectedPoint!;
        Assert.Equal(TeachingTarget.BoltPosition, loaded.Position.Target);
        Assert.Equal(head, loaded.Position.Bolt!.Head);
        Assert.Equal((expectedX, expectedY, expectedZ), (loaded.Coordinates!.X, loaded.Coordinates.Y, loaded.Coordinates.Z));
        Assert.Equal(recipeBefore, JsonSerializer.Serialize(recipes.Current));

        teaching.SelectedTeachingUnit = HardwareArea.InspectionGantry;
        teaching.CarrierImages = await teaching.RecipeEditor.LoadCarrierImagesAsync();
        var previousImage = RecordedImage(teaching);
        settings.CarrierReference.UpperLeftLocatingPin = null;
        settings.CarrierReference.LowerRightLocatingPin = null;
        await teaching.Inspection.MoveToAsync(new() { X = 120, Y = 230 });
        await WaitUntilAsync(() => teaching.TeachCurrentPositionCommand.CanExecute(null));
        await teaching.TeachCurrentPositionCommand.ExecuteAsync(null);
        Assert.Null(teaching.CameraError);
        Assert.NotSame(previousImage, RecordedImage(teaching));
        var rerecorded = teaching.SelectedPoint!.Position.Bolt!;
        Assert.Equal((120d, 230d), (rerecorded.X, rerecorded.Y));
        Assert.Equal((expectedX, expectedY), (rerecorded.FasteningX, rerecorded.FasteningY));
        Assert.Equal(-0.75, rerecorded.FasteningZOffset);
        await recipes.LoadAsync(teaching.RecipeEditor.ActiveName);
        teaching.SelectedPcb = heatSink;
        teaching.SelectedTeachingUnit = HardwareArea.BoltFastening;
        var independent = teaching.SelectedPoint!;
        Assert.Equal((expectedX, expectedY, expectedZ), (independent.Coordinates!.X, independent.Coordinates.Y, independent.Coordinates.Z));
        await WaitUntilAsync(() => teaching.MoveToPointCommand.CanExecute(null));
        await teaching.MoveToPointCommand.ExecuteAsync(null);
        Assert.Equal((expectedX, expectedY, expectedZ), fastening.Motion.Feedback.Position);

        teaching.SelectedTeachingUnit = HardwareArea.InspectionGantry;
        await WaitUntilAsync(() => teaching.RemoveBoltPointCommand.CanExecute(null));
        teaching.RemoveBoltPointCommand.Execute(null);
        Assert.Empty(recipes.Current.Pcb.BoltPoints);
        teaching.SelectedTeachingUnit = HardwareArea.BoltFastening;
        Assert.DoesNotContain(teaching.FilteredPoints, point => point.Position.Target == TeachingTarget.BoltPosition);
        await machine.ShutdownAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GantryGrabSavesImageAndLightWithoutChangingPositionOrRoiOrStoppingLive(bool barcode)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.Inspection);
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        var teaching = services.GetRequiredService<TeachingViewModel>();
        if (barcode)
            teaching.SelectedPoint = teaching.FilteredPoints.Single(point => point.Position.Target == TeachingTarget.DataMatrix);
        else
            teaching.AddBoltPointCommand.Execute(null);
        Assert.False(teaching.GrabCommand.CanExecute(null));
        await teaching.Inspection.MoveToAsync(new() { X = 17, Y = 29 });
        await WaitUntilAsync(() => teaching.TeachCurrentPositionCommand.CanExecute(null));
        await teaching.TeachCurrentPositionCommand.ExecuteAsync(null);
        var original = RecordedImage(teaching)!;
        var editor = services.GetRequiredService<InspectionTeachingViewModel>();
        editor.SelectedRecipeName = teaching.RecipeEditor.ActiveName;
        await editor.LoadRecipeCommand.ExecuteAsync(null);
        var previousEditorImage = editor.Preview.Image;
        var region = new PixelRegion(2, 3, 10, 12);
        original.Metadata.Region = region;
        editor.SelectedPoint!.Metadata.Region = region;
        var bolt = teaching.SelectedPoint!.Position.Bolt;
        var originalBoltPosition = (bolt?.X, bolt?.Y);
        await teaching.Inspection.MoveToAsync(new() { X = 41, Y = 53 });
        var recipeBeforePreview = JsonSerializer.Serialize(teaching.Recipes.Current);
        teaching.LiveLightLevel = 67;
        Assert.Equal(recipeBeforePreview, JsonSerializer.Serialize(teaching.Recipes.Current));
        await teaching.ToggleLiveViewCommand.ExecuteAsync(null);

        await WaitUntilAsync(() => teaching.GrabCommand.CanExecute(null));
        await teaching.GrabCommand.ExecuteAsync(null);

        Assert.Null(teaching.CameraError);
        Assert.Null(teaching.RecipeEditor.Error);
        Assert.True(teaching.Inspection.IsLiveView);
        var captured = RecordedImage(teaching)!;
        Assert.NotSame(original.Image, captured.Image);
        Assert.Equal((17d, 29d), (captured.Position!.X, captured.Position!.Y));
        Assert.Equal(originalBoltPosition, (bolt?.X, bolt?.Y));
        Assert.Equal(region, captured.Metadata.Region);
        await teaching.ToggleLiveViewCommand.ExecuteAsync(null);
        Assert.Same(captured.Image, teaching.CameraImage);

        editor.Activate();
        await editor.RefreshImagesCommand.ExecutionTask!;
        Assert.Null(editor.Error);
        Assert.NotSame(previousEditorImage, editor.Preview.Image);
        var loaded = Assert.Single(editor.Points);
        Assert.Equal(region, loaded.Metadata.Region);
        Assert.Equal((17d, 29d), (loaded.Position!.X, loaded.Position!.Y));
        Assert.Equal(67, barcode ? editor.DataMatrix!.LightLevel : editor.SelectedPoint!.Bolt!.LightLevel);
        Assert.Equal(captured.Image.PixelWidth, loaded.Image.PixelWidth);
        var recordedPoint = teaching.SelectedPoint;
        teaching.LiveLightLevel = 99;
        teaching.SelectedPoint = teaching.FilteredPoints.Single(point => point.Position.Target == TeachingTarget.NgCarrierPickup);
        teaching.SelectedPoint = recordedPoint;
        Assert.Equal(67, teaching.LiveLightLevel);
        await machine.ShutdownAsync();
    }

    [Fact]
    public async Task FailedOrCancelledBoltRecordingKeepsCoordinatesAndImageTogether()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.Inspection);
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        var teaching = services.GetRequiredService<TeachingViewModel>();
        teaching.AddBoltPointCommand.Execute(null);
        await WaitUntilAsync(() => teaching.TeachCurrentPositionCommand.CanExecute(null));
        await teaching.TeachCurrentPositionCommand.ExecuteAsync(null);
        var original = RecordedImage(teaching)!;
        var recipeBefore = JsonSerializer.Serialize(teaching.Recipes.Current);
        var store = services.GetRequiredService<MachineStore>();
        var imageBefore = store.LoadRecipeImage(teaching.RecipeEditor.ActiveName, original.Metadata.Number);
        await services.GetRequiredService<InspectionStation>().MoveToAsync(new() { X = 17, Y = 29 });
        using var connection = new SqliteConnection($"Data Source={store.DatabaseFile}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TRIGGER FailBoltImage BEFORE INSERT ON RecipeImages BEGIN SELECT RAISE(ABORT, 'bolt image write failed'); END";
        command.ExecuteNonQuery();

        await teaching.TeachCurrentPositionCommand.ExecuteAsync(null);

        Assert.Contains("bolt image write failed", teaching.RecipeEditor.Error);
        Assert.Contains("bolt image write failed", teaching.CameraError);
        Assert.Same(original, RecordedImage(teaching));
        Assert.Equal(recipeBefore, JsonSerializer.Serialize(teaching.Recipes.Current));
        Assert.Equal(recipeBefore, JsonSerializer.Serialize(store.LoadRecipe(teaching.RecipeEditor.ActiveName)));
        Assert.Equal(imageBefore, store.LoadRecipeImage(teaching.RecipeEditor.ActiveName, original.Metadata.Number));
        command.CommandText = "DROP TRIGGER FailBoltImage";
        command.ExecuteNonQuery();

        var cancelled = false;
        void CancelRecording()
        {
            cancelled = true;
            teaching.TeachCurrentPositionCommand.Cancel();
        }
        teaching.Inspection.LiveViewChanged += CancelRecording;
        try
        {
            await teaching.TeachCurrentPositionCommand.ExecuteAsync(null);
        }
        finally
        {
            teaching.Inspection.LiveViewChanged -= CancelRecording;
        }
        Assert.True(cancelled);
        Assert.Same(original, RecordedImage(teaching));
        Assert.Equal(recipeBefore, JsonSerializer.Serialize(teaching.Recipes.Current));
        Assert.Equal(recipeBefore, JsonSerializer.Serialize(store.LoadRecipe(teaching.RecipeEditor.ActiveName)));
        Assert.Equal(imageBefore, store.LoadRecipeImage(teaching.RecipeEditor.ActiveName, original.Metadata.Number));
        await machine.ShutdownAsync();
    }

    [Fact]
    public async Task InspectionTeachingMovesToShuttleWithBothAxesTogether()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.Inspection);
        settings.NgCarrierTransfer.ShuttlePlacePosition = new() { X = 35, Y = 60 };
        await using var services = CreateDisplayServices(out var feedback, settings);
        var machine = services.GetRequiredService<MachineController>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        try
        {
            var gantry = services.GetRequiredService<InspectionStation>();
            var pickupPosition = settings.NgCarrierTransfer.CarrierPickupPosition!;
            var shuttlePosition = settings.NgCarrierTransfer.ShuttlePlacePosition;
            await gantry.MoveToAsync(pickupPosition);
            var teaching = services.GetRequiredService<TeachingViewModel>();
            teaching.SelectedTeachingUnit = HardwareArea.InspectionGantry;
            teaching.SelectedPoint = teaching.FilteredPoints.Single(
                point => point.Position.Target == TeachingTarget.NgShuttlePlace);
            await WaitUntilAsync(() => teaching.MoveToPointCommand.CanExecute(null));

            var axesMovedTogether = false;
            void ObserveShuttleMove(double x, double y, double z)
            {
                axesMovedTogether |= x > pickupPosition.X && x < shuttlePosition.X
                    && y > pickupPosition.Y && y < shuttlePosition.Y;
            }
            gantry.Motion.Feedback.PositionChanged += ObserveShuttleMove;
            try
            {
                await teaching.MoveToPointCommand.ExecuteAsync(null);
            }
            finally
            {
                gantry.Motion.Feedback.PositionChanged -= ObserveShuttleMove;
            }

            Assert.True(axesMovedTogether);
            Assert.Empty(feedback.AxisMoves);
            Assert.True(MotionService.IsAt(gantry.Motion.Feedback, shuttlePosition));
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InspectionMoveToUsesRecordedXyWithoutRequiringRoi(bool barcode)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.Inspection);
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        try
        {
            var recipe = services.GetRequiredService<RecipeManager>().Current;
            var bolt = new BoltPoint { Number = 1, HeatSink = HeatSinkSlot.HeatSink1, X = 12, Y = 9 };
            recipe.Pcb.BoltPoints.Add(bolt);
            recipe.CarrierImages.AddRange([
                new() { Number = 1, BoltNumber = 1, Region = new(0, 0, 20, 20) },
                new() { Number = 2, IsBarcode = true, Center = new() { X = 25, Y = 16 }, Region = new(0, 0, 20, 20) },
                new() { Number = 3, IsBarcode = true, HeatSink = HeatSinkSlot.HeatSink2, Center = new(), Region = new(0, 0, 20, 20) },
            ]);
            var fov = recipe.CarrierImages.Single(item => item.IsBarcode == barcode && item.HeatSink == HeatSinkSlot.HeatSink1);
            var teaching = services.GetRequiredService<TeachingViewModel>();
            var gantry = services.GetRequiredService<InspectionStation>();
            teaching.SelectedTeachingUnit = HardwareArea.InspectionGantry;
            teaching.SelectedPoint = teaching.FilteredPoints.Single(point =>
                barcode ? point.Position.Target == TeachingTarget.DataMatrix : point.Position.Bolt == bolt);

            foreach (var region in new PixelRegion?[] { null, new(9999, 0, 20, 20) })
            {
                fov.Region = region;
                var recipeBefore = JsonSerializer.Serialize(recipe);
                await gantry.MoveToAsync(new() { X = 1, Y = 2 });
                await WaitUntilAsync(() => teaching.MoveToPointCommand.CanExecute(null));
                Assert.Equal((recipe.GetInspectionPosition(fov).X, recipe.GetInspectionPosition(fov).Y), (teaching.SelectedPoint.Coordinates!.X, teaching.SelectedPoint.Coordinates!.Y));
                Assert.DoesNotContain("Not taught", teaching.SelectedPoint.PositionLabel);
                Assert.False(machine.TeachingReady);

                await teaching.MoveToPointCommand.ExecuteAsync(null);

                Assert.True(MotionService.IsAt(gantry.Motion.Feedback, recipe.GetInspectionPosition(fov)));
                Assert.Equal(recipeBefore, JsonSerializer.Serialize(recipe));
            }

            fov.Region = new(0, 0, 20, 20);
            Assert.True(machine.TeachingReady);

            await gantry.SetLiftUpAsync(false);
            Assert.False(teaching.MoveToPointCommand.CanExecute(null));
            await Assert.ThrowsAsync<MotionInterlockException>(() => barcode
                ? teaching.Inspection.MoveToBarcodeAsync(HeatSinkSlot.HeatSink1)
                : teaching.Inspection.MoveToBoltAsync(bolt));
            await gantry.SetLiftUpAsync(true);

            recipe.CarrierImages.Remove(fov);
            Assert.False(teaching.SelectedPoint.Position.HasPosition);
            Assert.False(teaching.MoveToPointCommand.CanExecute(null));
            await Assert.ThrowsAsync<InvalidOperationException>(() => barcode
                ? teaching.Inspection.MoveToBarcodeAsync(HeatSinkSlot.HeatSink1)
                : teaching.Inspection.MoveToBoltAsync(bolt));
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task LoadedRecipeIsVisibleToExistingConsumers()
    {
        await using var services = CreateServices(FlowSettings());
        var recipes = services.GetRequiredService<RecipeManager>();
        _ = services.GetRequiredService<BoltFasteningStation>();
        var legacyBolt = JsonSerializer.Deserialize<BoltPoint>("{\"Number\":1,\"X\":15,\"Y\":25}")!;
        Assert.Null(legacyBolt.FasteningX);
        Assert.Null(legacyBolt.FasteningY);
        Assert.Equal(0, legacyBolt.FasteningZOffset);
        var inspector = services.GetRequiredService<InspectionStation>();
        var editor = services.GetRequiredService<RecipeEditor>();
        var preview = new InspectionPreview(recipes.Current);
        var frame = new ImageFrame(1, 1, 3, [160, 160, 160]);
        var region = new PixelRegion(0, 0, 1, 1);
        recipes.Current.BoltInspection.BrightnessThreshold = 128;
        preview.Clear(bolt: new());
        preview.SetSavedImage(InspectionPreview.CreateBitmap(frame), region);
        await preview.InspectAsync(CancellationToken.None);
        Assert.StartsWith("OK", preview.Result);
        Assert.False(inspector.HasBarcodeRegion(HeatSinkSlot.HeatSink1));
        var saved = new Recipe
        {
            Name = "Other",
            BoltInspection = new() { BrightnessThreshold = 200 },
            Pcb = new() { BoltPoints = [legacyBolt] },
            CarrierImages = [new() { Number = 1, IsBarcode = true, Center = new(), Region = region }],
        };
        services.GetRequiredService<MachineStore>().SaveRecipe(saved);

        await recipes.LoadAsync(saved.Name);

        Assert.Equal("Other", editor.ActiveName);
        Assert.Equal("Other", editor.Name);
        Assert.Equal(200, preview.BrightnessThreshold);
        await preview.InspectAsync(CancellationToken.None);
        Assert.StartsWith("NG", preview.Result);
        Assert.True(inspector.HasBarcodeRegion(HeatSinkSlot.HeatSink1));
        Assert.Same(recipes.Current.CarrierImages[0], inspector.GetBarcodeFov(HeatSinkSlot.HeatSink1));
        var loadedBolt = Assert.Single(recipes.Current.Pcb.BoltPoints);
        Assert.Equal((15d, 25d), (loadedBolt.FasteningX, loadedBolt.FasteningY));
        Assert.Equal(0, loadedBolt.FasteningZOffset);
        loadedBolt.FasteningX = 17;
        loadedBolt.FasteningZOffset = 0.5;
        await recipes.SaveAsync(saved.Name);
        await recipes.LoadAsync(saved.Name);
        loadedBolt = Assert.Single(recipes.Current.Pcb.BoltPoints);
        Assert.Equal((17d, 25d), (loadedBolt.FasteningX, loadedBolt.FasteningY));
        Assert.Equal(0.5, loadedBolt.FasteningZOffset);
    }

    [Fact]
    public async Task TeachingUnitSelectionOwnsHandoffAxesAndCancelsJog()
    {
        await using var services = CreateServices(FlowSettings());
        var machine = services.GetRequiredService<MachineController>();
        var teaching = services.GetRequiredService<TeachingViewModel>();
        var supply = services.GetRequiredService<PcbSupplier>();
        var placement = services.GetRequiredService<PcbPlacer>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        await WaitUntilAsync(() => services.GetRequiredService<MachineState>().FeedbackReadiness.Homed);
        Assert.True(services.GetRequiredService<MachineState>().FeedbackReadiness.Homed,
            services.GetRequiredService<MachineState>().AlarmDetail);
        teaching.SelectedTeachingUnit = HardwareArea.PcbSupply;
        Assert.All(teaching.FilteredPoints, point => Assert.Equal(MotionGroup.PcbSupply, point.Position.MotionGroup));
        teaching.JogSpeed = 1;
        await WaitUntilAsync(() => teaching.JogCommand.CanExecute(TeachingDirection.XPlus));
        var jog = teaching.JogCommand.ExecuteAsync(TeachingDirection.XPlus);
        Assert.True(supply.Motion.Feedback.IsMoving);

        teaching.SelectedPoint = teaching.FilteredPoints.Single(
            point => point.Position.Target == TeachingTarget.SupplyHandoff);
        await jog.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(supply.Motion.Feedback.IsMoving);
        Assert.Equal(MotionGroup.PcbSupply, teaching.ActiveMotionGroup);
        Assert.Same(supply.Motion.Feedback, teaching.Motion.Feedback);
        Assert.Contains(OutputIo.PcbSupplyGripperClosed, TeachingRows(teaching).Keys);

        await WaitUntilAsync(() => teaching.JogCommand.CanExecute(TeachingDirection.XPlus));
        jog = teaching.JogCommand.ExecuteAsync(TeachingDirection.XPlus);
        Assert.True(supply.Motion.Feedback.IsMoving);
        teaching.SelectedTeachingUnit = HardwareArea.PcbPlacementHandler;
        await jog.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(supply.Motion.Feedback.IsMoving);
        Assert.False(placement.Motion.Feedback.IsMoving);
        Assert.Equal(MotionGroup.PcbPlacementHandler, teaching.ActiveMotionGroup);
        Assert.All(teaching.FilteredPoints, point => Assert.Equal(MotionGroup.PcbPlacementHandler, point.Position.MotionGroup));
        teaching.SelectedPoint = teaching.FilteredPoints.Single(
            point => point.Position.Target == TeachingTarget.PlacementHandoff);
        Assert.Equal(HardwareArea.PcbPlacementHandler, teaching.SelectedTeachingUnit);
        Assert.Same(placement.Motion.Feedback, teaching.Motion.Feedback);
        Assert.DoesNotContain(OutputIo.Unused3, TeachingRows(teaching).Keys);
        Assert.DoesNotContain(OutputIo.PcbSupplyGripperClosed, TeachingRows(teaching).Keys);

        teaching.SelectedTeachingUnit = HardwareArea.InspectionGantry;
        Assert.True(teaching.IsInspectionSelected);
        Assert.Equal(MotionGroup.InspectionGantry, teaching.ActiveMotionGroup);
        Assert.False(services.GetRequiredService<OperationCancellation>().HasActiveOperations);
    }

    [Fact]
    public async Task PlacementTeachingRecordsStandbyAndSavesReceiveZSeparately()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.PcbPlacement);
        settings.PcbPlacementHandler.ReceiveZ = null;
        await using var services = CreateMotionScopeServices(settings, out var probes);
        var machine = services.GetRequiredService<MachineController>();
        var teaching = services.GetRequiredService<TeachingViewModel>();
        var placement = services.GetRequiredService<PcbPlacer>();
        await machine.InitializeAsync();
        var probe = probes[MotionGroup.PcbPlacementHandler];
        teaching.SelectedTeachingUnit = HardwareArea.PcbPlacementHandler;
        Assert.DoesNotContain(teaching.FilteredPoints, point => point.Position.Target == TeachingTarget.SafeZ);
        var handoff = teaching.FilteredPoints.Single(point => point.Position.Target == TeachingTarget.PlacementHandoff);
        teaching.SelectedPoint = handoff;
        await Assert.ThrowsAsync<MotionInterlockException>(() => placement.MoveAxisAsync(MotionAxis.Z, 7));
        await WaitUntilAsync(() => teaching.State.SetupEditingEnabled && teaching.Motion.Axes[MotionAxis.Z].State is not null);
        var originalZ = settings.PcbPlacementHandler.HandoffPosition.Z;
        try
        {
            Assert.False(teaching.TeachCurrentPositionCommand.CanExecute(null));
            await teaching.TeachCurrentPositionCommand.ExecuteAsync(null);
            Assert.Equal(originalZ, settings.PcbPlacementHandler.HandoffPosition.Z);

            // Standby XYZ requires all three axes; Receive Z only needs Z.
            Assert.True(await placement.HomeAxisAsync(MotionAxis.Z));
            Assert.False(placement.Motion.Feedback.GetAxisState(MotionAxis.X).Homed);
            await placement.MoveAxisAsync(MotionAxis.Z, 7);
            Assert.False(teaching.TeachCurrentPositionCommand.CanExecute(null));
            await teaching.TeachCurrentPositionCommand.ExecuteAsync(null);
            Assert.Equal(originalZ, handoff.Coordinates!.Z);

            var receive = teaching.FilteredPoints.Single(point => point.Position.Target == TeachingTarget.PlacementReceiveZ);
            teaching.SelectedPoint = receive;
            Assert.False(receive.Position.HasPosition);
            await WaitUntilAsync(() => teaching.TeachCurrentPositionCommand.CanExecute(null));
            await teaching.TeachCurrentPositionCommand.ExecuteAsync(null);
            Assert.Equal(7, settings.PcbPlacementHandler.ReceiveZ);
            Assert.Equal(7, services.GetRequiredService<MachineStore>().LoadSettings()
                .Get<PcbPlacementHandlerSettings>().ReceiveZ);
            Assert.Equal(originalZ, settings.PcbPlacementHandler.HandoffPosition.Z);
            teaching.SelectedPoint = handoff;

            await placement.MoveAxisAsync(MotionAxis.Z, settings.PcbPlacementHandler.HandoffPosition.Z);
            Assert.True(await placement.HomeHorizontalAsync());
            await placement.MoveAxisAsync(MotionAxis.Z, 7);
            await WaitUntilAsync(() => teaching.TeachCurrentPositionCommand.CanExecute(null));
            await teaching.TeachCurrentPositionCommand.ExecuteAsync(null);
            Assert.Equal(7, handoff.Coordinates!.Z);
            Assert.Equal(7, settings.PcbPlacementHandler.HandoffPosition.Z);
            Assert.Equal(originalZ, services.GetRequiredService<MachineStore>().LoadSettings()
                .Get<PcbPlacementHandlerSettings>().HandoffPosition.Z);
            await teaching.SaveCommand.ExecuteAsync(null);
            Assert.Equal(7, settings.PcbPlacementHandler.HandoffPosition.Z);
            Assert.True(placement.IsAtHorizontalZ);
            Assert.Null(teaching.SaveError);

            await placement.MoveAxisAsync(MotionAxis.Z, 9);
            await WaitUntilAsync(() => teaching.TeachCurrentPositionCommand.CanExecute(null));
            probe.OverrideState = state => state with { Homed = false };
            await teaching.TeachCurrentPositionCommand.ExecuteAsync(null);

            Assert.Equal(7, handoff.Coordinates!.Z);
            Assert.Equal(7, settings.PcbPlacementHandler.HandoffPosition.Z);
            Assert.Equal(7, services.GetRequiredService<MachineStore>().LoadSettings()
                .Get<PcbPlacementHandlerSettings>().HandoffPosition.Z);
            Assert.Contains("Home", teaching.SaveError);
        }
        finally
        {
            probe.OverrideState = null;
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task TeachingOutputsWaitForFeedbackAndCancelWithoutReversingPneumatics()
    {
        await using var services = CreateServices(FlowSettings());
        var machine = services.GetRequiredService<MachineController>();
        var teaching = services.GetRequiredService<TeachingViewModel>();
        teaching.SelectedTeachingUnit = HardwareArea.PcbSupply;
        var io = services.GetRequiredService<VirtualIoService>();
        var state = services.GetRequiredService<MachineState>();
        var gripper = TeachingRows(teaching)[OutputIo.PcbSupplyGripperClosed];
        var signals = services.GetRequiredService<IoSignals>();
        var group = Assert.Single(teaching.TeachingIoGroups);
        Assert.All(group.Outputs, row => Assert.Same(signals.Outputs[row.Io.Signal], row.Io));
        Assert.All(group.Sensors, row => Assert.Same(signals.Inputs[row.Signal], row));
        Assert.All(group.Outputs.SelectMany(row => row.Io.Feedback),
            row => Assert.DoesNotContain(row, group.Sensors));
        await WaitUntilAsync(() => !gripper.ToggleOutputCommand.CanExecute(null));
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        var handler = services.GetRequiredService<PcbSupplier>();
        Assert.True(state.FeedbackReadiness.Homed, state.AlarmDetail);
        var rotation = TeachingRows(teaching)[OutputIo.PcbSupplyRotate];
        var wasRotated = io.GetOutput(OutputIo.PcbSupplyRotate);
        await handler.MoveAxisAsync(MotionAxis.Z, 5);
        await WaitUntilAsync(() => rotation.ToggleOutputCommand.CanExecute(null));
        await rotation.ToggleOutputCommand.ExecuteAsync(null);
        Assert.Equal(0, handler.Motion.Feedback.Position.Z);
        Assert.Equal(wasRotated ? PcbSupplyRotationState.Unrotated : PcbSupplyRotationState.Rotated, handler.Rotation);
        await rotation.ToggleOutputCommand.ExecuteAsync(null);
        await handler.MoveAxisAsync(MotionAxis.X, 80);
        await WaitUntilAsync(() => rotation.ToggleOutputCommand.CanExecute(null));
        await WaitUntilAsync(() => teaching.StepCommand.CanExecute(TeachingDirection.ZPlus));
        await handler.MoveAxisAsync(MotionAxis.X, 0);
        io.AutoResponseEnabled = false;
        await WaitUntilAsync(() => gripper.ToggleOutputCommand.CanExecute(null));

        var pending = gripper.ToggleOutputCommand.ExecuteAsync(null);
        Assert.True(io.GetOutput(gripper.Io.Signal));
        Assert.False(pending.IsCompleted);
        await WaitUntilAsync(() => !gripper.ToggleOutputCommand.CanExecute(null));
        Assert.True(state.IsRunning); // The feedback wait owns a machine operation.
        var feedback = io.GetOutputFeedback(gripper.Io.Signal)!;
        io.SetInput(feedback.OnInput, true);
        Assert.False(pending.IsCompleted); // Both inputs ON is not completion.
        io.SetInput(feedback.OffInput!.Value, false);
        await pending.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => gripper.ToggleOutputCommand.CanExecute(null));

        var beforeSelection = handler.Motion.Feedback.Position;
        var releasing = gripper.ToggleOutputCommand.ExecuteAsync(null);
        Assert.False(releasing.IsCompleted);
        teaching.SelectedTeachingUnit = HardwareArea.PcbPlacementHandler;
        await releasing.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(MotionGroup.PcbPlacementHandler, teaching.SelectedPoint!.Position.MotionGroup);
        Assert.Equal(beforeSelection, handler.Motion.Feedback.Position);
        Assert.False(io.GetOutput(gripper.Io.Signal));
        Assert.True(io.GetInput(feedback.OnInput));
        Assert.Equal(MachineAlarm.None, state.Alarm);
        await WaitUntilAsync(() => !gripper.ToggleOutputCommand.CanExecute(null));

        teaching.SelectedTeachingUnit = HardwareArea.PcbSupply;
        Assert.Same(gripper, TeachingRows(teaching)[OutputIo.PcbSupplyGripperClosed]);
        await WaitUntilAsync(() => gripper.ToggleOutputCommand.CanExecute(null));
        teaching.SelectedTeachingUnit = HardwareArea.PcbPlacementHandler;
        var lift = TeachingRows(teaching)[OutputIo.PcbPlacementHandlerDown];
        var lowering = lift.ToggleOutputCommand.ExecuteAsync(null);
        await teaching.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(2));
        await lowering;
        Assert.True(io.GetOutput(lift.Io.Signal));
        Assert.False(services.GetRequiredService<OperationCancellation>().HasActiveOperations);
        Assert.Equal(MachineAlarm.None, state.Alarm);
    }

    [Fact]
    public async Task PlacementRotationStaysOffInInitializationTeachingAndOutputControl()
    {
        await using var services = CreateServices(FlowSettings());
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<VirtualIoService>();
        io.SetOutput(OutputIo.PcbPlacementHandlerRotate, true);
        await machine.InitializeAsync();
        try
        {
            Assert.False(io.GetOutput(OutputIo.PcbPlacementHandlerRotate));
            var teaching = services.GetRequiredService<TeachingViewModel>();
            teaching.SelectedTeachingUnit = HardwareArea.PcbPlacementHandler;
            Assert.DoesNotContain(teaching.TeachingIoGroups.SelectMany(group => group.Outputs),
                row => row.Io.Signal == OutputIo.PcbPlacementHandlerRotate);
            Assert.DoesNotContain(teaching.TeachingIoGroups.SelectMany(group => group.Sensors),
                row => row.Signal is InputIo.PcbPlacementHandlerRotated or InputIo.PcbPlacementHandlerUnrotated);
            var rotation = services.GetRequiredService<IoSignals>().Outputs[OutputIo.PcbPlacementHandlerRotate];
            Assert.False(machine.IsSetTeachingOutputAllowed(rotation));
            await machine.ToggleTeachingOutputAsync(rotation, CancellationToken.None, CancellationToken.None);
            Assert.False(io.GetOutput(OutputIo.PcbPlacementHandlerRotate));
            var output = new OutputWindowRow(
                services.GetRequiredService<IoSignals>().Outputs[OutputIo.PcbPlacementHandlerRotate], machine);
            Assert.False(output.ToggleCommand.CanExecute(null));
            Assert.Equal(OutputBlockReason.None, machine.ToggleDiagnosticOutput(OutputIo.PcbPlacementHandlerRotate));
            Assert.False(io.GetOutput(OutputIo.PcbPlacementHandlerRotate));
            io.SetOutput(OutputIo.PcbPlacementHandlerRotate, true);
            services.GetRequiredService<MachineState>().SetError(MachineAlarm.PcbPlacement, new InvalidOperationException("Reset fixed output"));
            await machine.ResetAsync();
            Assert.False(io.GetOutput(OutputIo.PcbPlacementHandlerRotate));
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task TeachingPickupTableTogglesBothDirectionsAndWaitsForFeedback()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.BoltFastening);
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var station = services.GetRequiredService<BoltFasteningStation>();
        await machine.InitializeAsync();
        var teaching = services.GetRequiredService<TeachingViewModel>();
        teaching.SelectedTeachingUnit = HardwareArea.BoltFastening;
        var table = Assert.Single(teaching.TeachingIoGroups.SelectMany(group => group.Outputs),
            row => row.Io.Signal == OutputIo.PickupTableDown);
        var position = station.Motion.Feedback.Position;
        try
        {
            await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.PickupTableDown, false);
            await WaitUntilAsync(() => table.ToggleOutputCommand.CanExecute(null));
            await table.ToggleOutputCommand.ExecuteAsync(null);
            Assert.True(io.GetOutput(OutputIo.PickupTableDown));
            Assert.Equal(StationCylinderState.Down, station.PickupTablePosition);

            io.AutoResponseEnabled = false;
            await WaitUntilAsync(() => table.ToggleOutputCommand.CanExecute(null));
            var raising = table.ToggleOutputCommand.ExecuteAsync(null);
            Assert.False(io.GetOutput(OutputIo.PickupTableDown));
            Assert.False(raising.IsCompleted);
            Assert.True(state.IsRunning);
            io.SetInput(InputIo.PickupTableUp, true);
            Assert.False(raising.IsCompleted);
            io.SetInput(InputIo.PickupTableDown, false);
            await raising.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(StationCylinderState.Up, station.PickupTablePosition);

            await WaitUntilAsync(() => table.ToggleOutputCommand.CanExecute(null));
            var lowering = table.ToggleOutputCommand.ExecuteAsync(null);
            Assert.False(lowering.IsCompleted);
            teaching.JogStopCommand.Execute(null);
            await lowering.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(io.GetOutput(OutputIo.PickupTableDown));
            Assert.Equal(position, station.Motion.Feedback.Position);
            Assert.Equal(MachineAlarm.None, state.Alarm);
            Assert.False(services.GetRequiredService<OperationCancellation>().HasActiveOperations);
        }
        finally
        {
            await teaching.ShutdownAsync();
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task TeachingOutputsKeepOwnerMovementRulesAndReportFeedbackTimeout()
    {
        var settings = FlowSettings();
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<VirtualIoService>();
        var state = services.GetRequiredService<MachineState>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        // HOME ends at the origin; this test explicitly prepares the horizontal travel height.
        await services.GetRequiredService<PcbPlacer>().MoveAxisAsync(
            MotionAxis.Z, services.GetRequiredService<PcbPlacementHandlerSettings>().HandoffPosition.Z);
        var teaching = services.GetRequiredService<TeachingViewModel>();
        Assert.True(state.Ready, state.AlarmDetail);
        teaching.SelectedTeachingUnit = HardwareArea.PcbPlacementHandler;
        var lift = TeachingRows(teaching)[OutputIo.PcbPlacementHandlerDown];
        await lift.ToggleOutputCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => !teaching.StepCommand.CanExecute(TeachingDirection.XPlus));
        Assert.DoesNotContain(OutputIo.PcbPlacementHandlerRotate, TeachingRows(teaching).Keys);
        await lift.ToggleOutputCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => teaching.StepCommand.CanExecute(TeachingDirection.XPlus));
        var ipm = TeachingRows(teaching)[OutputIo.PcbPlacementIpmDown];
        await ipm.ToggleOutputCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => teaching.StepCommand.CanExecute(TeachingDirection.XPlus));

        teaching.SelectedTeachingUnit = HardwareArea.BoltFastening;
        Assert.Contains(OutputIo.ShootBolt, TeachingRows(teaching).Keys);
        Assert.DoesNotContain(OutputIo.ShootingEscapeForward, TeachingRows(teaching).Keys);
        var directStart = teaching.TeachingIoGroups.SelectMany(group => group.Outputs)
            .Single(row => row.Io.Signal == OutputIo.PickupBoltStart);
        Assert.False(directStart.IsSupported);
        Assert.False(directStart.ToggleOutputCommand.CanExecute(null));
        await directStart.ToggleOutputCommand.ExecuteAsync(null);
        Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
        foreach (var output in new[] { OutputIo.PickupHeadDown, OutputIo.ShootingHeadDown })
        {
            var head = TeachingRows(teaching)[output];
            await head.ToggleOutputCommand.ExecuteAsync(null);
            Assert.True(io.GetOutput(output));
            Assert.False(services.GetRequiredService<BoltFasteningStation>().IsHorizontalMoveAllowed);
            await WaitUntilAsync(() => teaching.StepCommand.CanExecute(TeachingDirection.XPlus));
            await head.ToggleOutputCommand.ExecuteAsync(null);
            Assert.False(io.GetOutput(output));
            Assert.True(services.GetRequiredService<BoltFasteningStation>().IsHorizontalMoveAllowed);
        }

        teaching.SelectedTeachingUnit = HardwareArea.InspectionGantry;
        var ngGripper = TeachingRows(teaching)[OutputIo.NgCarrierGripperClose];
        await ngGripper.ToggleOutputCommand.ExecuteAsync(null);
        Assert.True(io.GetOutput(ngGripper.Io.Signal));
        Assert.True(io.GetInput(InputIo.NgCarrierGripperClosed));
        await ngGripper.ToggleOutputCommand.ExecuteAsync(null);
        Assert.False(io.GetOutput(ngGripper.Io.Signal));
        Assert.True(io.GetInput(InputIo.NgCarrierGripperOpen));

        var ngLift = TeachingRows(teaching)[OutputIo.NgCarrierPickupDown];
        settings.Units.Inspection = false;
        await WaitUntilAsync(() => ngLift.ToggleOutputCommand.CanExecute(null));
        settings.Units.Inspection = true;
        io.AutoResponseEnabled = false;
        var pending = ngLift.ToggleOutputCommand.ExecuteAsync(null);
        teaching.JogStopCommand.Execute(null);
        await pending.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(MachineAlarm.None, state.Alarm);
        Assert.True(io.GetOutput(ngLift.Io.Signal));

        settings.Options.TimeoutMilliseconds = 50;
        io.SetOutput(ngLift.Io.Signal, false); // External output change; toggle must read the current DO.
        await ngLift.ToggleOutputCommand.ExecuteAsync(null);
        Assert.Equal(MachineAlarm.NgCarrierTransfer, state.Alarm);
    }

    [Fact]
    public async Task TeachingReportsMotionAndHomeBlocks()
    {
        var settings = FlowSettings();
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<VirtualIoService>();
        var teaching = services.GetRequiredService<TeachingViewModel>();
        teaching.SelectedTeachingUnit = HardwareArea.PcbPlacementHandler;
        await machine.InitializeAsync();
        await WaitUntilAsync(() => teaching.MotionHint == TeachingMotionHint.HomeRequired);
        Assert.False(teaching.JogCommand.CanExecute(TeachingDirection.XPlus));
        Assert.True(teaching.HomeCommand.CanExecute(null));

        io.SetInput(InputIo.Door1Open, false);
        Assert.True(services.GetRequiredService<MachineState>().DoorInterlockReady);
        Assert.Equal(HomeBlockReason.None, teaching.HomeBlock);
        Assert.True(teaching.HomeCommand.CanExecute(null));
        io.SetInput(InputIo.Door1Open, true);
        Assert.Equal(HomeBlockReason.None, teaching.HomeBlock);

        var motion = services.GetRequiredKeyedService<IXyMotion>(MotionGroup.PcbPlacementHandler);
        motion.SetServo(MotionAxis.Z, false);
        await WaitUntilAsync(() => teaching.MotionHint == TeachingMotionHint.ServoOff);
        Assert.False(teaching.HomeCommand.CanExecute(null));

        settings.Units.PcbPlacement = false;
        Assert.Equal(HomeBlockReason.UnitDisabled, teaching.HomeBlock);
        Assert.Equal(TeachingMotionHint.UnitDisabled, teaching.MotionHint);
        teaching.SelectedTeachingUnit = HardwareArea.InspectionGantry;
        Assert.Equal(HomeBlockReason.None, teaching.HomeBlock);
        Assert.Equal(TeachingMotionHint.None, teaching.MotionHint);
        Assert.True(teaching.HomeCommand.CanExecute(null));
    }

    [Fact]
    public async Task TeachingControlsOnlyItsOwnStopper()
    {
        await using var services = CreateServices(FlowSettings());
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<VirtualIoService>();
        var teaching = services.GetRequiredService<TeachingViewModel>();
        await machine.InitializeAsync();
        (HardwareArea Unit, OutputIo Output, InputIo Down, InputIo Up)[] stoppers = [
            (HardwareArea.PcbPlacementHandler, OutputIo.PcbPlacementStopperUp,
                InputIo.PcbPlacementStopperDown, InputIo.PcbPlacementStopperUp),
            (HardwareArea.BoltFastening, OutputIo.BoltFasteningStopperUp,
                InputIo.BoltFasteningStopperDown, InputIo.BoltFasteningStopperUp),
            (HardwareArea.InspectionGantry, OutputIo.InspectionStopperUp,
                InputIo.InspectionStopperDown, InputIo.InspectionStopperUp),
        ];
        var changed = new ConcurrentQueue<OutputIo>();
        io.OutputChanged += (signal, _) => changed.Enqueue(signal);

        foreach (var (unit, output, down, up) in stoppers)
        {
            teaching.SelectedTeachingUnit = unit;
            var stopper = TeachingRows(teaching)[output];
            Assert.Contains(teaching.TeachingIoGroups.SelectMany(group => group.Outputs),
                row => row == stopper);
            await WaitUntilAsync(() => stopper.ToggleOutputCommand.CanExecute(null));
            await stopper.ToggleOutputCommand.ExecuteAsync(null);
            Assert.False(io.GetInput(down));
            Assert.True(io.GetInput(up));
            await stopper.ToggleOutputCommand.ExecuteAsync(null);
            Assert.True(io.GetInput(down));
            Assert.False(io.GetInput(up));
            Assert.All(changed, signal => Assert.Equal(output, signal));
            changed.Clear();
        }
    }

    [Fact]
    public async Task TeachingAllowsMovementAndHomeWithBothHandlersAtHandoff()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.PcbSupply);
        settings.Units.PcbPlacement = true;
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var teaching = services.GetRequiredService<TeachingViewModel>();
        var supply = services.GetRequiredService<PcbSupplier>();
        var placement = services.GetRequiredService<PcbPlacer>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        try
        {
            io.SetInput(InputIo.PcbSupplyPcbDetected, true);
            Assert.True(machine.IsHomeAllowed);
            await machine.HomeAsync(CancellationToken.None);
            await supply.PrepareHandoffAsync(CancellationToken.None);
            await placement.PrepareHandoffAsync();
            Assert.True(MotionService.IsAt(supply.Motion.Feedback, settings.PcbSupply.HandoffPosition));
            Assert.True(MotionService.IsAt(placement.Motion.Feedback, settings.PcbPlacementHandler.HandoffPosition));

            foreach (var group in new[] { HardwareArea.PcbSupply, HardwareArea.PcbPlacementHandler })
            {
                teaching.SelectedTeachingUnit = group;
                await WaitUntilAsync(() => teaching.StepCommand.CanExecute(TeachingDirection.XPlus));
                var feedback = group == HardwareArea.PcbSupply ? supply.Motion.Feedback : placement.Motion.Feedback;
                var expectedX = feedback.Position.X + teaching.StepDistance;

                await teaching.StepCommand.ExecuteAsync(TeachingDirection.XPlus);

                Assert.Equal(expectedX, feedback.Position.X, 6);
                Assert.Equal(MachineAlarm.None, state.Alarm);
            }

            var supplyPosition = supply.Motion.Feedback.Position;
            var manual = services.GetRequiredService<MotionWindowViewModel>();
            var placementX = manual.Axes.Single(
                row => row.Group == MotionGroup.PcbPlacementHandler && row.Axis == MotionAxis.X);
            await WaitUntilAsync(() => placementX.HomeCommand.CanExecute(null));
            await placementX.HomeCommand.ExecuteAsync(null);
            Assert.Equal(0, placement.Motion.Feedback.Position.X);

            await WaitUntilAsync(() => teaching.HomeCommand.CanExecute(null));
            await teaching.HomeCommand.ExecuteAsync(null);
            Assert.Equal((0, 0, 0), placement.Motion.Feedback.Position);
            Assert.All(placement.Motion.Feedback.Axes, axis => Assert.True(placement.Motion.Feedback.GetAxisState(axis).Homed));
            Assert.Equal(supplyPosition, supply.Motion.Feedback.Position);
            Assert.Equal(MachineAlarm.None, state.Alarm);
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task TeachingControlsOnlyItsOwnBackupPlate()
    {
        var settings = FlowSettings();
        settings.Units.Inspection = false;
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        var teaching = services.GetRequiredService<TeachingViewModel>();
        (HardwareArea Group, TeachingTarget Target, InputIo Up, InputIo Down, OutputIo Output)[] stations = [
            (
                HardwareArea.PcbPlacementHandler,
                TeachingTarget.HeatSink1PcbPlacement,
                InputIo.PcbPlacementBackupPlateUp,
                InputIo.PcbPlacementBackupPlateDown,
                OutputIo.PcbPlacementBackupPlateUp),
            (
                HardwareArea.BoltFastening,
                TeachingTarget.ShootingHeadUpperLeftLocatingPin,
                InputIo.BoltFasteningBackupPlateUp,
                InputIo.BoltFasteningBackupPlateDown,
                OutputIo.BoltFasteningBackupPlateUp),
            (
                HardwareArea.InspectionGantry,
                TeachingTarget.CarrierUpperLeftLocatingPin,
                InputIo.InspectionBackupPlateUp,
                InputIo.InspectionBackupPlateDown,
                OutputIo.InspectionBackupPlateUp),
        ];
        foreach (var station in stations)
        {
            await ((IIoService)io).SetOutputAndWaitAsync(station.Output, false);
        }

        foreach (var (group, target, up, down, output) in stations)
        {
            teaching.SelectedTeachingUnit = group;
            teaching.SelectedPoint = teaching.FilteredPoints.Single(point => point.Position.Target == target);
            var plate = TeachingRows(teaching)[output];
            await WaitUntilAsync(() => plate.ToggleOutputCommand.CanExecute(null));
            await plate.ToggleOutputCommand.ExecuteAsync(null);
            Assert.True(io.GetInput(up));
            Assert.False(io.GetInput(down));
            foreach (var other in stations.Where(station => station.Group != group))
            {
                Assert.False(TeachingRows(teaching).ContainsKey(other.Output));
                Assert.False(io.GetInput(other.Up));
            }

            await plate.ToggleOutputCommand.ExecuteAsync(null);
            Assert.False(io.GetInput(up));
            Assert.True(io.GetInput(down));
        }

        var state = services.GetRequiredService<MachineState>();
        Assert.True(machine.IsHomeAllowed);
        await machine.HomeAsync(CancellationToken.None);
        var supply = services.GetRequiredKeyedService<IXyMotion>(MotionGroup.PcbSupply);
        await supply.MoveAxisAsync(MotionAxis.X, settings.PcbSupply.HandoffPosition.X, 1_000);
        teaching.SelectedTeachingUnit = HardwareArea.PcbPlacementHandler;
        Assert.Equal(settings.PcbSupply.HandoffPosition.X, supply.Position.X);
        await WaitUntilAsync(() => teaching.JogCommand.CanExecute(TeachingDirection.XPlus));
        var placementPlate = TeachingRows(teaching)[OutputIo.PcbPlacementBackupPlateUp];
        await WaitUntilAsync(() => placementPlate.ToggleOutputCommand.CanExecute(null));
        await placementPlate.ToggleOutputCommand.ExecuteAsync(null);
        await placementPlate.ToggleOutputCommand.ExecuteAsync(null);
        supply.SetServo(MotionAxis.X, false);
        Assert.False(state.ManualControlsEnabled);
        await WaitUntilAsync(() => placementPlate.ToggleOutputCommand.CanExecute(null));
        io.SetInput(InputIo.AutoMode, false);
        await WaitUntilAsync(() => !placementPlate.ToggleOutputCommand.CanExecute(null));
        io.SetInput(InputIo.AutoMode, true);

        teaching.SelectedTeachingUnit = HardwareArea.InspectionGantry;
        settings.Options.TimeoutMilliseconds = 50;
        io.AutoResponseEnabled = false;
        await TeachingRows(teaching)[OutputIo.InspectionBackupPlateUp].ToggleOutputCommand.ExecuteAsync(null);
        Assert.Equal(MachineAlarm.MainConveyor, state.Alarm);
    }

    [Fact]
    public async Task TeachingSavePersistsHandoffAndRecipeUnderIdleManualControl()
    {
        var settings = FlowSettings();
        var store = VirtualTest.OpenMachineStore(
            Path.Combine(Path.GetTempPath(), $"IBTM-buffer-teaching-{Guid.NewGuid():N}.db"));
        await using var services = new ServiceCollection().AddSingleton(store)
            .AddIbtmApplication(settings)
            .BuildServiceProvider();
        var machine = services.GetRequiredService<MachineController>();
        var teaching = services.GetRequiredService<TeachingViewModel>();
        teaching.SelectedTeachingUnit = HardwareArea.PcbSupply;
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        await WaitUntilAsync(() => teaching.SaveCommand.CanExecute(null));
        var supplyMotion = services.GetRequiredKeyedService<IXyMotion>(MotionGroup.PcbSupply);
        await supplyMotion.MoveToXYAsync(70, 20, settings.PcbSupply.Motion.HorizontalSpeed);
        await supplyMotion.MoveAxisAsync(MotionAxis.Z, 4, settings.PcbSupply.Motion.ZSpeed);
        teaching.SelectedPoint = teaching.FilteredPoints.Single(point => point.Position.Target == TeachingTarget.SupplyHandoff);
        await teaching.TeachCurrentPositionCommand.ExecuteAsync(null);
        Assert.Equal((70, 20, 4), (settings.PcbSupply.HandoffPosition.X,
            settings.PcbSupply.HandoffPosition.Y, settings.PcbSupply.HandoffPosition.Z));
        teaching.SelectedTeachingUnit = HardwareArea.PcbPlacementHandler;
        var placementMotion = services.GetRequiredKeyedService<IXyMotion>(MotionGroup.PcbPlacementHandler);
        await placementMotion.MoveToXYAsync(75, 25, settings.PcbPlacementHandler.Motion.HorizontalSpeed);
        await placementMotion.MoveAxisAsync(MotionAxis.Z, 7, settings.PcbPlacementHandler.Motion.ZSpeed);
        teaching.SelectedPoint = teaching.FilteredPoints.Single(point => point.Position.Target == TeachingTarget.PlacementHandoff);
        await teaching.TeachCurrentPositionCommand.ExecuteAsync(null);
        teaching.RecipeEditor.Name = "Unsaved product name";
        // Leaving and reopening Teaching must not replace recorded coordinates or the edited recipe name.
        await teaching.ShutdownAsync();
        teaching.Activate();
        teaching.Deactivate(); // This test verifies data without a WPF display dispatcher.
        Assert.Equal("Unsaved product name", teaching.RecipeEditor.Name);
        Assert.Equal((75, 25, 7), (
            teaching.SelectedPoint!.Coordinates!.X, teaching.SelectedPoint.Coordinates!.Y, teaching.SelectedPoint.Coordinates!.Z));
        teaching.SelectedTeachingUnit = HardwareArea.PcbSupply;
        var recordedHandoff = teaching.FilteredPoints.Single(point => point.Position.Target == TeachingTarget.SupplyHandoff);
        Assert.Equal((70, 20, 4), (recordedHandoff.Coordinates!.X, recordedHandoff.Coordinates!.Y, recordedHandoff.Coordinates!.Z));
        var recipe = services.GetRequiredService<RecipeManager>().Current;
        recipe.BoltInspection.LightLevel = 123;
        teaching.RecipeEditor.Name = " ";
        Assert.False(teaching.SaveCommand.CanExecute(null));
        await teaching.SaveCommand.ExecuteAsync(null);
        Assert.Equal(70, settings.PcbSupply.HandoffPosition.X);
        Assert.Empty(store.RecipeNames);
        teaching.RecipeEditor.Name = "Unified teaching";

        teaching.SelectedTeachingUnit = HardwareArea.PcbSupply;
        await teaching.TeachCurrentPositionCommand.ExecuteAsync(null);
        Assert.Equal(70, teaching.FilteredPoints.Single(
            point => point.Position.Target == TeachingTarget.SupplyHandoff).Coordinates!.X);
        teaching.SelectedTeachingUnit = HardwareArea.PcbPlacementHandler;
        Assert.Equal(75, teaching.FilteredPoints.Single(
            point => point.Position.Target == TeachingTarget.PlacementHandoff).Coordinates!.X);
        Assert.Equal(70, settings.PcbSupply.HandoffPosition.X);
        recipe.PcbSupply.Pcb1PickPosition = new() { X = 12, Y = 34, Z = 56 };

        using (services.GetRequiredService<OperationCancellation>().Link())
        {
            await WaitUntilAsync(() => !teaching.SaveCommand.CanExecute(null));
            await teaching.SaveCommand.ExecuteAsync(null);
            Assert.Equal(70, settings.PcbSupply.HandoffPosition.X);
        }

        await WaitUntilAsync(() => teaching.SaveCommand.CanExecute(null));

        io.SetInput(InputIo.AutoMode, false);
        await WaitUntilAsync(() => !teaching.SaveCommand.CanExecute(null));
        await teaching.SaveCommand.ExecuteAsync(null);
        Assert.Equal(70, settings.PcbSupply.HandoffPosition.X);

        io.SetInput(InputIo.AutoMode, true);
        await WaitUntilAsync(() => teaching.SaveCommand.CanExecute(null));
        settings.Units.PcbSupply = false;
        await teaching.SaveCommand.ExecuteAsync(null);

        Assert.Null(teaching.SaveError);
        Assert.Equal(70, settings.PcbSupply.HandoffPosition.X);
        Assert.Equal(75, settings.PcbPlacementHandler.HandoffPosition.X);
        var saved = store.LoadSettings().Get<PcbSupplySettings>();
        var savedPlacement = store.LoadSettings().Get<PcbPlacementHandlerSettings>();
        Assert.Equal(70, saved.HandoffPosition.X);
        Assert.Equal(20, saved.HandoffPosition.Y);
        Assert.Equal(4, saved.HandoffPosition.Z);
        Assert.Equal(75, savedPlacement.HandoffPosition.X);
        Assert.Equal(25, savedPlacement.HandoffPosition.Y);
        Assert.Equal(7, savedPlacement.HandoffPosition.Z);
        var savedRecipe = store.LoadRecipe("Unified teaching");
        Assert.Equal(34, savedRecipe.PcbSupply.Pcb1PickPosition.Y);
        Assert.Equal(123, savedRecipe.BoltInspection.LightLevel);
        Assert.Equal("Unified teaching", teaching.RecipeEditor.ActiveName);
        Assert.Contains("Unified teaching", teaching.RecipeEditor.Recipes);

        await teaching.ShutdownAsync();
        teaching.Activate();
        teaching.Deactivate();
        Assert.Equal((75, 25, 7), (
            teaching.SelectedPoint!.Coordinates!.X, teaching.SelectedPoint.Coordinates!.Y, teaching.SelectedPoint.Coordinates!.Z));

        teaching.SelectedTeachingUnit = HardwareArea.PcbSupply;
        teaching.FilteredPoints.Single(point => point.Position.Target == TeachingTarget.SupplyHandoff)
            .Teach(80, 0, 0);
        teaching.SaveError = "Previous save failure";
        void StopBeforeWriting(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(TeachingViewModel.SaveError) && teaching.SaveError is null)
                machine.Stop();
        }

        teaching.PropertyChanged += StopBeforeWriting;
        try
        {
            await teaching.SaveCommand.ExecuteAsync(null);
        }
        finally
        {
            teaching.PropertyChanged -= StopBeforeWriting;
        }

        Assert.Equal(80, settings.PcbSupply.HandoffPosition.X);
        Assert.Equal(70, store.LoadSettings().Get<PcbSupplySettings>().HandoffPosition.X);
        Assert.Contains("cancelled", teaching.SaveError);
        await WaitUntilAsync(() => teaching.SaveCommand.CanExecute(null));
        await teaching.SaveCommand.ExecuteAsync(null);
        Assert.Null(teaching.SaveError);
        Assert.Equal(80, store.LoadSettings().Get<PcbSupplySettings>().HandoffPosition.X);

        // A failed recipe write must report the partial save and remain retryable.
        using var connection = new SqliteConnection($"Data Source={store.DatabaseFile}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TRIGGER FailTeachingRecipe BEFORE INSERT ON Recipes BEGIN SELECT RAISE(ABORT, 'recipe write failed'); END";
        command.ExecuteNonQuery();
        teaching.RecipeEditor.Name = "Teaching retry";
        teaching.FilteredPoints.Single(point => point.Position.Target == TeachingTarget.SupplyHandoff)
            .Teach(90, 0, 0);
        await teaching.SaveCommand.ExecuteAsync(null);
        Assert.Contains("recipe was not saved", teaching.SaveError);
        Assert.Contains("recipe write failed", teaching.SaveError);
        Assert.Equal(90, store.LoadSettings().Get<PcbSupplySettings>().HandoffPosition.X);
        Assert.DoesNotContain("Teaching retry", store.RecipeNames);
        Assert.Equal("Unified teaching", teaching.RecipeEditor.ActiveName);

        command.CommandText = "DROP TRIGGER FailTeachingRecipe";
        command.ExecuteNonQuery();
        await teaching.SaveCommand.ExecuteAsync(null);
        Assert.Null(teaching.SaveError);
        Assert.Null(teaching.RecipeEditor.Error);
        Assert.Equal("Teaching retry", teaching.RecipeEditor.ActiveName);
        Assert.Equal(123, store.LoadRecipe("Teaching retry").BoltInspection.LightLevel);
    }

    [Fact]
    public async Task TeachingSaveRetriesFailedBoltPositionRecordingWithoutReplacingCoordinates()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.BoltFastening);
        settings.BoltFastening.SafeZ = 5;
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var teaching = services.GetRequiredService<TeachingViewModel>();
        var store = services.GetRequiredService<MachineStore>();
        var motion = services.GetRequiredKeyedService<IXyMotion>(MotionGroup.BoltFastening);
        await store.SaveSettingsAsync(settings.Sections);
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        teaching.SelectedTeachingUnit = HardwareArea.BoltFastening;
        teaching.SelectedPoint = teaching.FilteredPoints.Single(point => point.Position.Target == TeachingTarget.SafeZ);
        await motion.MoveAxisAsync(MotionAxis.Z, 8, 10_000);
        await WaitUntilAsync(() => teaching.TeachCurrentPositionCommand.CanExecute(null));
        using var connection = new SqliteConnection($"Data Source={store.DatabaseFile}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TRIGGER FailBoltTeaching BEFORE UPDATE ON Settings WHEN NEW.Key = 'BoltFasteningSettings' BEGIN SELECT RAISE(ABORT, 'bolt teaching write failed'); END";
        command.ExecuteNonQuery();

        await teaching.TeachCurrentPositionCommand.ExecuteAsync(null);

        Assert.Contains("bolt teaching write failed", teaching.SaveError);
        Assert.Equal(8, settings.BoltFastening.SafeZ);
        Assert.Equal(5, store.LoadSettings().Get<BoltFasteningSettings>().SafeZ);
        command.CommandText = "DROP TRIGGER FailBoltTeaching";
        command.ExecuteNonQuery();
        // Saving retries the recorded value, even after the physical axis has moved elsewhere.
        await motion.MoveAxisAsync(MotionAxis.Z, 12, 10_000);
        await WaitUntilAsync(() => teaching.SaveCommand.CanExecute(null));
        await teaching.SaveCommand.ExecuteAsync(null);

        Assert.Null(teaching.SaveError);
        Assert.Equal(8, settings.BoltFastening.SafeZ);
        Assert.Equal(8, store.LoadSettings().Get<BoltFasteningSettings>().SafeZ);
        Assert.Equal(12, motion.Position.Z);
        teaching.SelectedPcb = HeatSinkSlot.HeatSink2;
        Assert.Equal(8, teaching.SelectedPoint!.Coordinates!.Z);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TeachingSaveCancelledBeforeExecutionKeepsRecordedCoordinates(bool closeTeaching)
    {
        var settings = FlowSettings();
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var teaching = services.GetRequiredService<TeachingViewModel>();
        teaching.SelectedTeachingUnit = HardwareArea.PcbSupply;
        var operations = services.GetRequiredService<OperationCancellation>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        await WaitUntilAsync(() => teaching.SaveCommand.CanExecute(null));
        teaching.FilteredPoints.Single(point => point.Position.Target == TeachingTarget.SupplyHandoff)
            .Teach(70, 0, 0);

        void CancelWhenStarted()
        {
            if (!operations.HasActiveOperations)
                return;
            if (closeTeaching)
                teaching.Deactivate();
            else
                machine.Stop();
        }

        operations.ActivityChanged += CancelWhenStarted;
        await teaching.SaveCommand.ExecuteAsync(null);
        operations.ActivityChanged -= CancelWhenStarted;

        Assert.Equal(70, settings.PcbSupply.HandoffPosition.X);
        Assert.Null(teaching.SaveError);
        Assert.False(operations.HasActiveOperations);
    }

    [Fact]
    public async Task FasteningTeachingAdjustsOneAxisWithHeadsDownAndPreservesPositioningRules()
    {
        var settings = FlowSettings();
        settings.Units.Inspection = false;
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var gantry = services.GetRequiredService<BoltFasteningStation>();
        var teaching = services.GetRequiredService<TeachingViewModel>();
        await machine.InitializeAsync();
        await gantry.RaiseCylindersAsync();
        Assert.True(await gantry.HomeAxisAsync(MotionAxis.Z));
        Assert.True(await gantry.HomeHorizontalAsync());
        await gantry.MoveToXYAsync(20, 20);
        await gantry.MoveZAsync(10);
        await Task.WhenAll(
            ((IIoService)io).SetOutputAndWaitAsync(OutputIo.PickupHeadDown, true),
            ((IIoService)io).SetOutputAndWaitAsync(OutputIo.ShootingHeadDown, true));
        teaching.SelectedTeachingUnit = HardwareArea.BoltFastening;
        teaching.SelectedPoint = teaching.FilteredPoints.Single(
            point => point.Position.Target == TeachingTarget.BoltPickup);
        teaching.JogSpeed = 1;
        teaching.StepDistance = 0.1;
        await WaitUntilAsync(() => !teaching.MoveToPointCommand.CanExecute(null));
        await WaitUntilAsync(() => teaching.TeachCurrentPositionCommand.CanExecute(null));
        Assert.Equal(HomeBlockReason.FasteningNotRaised, machine.GetHomeBlock(requireRaised: true));
        await WaitUntilAsync(() => teaching.StepCommand.CanExecute(TeachingDirection.XPlus));

        await teaching.StepCommand.ExecuteAsync(TeachingDirection.XPlus);
        await teaching.StepCommand.ExecuteAsync(TeachingDirection.YMinus);
        var adjusted = gantry.Motion.Feedback.Position;
        Assert.Equal(20.1, adjusted.X, 6);
        Assert.Equal(19.9, adjusted.Y, 6);
        Assert.Equal(10, adjusted.Z);
        var jog = teaching.JogCommand.ExecuteAsync(TeachingDirection.XPlus);
        await WaitUntilAsync(() => gantry.Motion.Feedback.Position.X > 20.1);
        Assert.Equal(MotionCommand.Adjustment, gantry.Motion.Feedback.Command);
        teaching.JogStopCommand.Execute(null);
        await jog.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(MotionCommand.None, gantry.Motion.Feedback.Command);
        var stopped = gantry.Motion.Feedback.Position;
        await Task.Delay(30);
        Assert.Equal(stopped, gantry.Motion.Feedback.Position);
        Assert.Equal(adjusted.Y, stopped.Y);
        Assert.Equal(10, stopped.Z);
        Assert.True(io.GetInput(InputIo.PickupHeadDown));
        Assert.True(io.GetInput(InputIo.ShootingHeadDown));
        await WaitUntilAsync(() => !state.IsRunning);
        Assert.Equal(MachineAlarm.None, state.Alarm);
        await Assert.ThrowsAsync<MotionInterlockException>(() => gantry.MoveToXYAsync(30, 30));
        await Assert.ThrowsAsync<MotionInterlockException>(() => gantry.HomeHorizontalAsync());

        await gantry.AdjustAxisAsync(MotionAxis.X, -56.561, 10_000);
        Assert.Equal(-56.561, gantry.Motion.Feedback.Position.X, 6);
        Assert.Equal(MotionCommand.None, gantry.Motion.Feedback.Command);
        await gantry.AdjustAxisAsync(MotionAxis.X, 201, 10_000);
        using var jogStop = new CancellationTokenSource();
        var motion = services.GetRequiredKeyedService<IXyMotion>(MotionGroup.BoltFastening);
        var beyondOldMaximum = motion.JogAsync(MotionAxis.X, 10, jogStop.Token);
        await WaitUntilAsync(() => gantry.Motion.Feedback.Position.X > 201.1);
        jogStop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => beyondOldMaximum);
        Assert.Equal(stopped.Y, gantry.Motion.Feedback.Position.Y);
        Assert.Equal(stopped.Z, gantry.Motion.Feedback.Position.Z);
        await WaitUntilAsync(() => teaching.StepCommand.CanExecute(TeachingDirection.XPlus));

        await gantry.RaiseCylindersAsync();
        await WaitUntilAsync(() => teaching.MoveToPointCommand.CanExecute(null));
        MotionCommand positioning = MotionCommand.None;
        gantry.Motion.Feedback.MovingChanged += moving =>
        {
            if (moving)
                positioning = gantry.Motion.Feedback.Command;
        };
        await gantry.MoveToXYAsync(200, stopped.Y);
        Assert.Equal(MotionCommand.Positioning, positioning);
        Assert.Equal(MotionCommand.None, gantry.Motion.Feedback.Command);

        await WaitUntilAsync(() => teaching.JogCommand.CanExecute(TeachingDirection.XMinus));
        var fail = true;
        gantry.Motion.Feedback.PositionChanged += (_, _, _) =>
        {
            if (!fail)
                return;
            fail = false;
            throw new MotionException("Injected teaching move", new IOException());
        };
        await teaching.JogCommand.ExecuteAsync(TeachingDirection.XMinus);
        Assert.Equal(MachineAlarm.MotionUnavailable, state.Alarm);
        Assert.False(gantry.Motion.Feedback.IsMoving);
        Assert.Equal(MotionCommand.None, gantry.Motion.Feedback.Command);
    }

    public enum TeachingStopAction
    {
        Stop,
        ChangeUnit,
        Close,
    }

    [Theory]
    [InlineData(NgTransferDestination.Shuttle, InputIo.InspectionHeatSink1Present)]
    [InlineData(NgTransferDestination.Station, InputIo.NgShuttleUp)]
    public async Task NgPickupRechecksSourceAfterDescent(
        NgTransferDestination destination, InputIo lostInput)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.Inspection);
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<VirtualIoService>();
        var transfer = services.GetRequiredService<InspectionStation>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        await transfer.Station.SeatAsync(CancellationToken.None);
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.NgShuttleDown, false);
        io.SetInput(destination == NgTransferDestination.Shuttle
            ? InputIo.InspectionHeatSink1Present : InputIo.NgShuttleCarrierDetected, true);
        // Detection can remain ON even though the pickup has not gripped anything.
        io.SetInput(InputIo.NgCarrierDetected, true);
        var lost = false;
        var closed = false;
        var raisedAfterLoss = false;
        void LoseSourceDuringDescent(OutputIo output, bool on)
        {
            if (output == OutputIo.NgCarrierPickupDown && on && !lost)
            {
                lost = true;
                io.SetInput(lostInput, false);
            }
            closed |= lost && output == OutputIo.NgCarrierGripperClose && on;
            raisedAfterLoss |= lost && output == OutputIo.NgCarrierPickupDown && !on;
        }

        io.OutputChanged += LoseSourceDuringDescent;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        try
        {
            Assert.False(await transfer.ExecuteTransferAsync(
                destination, InspectionStationState.PickingCarrier, timeout.Token));
            Assert.True(lost);
            Assert.False(closed);
            Assert.False(raisedAfterLoss);
            Assert.False(transfer.IsTransferPending);
            Assert.Equal(NgTransferGripperState.Open, transfer.Gripper);

            io.OutputChanged -= LoseSourceDuringDescent;
            io.SetInput(lostInput, true);
            Assert.True(await transfer.ExecuteTransferAsync(
                destination, transfer.GetNextTransferStep(destination, canPickUp: true), timeout.Token));
            Assert.True(transfer.IsTransferPending);
            Assert.True(transfer.IsRaised);
            Assert.Equal(NgTransferGripperState.Closed, transfer.Gripper);
        }
        finally
        {
            io.OutputChanged -= LoseSourceDuringDescent;
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NgTransferStopsBeforeLoweringAfterGripFeedbackLoss(bool opens)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.Inspection);
        settings.InspectionGantry.Motion.HorizontalSpeed = 200;
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<VirtualIoService>();
        var move = services.GetRequiredService<InspectionStation>();
        var gantry = services.GetRequiredService<InspectionStation>();
        var pickup = services.GetRequiredService<InspectionStation>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        io.SetInput(InputIo.InspectionHeatSink1Present, true);
        await services.GetRequiredService<InspectionStation>().Station.SeatAsync(CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        await move.ExecuteTransferAsync(NgTransferDestination.Shuttle, InspectionStationState.PickingCarrier, timeout.Token);
        var lost = false;
        var lowered = false;
        gantry.Motion.Feedback.PositionChanged += (x, y, z) =>
        {
            if (!lost && gantry.Motion.Feedback.IsMoving && x > 30)
            {
                lost = true;
                io.SetInputs((InputIo.NgCarrierGripperClosed, false), (InputIo.NgCarrierGripperOpen, opens));
            }
        };
        io.OutputChanged += (output, on) => lowered |= output == OutputIo.NgCarrierPickupDown && on;
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => move.ExecuteTransferAsync(
                NgTransferDestination.Shuttle, InspectionStationState.PlacingCarrier, timeout.Token));
            Assert.True(lost);
            Assert.False(lowered);
            Assert.False(gantry.Motion.Feedback.IsMoving);
            Assert.True(pickup.IsTransferPending);
            var restarted = false;
            io.OutputChanged += (output, on) => restarted |= output is OutputIo.NgCarrierGripperClose
                or OutputIo.NgCarrierPickupDown;
            gantry.Motion.Feedback.MovingChanged += moving => restarted |= moving;
            await Assert.ThrowsAsync<InvalidOperationException>(() => move.ExecuteTransferAsync(
                NgTransferDestination.Shuttle,
                move.GetNextTransferStep(NgTransferDestination.Shuttle, canPickUp: true), timeout.Token));
            Assert.False(restarted);
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task SmemaTestInputsRequireTeachingAndClearWhenTheSelectorTurnsOff()
    {
        await using var services = CreateServices(FlowSettings());
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<VirtualIoService>();
        var supply = services.GetRequiredService<PcbSupplier>();
        var conveyor = services.GetRequiredService<IBTM.Conveyor.MainConveyor>();
        await machine.InitializeAsync();
        io.AutoResponseEnabled = false;
        io.SetInput(InputIo.PcbSupplyAvailableFromFront1, false);
        io.SetInput(InputIo.MainConveyorAvailableFromFront2, false);
        io.SetInput(InputIo.MainConveyorReadyFromRear, false);
        try
        {
            Assert.True(io.GetInput(InputIo.AutoMode)); // Teaching/manual contact ON.
            io.SetInput(InputIo.PcbSupplyAvailableFromFront1, true);
            io.SetInput(InputIo.MainConveyorAvailableFromFront2, true);
            io.SetInput(InputIo.MainConveyorReadyFromRear, true);
            Assert.False(supply.UpstreamCarrierAvailable);
            Assert.False(conveyor.UpstreamCarrierAvailable);
            Assert.False(conveyor.DownstreamReady);
            supply.TestUpstreamCarrierAvailable = true;
            conveyor.TestUpstreamCarrierAvailable = true;
            conveyor.TestDownstreamReady = true;
            io.SetInput(InputIo.PcbSupplyAvailableFromFront1, false);
            io.SetInput(InputIo.MainConveyorAvailableFromFront2, false);
            io.SetInput(InputIo.MainConveyorReadyFromRear, false);
            Assert.True(supply.UpstreamCarrierAvailable);
            Assert.True(conveyor.UpstreamCarrierAvailable);
            Assert.True(conveyor.DownstreamReady);

            io.SetInput(InputIo.AutoMode, false);
            Assert.False(supply.TestUpstreamCarrierAvailable);
            Assert.False(conveyor.TestUpstreamCarrierAvailable);
            Assert.False(conveyor.TestDownstreamReady);
            Assert.False(supply.UpstreamCarrierAvailable);
            Assert.False(conveyor.UpstreamCarrierAvailable);
            Assert.False(conveyor.DownstreamReady);

            // Direct calls cannot enable TEST while the teaching switch is OFF.
            supply.TestUpstreamCarrierAvailable = true;
            conveyor.TestUpstreamCarrierAvailable = true;
            conveyor.TestDownstreamReady = true;
            Assert.False(supply.TestUpstreamCarrierAvailable);
            Assert.False(conveyor.TestUpstreamCarrierAvailable);
            Assert.False(conveyor.TestDownstreamReady);

            // Real SMEMA remains usable in AUTO.
            io.SetInput(InputIo.PcbSupplyAvailableFromFront1, true);
            io.SetInput(InputIo.MainConveyorAvailableFromFront2, true);
            io.SetInput(InputIo.MainConveyorReadyFromRear, true);
            Assert.True(supply.UpstreamCarrierAvailable);
            Assert.True(conveyor.UpstreamCarrierAvailable);
            Assert.True(conveyor.DownstreamReady);
            io.SetInput(InputIo.PcbSupplyAvailableFromFront1, false);
            io.SetInput(InputIo.MainConveyorAvailableFromFront2, false);
            io.SetInput(InputIo.MainConveyorReadyFromRear, false);
            io.SetInput(InputIo.AutoMode, true);
            Assert.False(supply.UpstreamCarrierAvailable);
            Assert.False(conveyor.UpstreamCarrierAvailable);
            Assert.False(conveyor.DownstreamReady);
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task FasteningCompletionCannotCompleteAReplacementCarrier()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.BoltFastening);
        await using var services = CreateServices(settings);
        services.GetRequiredService<RecipeManager>().Current.Pcb.BoltPoints =
            [new() { Number = 1, Head = FasteningHead.Shooting, X = 0, Y = 0, FasteningX = 0, FasteningY = 0 }];
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<VirtualIoService>();
        var station = services.GetRequiredService<BoltFasteningStation>();
        var work = services.GetRequiredService<BoltFasteningStation>().Station;
        var gantry = services.GetRequiredService<BoltFasteningStation>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        VirtualTest.SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, true);
        io.SetInput(InputIo.BoltFasteningHeatSink1Present, true);
        await work.SeatAsync(CancellationToken.None);
        var previousAssembly = work.GetAssembly(HeatSinkSlot.HeatSink1);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var replaced = false;
        void ReplaceAfterResult(HeatSinkAssembly assembly)
        {
            if (replaced || !assembly.PcbBoltResults.ContainsKey(1))
                return;
            replaced = true;
            VirtualTest.SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, false);
            VirtualTest.SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, true);
            stop.Cancel();
        }

        previousAssembly.ResultsChanged += ReplaceAfterResult;
        try
        {
            await station.RunAsync(stop.Token).WaitAsync(TimeSpan.FromSeconds(4));
            Assert.True(replaced);
            Assert.True(previousAssembly.PcbBoltResults[1].Success);
            Assert.Empty(work.Assemblies);
            Assert.False(work.Completed);
            Assert.False(gantry.Motion.Feedback.IsMoving);
        }
        finally
        {
            previousAssembly.ResultsChanged -= ReplaceAfterResult;
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SupplyDoesNotAdvanceTheNewCarrierWhenAnOldPickupFinishes(bool testSignal)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.PcbSupply);
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<VirtualIoService>();
        var supply = services.GetRequiredService<PcbSupplier>();
        var handler = services.GetRequiredService<PcbSupplier>();
        var motion = handler.Motion.Feedback;
        var recipe = new PcbSupplyRecipe
        {
            Pcb1PickPosition = new() { X = 10, Y = 10, Z = 5 },
            Pcb2PickPosition = new() { X = 20, Y = 10, Z = 5 },
        };
        services.GetRequiredService<RecipeManager>().Current.PcbSupply = recipe;
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        io.AutoResponseEnabled = false;
        io.SetInput(InputIo.PcbSupplyRotated, true);
        io.SetInput(InputIo.PcbSupplyUnrotated, false);
        io.SetInput(InputIo.PcbSupplyPcbDetected, false);
        io.SetInput(InputIo.PcbSupplyAvailableFromFront1, false);
        io.SetInput(InputIo.AutoMode, testSignal);
        void SetCarrierAvailable(bool available)
        {
            if (testSignal)
                handler.TestUpstreamCarrierAvailable = available;
            else
                io.SetInput(InputIo.PcbSupplyAvailableFromFront1, available);
        }
        SetCarrierAvailable(true);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var firstSlotVisits = 0;
        var atFirstSlot = false;
        var skippedFirstSlot = false;
        void ChangeCarrierAtPickup(double x, double y, double z)
        {
            var atPickup = Math.Abs(x - 10) < 0.01
                && Math.Abs(y - recipe.Pcb1PickPosition.Y!.Value) < 0.01
                && Math.Abs(z - 5) < 0.01;
            if (atPickup && !atFirstSlot)
            {
                firstSlotVisits++;
                if (firstSlotVisits == 1)
                {
                    SetCarrierAvailable(false);
                    SetCarrierAvailable(true);
                }
                else
                    stop.Cancel();
            }
            atFirstSlot = atPickup;
            if (firstSlotVisits == 1 && x > 15)
            {
                skippedFirstSlot = true;
                stop.Cancel();
            }
        }

        motion.PositionChanged += ChangeCarrierAtPickup;
        try
        {
            await supply.RunAsync(services.GetRequiredService<PcbPlacer>(), stop.Token).WaitAsync(TimeSpan.FromSeconds(4));
            Assert.False(skippedFirstSlot);
            Assert.Equal(2, firstSlotVisits);
            Assert.False(motion.IsMoving);
            Assert.Equal(!testSignal, io.GetOutput(OutputIo.PcbSupplyReadyToFront1));
        }
        finally
        {
            motion.PositionChanged -= ChangeCarrierAtPickup;
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepeatNgTransferReturnsToPickupThenSeparateWaitingPosition(bool hasWaitingPosition)
    {
        await using var services = CreateDisplayServices(out var feedback);
        var machine = services.GetRequiredService<MachineController>();
        var move = services.GetRequiredService<InspectionStation>();
        var settings = services.GetRequiredService<NgCarrierTransferSettings>();
        var gantry = services.GetRequiredService<InspectionStation>();
        var pickup = services.GetRequiredService<InspectionStation>();
        var io = services.GetRequiredService<VirtualIoService>();
        settings.WaitingPosition = hasWaitingPosition ? new() { X = 15, Y = 30 } : null;
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        // Repeat holds the carrier above the shuttle and returns without releasing it.
        io.SetInputs(
            (InputIo.InspectionHeatSink1Present, true),
            (InputIo.InspectionHeatSink2Present, true));
        await services.GetRequiredService<InspectionStation>().Station.SeatAsync(CancellationToken.None);
        await gantry.MoveToAsync(settings.CarrierPickupPosition!, 10_000);
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.NgShuttleDown, false);
        using var transferTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await move.ExecuteTransferAsync(NgTransferDestination.Shuttle,
            InspectionStationState.PickingCarrier, transferTimeout.Token, holdAtDestination: true);
        await move.ExecuteTransferAsync(NgTransferDestination.Shuttle,
            InspectionStationState.PlacingCarrier, transferTimeout.Token, holdAtDestination: true);
        Assert.True(move.IsTransferPending);
        Assert.Equal(NgTransferGripperState.Closed, pickup.Gripper);
        Assert.True(pickup.IsRaised);
        Assert.True(MotionService.IsAt(gantry.Motion.Feedback, settings.ShuttlePlacePosition));
        Assert.False(io.GetInput(InputIo.NgShuttleCarrierDetected));
        await move.Station.PrepareToReceiveAsync(transferTimeout.Token);
        var raisedAtPickup = false;
        io.OutputChanged += (output, on) =>
        {
            if (output != OutputIo.InspectionBackupPlateUp || !on)
                return;
            Assert.True(MotionService.IsAt(gantry.Motion.Feedback, settings.CarrierPickupPosition!));
            Assert.True(pickup.IsRaised);
            raisedAtPickup = true;
        };
        feedback.AxisMoves.Clear();
        var movedBeforeGrip = false;
        gantry.Motion.Feedback.PositionChanged += (_, _, _) =>
        {
            if (pickup.Gripper != NgTransferGripperState.Closed)
                movedBeforeGrip = true;
        };

        await move.ReturnToStationAsync(transferTimeout.Token).WaitAsync(TimeSpan.FromSeconds(3));

        Assert.False(move.IsTransferPending);
        Assert.True(raisedAtPickup);
        Assert.False(movedBeforeGrip);
        Assert.Empty(feedback.AxisMoves);
        Assert.True(io.GetInput(InputIo.InspectionHeatSink1Present));
        Assert.False(io.GetInput(InputIo.NgShuttleCarrierDetected));
        Assert.True(pickup.IsRaised);
        Assert.Equal(NgTransferGripperState.Open, pickup.Gripper);
        Assert.True(MotionService.IsAt(gantry.Motion.Feedback, settings.CarrierPickupPosition!));
        if (!hasWaitingPosition)
        {
            var position = settings.CarrierPickupPosition!;
            await Assert.ThrowsAsync<InvalidOperationException>(() => move.ClearStationAsync(CancellationToken.None));
            Assert.Empty(feedback.AxisMoves);
            Assert.True(MotionService.IsAt(gantry.Motion.Feedback, position));
            return;
        }

        var movedAfterReturn = false;
        gantry.Motion.Feedback.PositionChanged += (x, y, z) => movedAfterReturn = true;
        await move.ClearStationAsync(CancellationToken.None);
        Assert.True(movedAfterReturn);
        Assert.Empty(feedback.AxisMoves);
        Assert.True(MotionService.IsAt(gantry.Motion.Feedback, settings.WaitingPosition!));
        Assert.True(io.GetInput(InputIo.InspectionHeatSink1Present));
        Assert.False(io.GetInput(InputIo.NgCarrierDetected));
    }

    [Fact]
    public async Task NgPickupMovesXyTogetherAndResumesBeforeGripping()
    {
        await using var services = CreateDisplayServices(out var feedback);
        var machine = services.GetRequiredService<MachineController>();
        var move = services.GetRequiredService<InspectionStation>();
        var settings = services.GetRequiredService<NgCarrierTransferSettings>();
        var gantry = services.GetRequiredService<InspectionStation>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        await gantry.MoveToAsync(new() { X = 50, Y = 60 }, 10_000);

        settings.CarrierPickupPosition = null;
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => move.ExecuteTransferAsync(NgTransferDestination.Shuttle, InspectionStationState.PickingCarrier, CancellationToken.None)!);
        Assert.Empty(feedback.AxisMoves);
        Assert.Equal((50, 60, 0), gantry.Motion.Feedback.Position);

        settings.CarrierPickupPosition = new() { X = 5, Y = 20 };
        services.GetRequiredService<InspectionGantrySettings>().Motion.HorizontalSpeed = 100;
        settings.ShuttlePlacePosition = new() { X = 150, Y = 80 };
        var pickupPosition = settings.CarrierPickupPosition!;
        using var cancellation = new CancellationTokenSource();
        void StopDuringPickupMove(double x, double y, double z)
        {
            if (x > pickupPosition.X && x < 49
                && y > pickupPosition.Y && y < 59)
                cancellation.Cancel();
        }
        gantry.Motion.Feedback.PositionChanged += StopDuringPickupMove;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => move.ExecuteTransferAsync(NgTransferDestination.Shuttle, InspectionStationState.PickingCarrier, cancellation.Token)!);
        gantry.Motion.Feedback.PositionChanged -= StopDuringPickupMove;
        Assert.Empty(feedback.AxisMoves);
        var stopped = gantry.Motion.Feedback.Position;
        Assert.InRange(stopped.X, pickupPosition.X + 0.01, 49.99);
        Assert.InRange(stopped.Y, pickupPosition.Y + 0.01, 59.99);
        Assert.True(io.GetInput(InputIo.NgCarrierPickupUp));
        Assert.False(io.GetInput(InputIo.NgCarrierDetected));

        feedback.AxisMoves.Clear();
        Assert.False(await move.ExecuteTransferAsync(
            NgTransferDestination.Shuttle, InspectionStationState.PickingCarrier, CancellationToken.None));
        Assert.Empty(feedback.AxisMoves);
        Assert.True(MotionService.IsAt(gantry.Motion.Feedback, pickupPosition));
        Assert.True(io.GetInput(InputIo.NgCarrierPickupUp));
        Assert.Equal(settings.CarrierPickupPosition!.X, gantry.Motion.Feedback.Position.X);

        await services.GetRequiredService<InspectionStation>().Station.SeatAsync(CancellationToken.None);
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.NgShuttleDown, false);
        VirtualTest.SetCarrier(io, InputIo.InspectionHeatSink1Present, true);
        Assert.Equal(InspectionStationState.PickingCarrier, move.GetNextTransferStep(NgTransferDestination.Shuttle, canPickUp: true));
        await move.ExecuteTransferAsync(NgTransferDestination.Shuttle, InspectionStationState.PickingCarrier, CancellationToken.None)!;
        Assert.Equal(InspectionStationState.PlacingCarrier, move.GetNextTransferStep(NgTransferDestination.Shuttle, canPickUp: true));
        Assert.True(io.GetInput(InputIo.NgCarrierPickupUp));
        Assert.True(io.GetInput(InputIo.NgCarrierDetected));
        Assert.Equal(5, gantry.Motion.Feedback.Position.X);
        Assert.Empty(feedback.AxisMoves);

        feedback.AxisMoves.Clear();
        var axesMovedTogether = false;
        void ObserveShuttleMove(double x, double y, double z)
        {
            axesMovedTogether |= x > pickupPosition.X && x < settings.ShuttlePlacePosition.X
                && y > pickupPosition.Y && y < settings.ShuttlePlacePosition.Y;
        }
        gantry.Motion.Feedback.PositionChanged += ObserveShuttleMove;
        try
        {
            await move.ExecuteTransferAsync(NgTransferDestination.Shuttle, InspectionStationState.PlacingCarrier, CancellationToken.None)!;
        }
        finally
        {
            gantry.Motion.Feedback.PositionChanged -= ObserveShuttleMove;
        }
        Assert.True(axesMovedTogether);
        Assert.Empty(feedback.AxisMoves);
        Assert.True(MotionService.IsAt(gantry.Motion.Feedback, settings.ShuttlePlacePosition));
    }

    [Theory]
    [InlineData(NgTransferDestination.Station)]
    [InlineData(NgTransferDestination.Shuttle)]
    public async Task NgTransferUsesTheSameLiveReleaseStatesInBothDirections(
        NgTransferDestination destination)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.Inspection);
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<VirtualIoService>();
        var move = services.GetRequiredService<InspectionStation>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        await services.GetRequiredService<InspectionStation>().Station.SeatAsync(CancellationToken.None);
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.NgShuttleDown, false);
        // Establish pickup ownership through the real sequence, independently of presence DI.
        await move.ExecuteTransferAsync(destination, InspectionStationState.PickingCarrier,
            CancellationToken.None, allowEmpty: true);
        await services.GetRequiredService<InspectionStation>()
            .MoveToAsync(
                destination == NgTransferDestination.Station
                    ? settings.NgCarrierTransfer.CarrierPickupPosition!
                    : settings.NgCarrierTransfer.ShuttlePlacePosition,
                1_000);
        io.AutoResponseEnabled = false;
        var destinationSensor = destination == NgTransferDestination.Station
            ? InputIo.InspectionHeatSink1Present
            : InputIo.NgShuttleCarrierDetected;
        io.SetInput(InputIo.NgCarrierDetected, true);
        io.SetInput(InputIo.NgCarrierGripperOpen, false);
        io.SetInput(InputIo.NgCarrierGripperClosed, true);
        Assert.Equal(
            InspectionStationState.WaitingForDestination,
            move.GetNextTransferStep(destination, canPickUp: true, canReceive: false));
        io.SetInput(destinationSensor, true);
        AssertState(InspectionStationState.WaitingForDestination);
        // The descending held carrier can enter the support sensor before Down.
        io.SetInput(InputIo.NgCarrierPickupUp, false);
        AssertState(InspectionStationState.PlacingCarrier);
        io.SetInput(InputIo.NgCarrierPickupDown, true);
        AssertState(InspectionStationState.PlacingCarrier);
        if (destination == NgTransferDestination.Shuttle)
        {
            io.SetInput(InputIo.NgShuttleUp, false);
            io.SetInput(InputIo.NgShuttleDown, false);
            Assert.Equal(InspectionStationState.PreparingTransfer,
                move.GetNextTransferStep(destination, canPickUp: true, holdAtDestination: true));
            AssertState(InspectionStationState.WaitingForDestination);
            io.SetInput(InputIo.NgCarrierDetected, false);
            Assert.Equal(InspectionStationState.PreparingTransfer,
                move.GetNextTransferStep(destination, canPickUp: true, holdAtDestination: true));
            io.SetInput(InputIo.NgCarrierDetected, true);
            io.SetInput(InputIo.NgCarrierPickupDown, false);
            io.SetInput(InputIo.NgCarrierPickupUp, true);
            Assert.Equal(InspectionStationState.HoldingAtDestination,
                move.GetNextTransferStep(destination, canPickUp: true, holdAtDestination: true));
            io.SetInput(InputIo.NgCarrierPickupUp, false);
            io.SetInput(InputIo.NgCarrierGripperClosed, false);
            io.SetInput(InputIo.NgCarrierGripperOpen, true);
            // Unexpected opening above an unsupported destination must retain the failed transfer.
            Assert.Equal(InspectionStationState.PickingCarrier,
                move.GetNextTransferStep(destination, canPickUp: true, holdAtDestination: true));
            io.SetInput(InputIo.NgCarrierPickupDown, true);
            io.SetInput(InputIo.NgCarrierGripperClosed, true);
            io.SetInput(InputIo.NgCarrierGripperOpen, false);
            io.SetInput(InputIo.NgShuttleUp, true);
            var gripperOutput = io.GetOutput(OutputIo.NgCarrierGripperClose);
            Assert.False(await move.ExecuteTransferAsync(
                destination, InspectionStationState.HoldingAtDestination, CancellationToken.None));
            Assert.Equal(gripperOutput, io.GetOutput(OutputIo.NgCarrierGripperClose));
        }
        io.SetInput(InputIo.NgCarrierGripperClosed, false);
        AssertState(InspectionStationState.PlacingCarrier);
        io.SetInput(InputIo.NgCarrierGripperOpen, true);
        var pickup = services.GetRequiredService<InspectionStation>();
        Assert.True(pickup.IsTransferPending);
        using (var stop = new CancellationTokenSource())
        {
            var placing = move.ExecuteTransferAsync(destination, InspectionStationState.PlacingCarrier, stop.Token);
            Assert.False(pickup.IsTransferPending);
            Assert.False(io.GetOutput(OutputIo.NgCarrierGripperClose));
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => placing);
        }
        io.SetInput(destinationSensor, false);
        AssertState(InspectionStationState.PlacingCarrier);
        io.SetInput(destinationSensor, true);
        AssertState(InspectionStationState.PlacingCarrier);
        io.SetInput(InputIo.NgCarrierPickupDown, false);
        io.SetInput(InputIo.NgCarrierPickupUp, true);
        AssertState(InspectionStationState.TransferCompleted);
        await machine.ShutdownAsync();

        void AssertState(InspectionStationState expected)
        {
            Assert.Equal(expected, move.GetNextTransferStep(destination, canPickUp: false));
            Assert.Equal(expected, move.GetNextTransferStep(destination, canPickUp: true));
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private static ServiceProvider CreateDisplayServices(
        out DisplayReadMotion feedback,
        MachineSettings? settings = null)
    {
        var motion = DispatchProxy.Create<IXyMotion, DisplayReadMotion>();
        var probe = (DisplayReadMotion)motion;
        feedback = probe;
        return new ServiceCollection().AddSingleton(_ => VirtualTest.OpenMachineStore())
            .AddIbtmApplication(settings ?? FlowSettings())
            .AddSingleton<IReadOnlyDictionary<MotionGroup, IXyMotion>>(provider =>
            {
                var motions = Enum.GetValues<MotionGroup>().ToDictionary(
                    group => group, group => provider.GetRequiredKeyedService<IXyMotion>(group));
                probe.Motion = motions[MotionGroup.InspectionGantry];
                motions[MotionGroup.InspectionGantry] = motion;
                return motions;
            })
            .BuildServiceProvider();
    }

    private static ServiceProvider CreateServices(MachineSettings settings)
    {
        return new ServiceCollection().AddSingleton(_ => VirtualTest.OpenMachineStore())
            .AddIbtmApplication(settings)
            .BuildServiceProvider(
                new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true, });
    }

    private static Dictionary<OutputIo, TeachingOutputRow> TeachingRows(TeachingViewModel teaching)
    {
        return teaching.TeachingIoGroups.SelectMany(group => group.Outputs)
            .Where(row => row.IsSupported)
            .ToDictionary(row => row.Io.Signal);
    }

    private static ServiceProvider CreateMotionScopeServices(
        MachineSettings settings,
        out Dictionary<MotionGroup, ScopedMotionProbe> probes,
        Action<IServiceCollection>? configure = null)
    {
        var captured = new Dictionary<MotionGroup, ScopedMotionProbe>();
        probes = captured;
        var services = new ServiceCollection().AddSingleton(_ => VirtualTest.OpenMachineStore())
            .AddIbtmApplication(settings)
            .AddSingleton<IReadOnlyDictionary<MotionGroup, IXyMotion>>(provider =>
                Enum.GetValues<MotionGroup>().ToDictionary(group => group,
                    group =>
                    {
                        var motion = DispatchProxy.Create<IXyMotion, ScopedMotionProbe>();
                        var probe = (ScopedMotionProbe)motion;
                        probe.Motion = provider.GetRequiredKeyedService<IXyMotion>(group);
                        captured.Add(group, probe);
                        return motion;
                    }));
        configure?.Invoke(services);
        var provider = services.BuildServiceProvider();
        // These tests replace the handler factories that normally initialize virtual feedback.
        _ = provider.GetRequiredService<VirtualMachine>();
        return provider;
    }

    private static UnitSettings EnableOnly(MachineUnit unit)
    {
        return new()
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
    }

    private static HomeSettings FastHome()
    {
        return new() { SearchSpeed = 10_000 };
    }

    private static void FastHomes(MachineSettings settings)
    {
        foreach (var (motion, _) in settings.MotionSections)
        {
            motion.HorizontalHome = FastHome();
            motion.ZHome = FastHome();
        }
    }

    private static MachineSettings FlowSettings()
    {
        var settings = new MachineSettings();
        settings.PcbSupply.Motion = FastMotion();
        settings.PcbSupply.RotationZ = 0;
        settings.PcbSupply.HandoffPosition = new()
        {
            X = 80,
            Y = 30,
        };
        settings.PcbPlacementHandler.Motion = FastMotion();
        settings.PcbPlacementHandler.ReceiveZ = 12;
        settings.PcbPlacementHandler.HandoffPosition = new()
        {
            X = 80,
            Y = 30,
            Z = 10,
        };
        settings.BoltFastening.Motion = FastMotion();
        settings.BoltFastening.DryRunMilliseconds = 30;
        // Virtual tube passage takes 200 ms after detection, while the blow output remains on.
        settings.BoltFastening.ShootingArrivalDelaySeconds = 0.5;
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
        settings.CarrierReference.LowerRightLocatingPin = new() { X = 100, Y = 100 };
        settings.NgCarrierTransfer.WaitingPosition = new() { X = 5, Y = 20 };
        settings.NgCarrierTransfer.CarrierPickupPosition = new() { X = 5, Y = 20 };
        settings.NgCarrierTransfer.ShuttlePlacePosition = new() { X = 150, Y = 20 };
        return settings;
    }

    private static MotionSettings FastMotion()
    {
        return new()
        {
            HorizontalSpeed = 10_000,
            ZSpeed = 10_000,
            HorizontalHome = FastHome(),
            ZHome = FastHome(),
        };
    }

    private static BoltHeadSettings HeadSettings()
    {
        return new()
        {
            UpperLeftLocatingPin = new() { X = 0, Y = 0 },
            LowerRightLocatingPin = new() { X = 100, Y = 100 },
        };
    }

    private static void PrepareCarrierTeaching(MachineSettings settings, Recipe recipe)
    {
        recipe.PcbSupply.Pcb1PickPosition.Y = 10;
        recipe.PcbSupply.Pcb2PickPosition.Y = 10;
        settings.CarrierReference.UpperLeftLocatingPin = new() { X = 0, Y = 0 };
        settings.CarrierReference.LowerRightLocatingPin = new() { X = 100, Y = 100 };
        settings.BoltFastening.PickupHead = HeadSettings();
        settings.BoltFastening.ShootingHead = HeadSettings();
        recipe.Pcb = new();
        recipe.Pcb.BoltPoints.Add(
            new BoltPoint { Number = 1, Head = FasteningHead.Shooting, X = 10, Y = 10, });
        recipe.Pcb.BoltPoints.Add(
            new BoltPoint { Number = 1, HeatSink = HeatSinkSlot.HeatSink2, Head = FasteningHead.Shooting, X = 28, Y = 10 });
        foreach (var bolt in recipe.Pcb.BoltPoints)
            settings.BoltFastening.InitializeBoltPosition(bolt, settings.CarrierReference);
        TeachInspectionFovs(recipe);
    }

    private static void TeachInspectionFovs(Recipe recipe)
    {
        recipe.CarrierImages = recipe.Pcb.BoltPoints.Select((bolt, index) => new CarrierImageTile
        {
            Number = index + 1,
            Region = new(128, 88, 64, 64),
            BoltNumber = bolt.Number,
            HeatSink = bolt.HeatSink,
        }).ToList();
        foreach (var pcb in Enum.GetValues<HeatSinkSlot>())
        {
            recipe.CarrierImages.Add(new()
            {
                Number = recipe.CarrierImages.Count + 1,
                Center = new() { X = pcb == HeatSinkSlot.HeatSink1 ? 10 : 28, Y = 17 },
                Region = new(180, 40, 80, 80),
                IsBarcode = true,
                HeatSink = pcb,
            });
        }
    }

    public class HomeResultMotion : DispatchProxy
    {
        public HomeResultMotion()
        {
            Result = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public IXyMotion Motion { get; set; } = null!;
        public TaskCompletionSource<bool> Result { get; }
        public int HorizontalHomeCalls { get; private set; }
        public bool AwaitCleanupAfterCancellation { get; set; }
        public Exception? StartFailure { get; set; }
        public TaskCompletionSource Started { get; }
        public CancellationToken HomeCancellation { get; private set; }

        protected override object? Invoke(MethodInfo? method, object?[]? arguments)
        {
            if (method!.Name == nameof(IAxisMotion.HomeAsync)
                && (MotionAxis)arguments![0]! == MotionAxis.Z)
            {
                HomeCancellation = (CancellationToken)arguments[2]!;
                Started.TrySetResult();
                if (StartFailure is not null)
                    throw StartFailure;
                return AwaitCleanupAfterCancellation
                    ? Result.Task
                    : Result.Task.WaitAsync(HomeCancellation);
            }

            if (method.Name == nameof(IXyMotion.HomeHorizontalAsync))
            {
                HorizontalHomeCalls++;
            }

            return method.Invoke(Motion, arguments);
        }
    }

    private sealed class StoppingBoltHead : IBoltHead
    {
        public StoppingBoltHead()
        {
            Started = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Stopping = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Stopped = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public TaskCompletionSource Started { get; }
        public TaskCompletionSource Stopping { get; }
        public TaskCompletionSource Stopped { get; }


        public AdcStatusMonitor? Monitor => null;

        public Task CheckReadyAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task ResetAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task SelectPresetAsync(ushort preset, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }



        public async Task<BoltResult> TightenAsync(
            CancellationToken cancellationToken = default,
            Func<CancellationToken, Task>? feedAsync = null,
            int dryRunMilliseconds = 0)
        {
            Started.SetResult();
            try
            {
                if (feedAsync is not null)
                    await feedAsync(cancellationToken);
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
        public WaitingBoltHead()
        {
            ReadinessEntered = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
            ReadinessReleased = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public bool WaitForReadiness { get; set; }
        public int ReadinessChecks { get; private set; }
        public TaskCompletionSource ReadinessEntered { get; }
        public TaskCompletionSource ReadinessReleased { get; }


        public AdcStatusMonitor? Monitor => null;

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

        public Task SelectPresetAsync(ushort preset, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task ResetAsync(CancellationToken cancellationToken = default)
        {
            return CheckReadyAsync(cancellationToken);
        }

        public Task<BoltResult> TightenAsync(
            CancellationToken cancellationToken = default,
            Func<CancellationToken, Task>? feedAsync = null,
            int dryRunMilliseconds = 0)
        {
            throw new NotSupportedException();
        }


    }

    public class DisplayReadMotion : DispatchProxy, IMotionDiagnostics
    {
        public Action? BeforeRead;
        public Action? BeforePositionRead;
        public Action? BeforeHome;
        public Exception? DiagnosticReadError;
        public Action<MotionAxis>? AfterDiagnosticStateRead;

        public DisplayReadMotion()
        {
            AxisMoves = [];
        }

        public IXyMotion Motion { get; set; } = null!;
        public MotionAxis? LastMovedAxis { get; private set; }
        public double? LastMoveVelocity { get; private set; }
        public List<(MotionAxis Axis, double Position)> AxisMoves { get; }

        public (AxisState? State, Exception? Error) ReadDiagnosticState(MotionAxis axis)
        {
            if (DiagnosticReadError is { } error)
                return (null, error);
            var read = ((IMotionDiagnostics)Motion).ReadDiagnosticState(axis);
            AfterDiagnosticStateRead?.Invoke(axis);
            return read;
        }

        public (double? Position, Exception? Error) ReadDiagnosticPosition(MotionAxis axis)
        {
            return ((IMotionDiagnostics)Motion).ReadDiagnosticPosition(axis);
        }

        protected override object? Invoke(MethodInfo? method, object?[]? arguments)
        {
            if (method!.Name is nameof(IAxisMotion.HomeAsync) or nameof(IXyMotion.HomeHorizontalAsync))
                BeforeHome?.Invoke();
            if (method!.Name == nameof(IMotionFeedback.GetAxisState))
                BeforeRead?.Invoke();
            if (method.Name == $"get_{nameof(IMotionFeedback.Position)}")
                BeforePositionRead?.Invoke();
            if (method.Name == nameof(IAxisMotion.MoveAxisAsync))
            {
                LastMovedAxis = (MotionAxis)arguments![0]!;
                AxisMoves.Add(((MotionAxis)arguments![0]!, (double)arguments[1]!));
            }
            if (method.Name is nameof(IAxisMotion.MoveAxisAsync) or nameof(IXyMotion.MoveToXYAsync))
                LastMoveVelocity = (double)arguments![2]!;
            return method.Invoke(Motion, arguments);
        }
    }

    public class ScopedMotionProbe : DispatchProxy, IMotionDiagnostics
    {
        private bool _initialized;
        public IAxisMotion Motion = null!;
        public bool ReportReady;
        public bool FailHardwareCalls;
        public bool AllowStop;
        public int HardwareCalls;
        public int InitializationCalls;
        public int ResetCalls;
        public Action? BeforeHardwareRead;
        public Func<AxisState, AxisState>? OverrideState;
        public Exception? DiagnosticReadError;

        public (AxisState? State, Exception? Error) ReadDiagnosticState(MotionAxis axis)
        {
            switch (true)
            {
                case true when DiagnosticReadError is { } error:
                    return (null, error);
                case true when FailHardwareCalls:
                    return (null, new IOException("Unavailable diagnostic state."));
            }
            var read = ((IMotionDiagnostics)Motion).ReadDiagnosticState(axis);
            return (read.State is { } state ? OverrideState?.Invoke(state) ?? state : null, read.Error);
        }

        public (double? Position, Exception? Error) ReadDiagnosticPosition(MotionAxis axis)
        {
            switch (true)
            {
                case true when DiagnosticReadError is { } error:
                    return (null, error);
                case true when FailHardwareCalls:
                    return (null, new IOException("Unavailable diagnostic position."));
                default:
                    return ((IMotionDiagnostics)Motion).ReadDiagnosticPosition(axis);
            }
        }

        protected override object? Invoke(MethodInfo? method, object?[]? arguments)
        {
            var name = method!.Name;
            // A disabled device may reject acquisition while still accepting an explicit STOP.
            switch (true)
            {
                case true when name == nameof(IAxisMotion.Stop) && AllowStop:
                    Motion.Stop();
                    return null;
                case true when name == "get_IsReady":
                    BeforeHardwareRead?.Invoke();
                    return ReportReady || _initialized;
            }
            if (!method.IsSpecialName
                || name is "get_IsMoving" or "get_IsMovingHorizontal")
            {
                BeforeHardwareRead?.Invoke();
                Interlocked.Increment(ref HardwareCalls);
                if (name == nameof(IAxisMotion.ResetAsync))
                    Interlocked.Increment(ref ResetCalls);
                if (FailHardwareCalls)
                    throw new IOException($"Unavailable motion: {name}");
            }

            var result = method.Invoke(Motion, arguments);
            if (name == nameof(IAxisMotion.Initialize))
            {
                _initialized = true;
                Interlocked.Increment(ref InitializationCalls);
            }
            if (name == nameof(IMotionFeedback.GetAxisState)
                && OverrideState is { } transform)
                return transform((AxisState)result!);
            return result;
        }
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
}
