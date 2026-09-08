using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
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
        Assert.True(teaching.CaptureInspectionCommand.CanExecute(null));
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
        io.SetInput(InputIo.AutoMode, true);
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
    public void HardwareDefinitionsDriveSettingsAndManualAxisLists()
    {
        var settings = FlowSettings();
        settings.PcbSupplyHardware.Axes[MachineAxis.PcbSupplyY].Maximum = 315;
        using var services = CreateServices(settings);
        var manual = services.GetRequiredService<ManualHardwareViewModel>();
        var view = services.GetRequiredService<SettingsViewModel>();
        Assert.Equal(11, manual.Axes.Length);
        foreach (var section in new (MotionSettings Settings, MotionHardwareSettings Hardware)[]
        {
            (settings.PcbSupply.Motion, settings.PcbSupplyHardware),
            (settings.PcbPlacementHandler.Motion, settings.PcbPlacementHandlerHardware),
            (settings.BoltFastening.Motion, settings.BoltFasteningHardware),
            (settings.InspectionGantry.Motion, settings.InspectionGantryHardware),
        })
        {
            view.SelectedMotionGroup = section.Hardware.Group;
            Assert.Same(section.Settings, view.CurrentMotionSettings);
            Assert.Same(section.Hardware, view.CurrentMotionHardwareSettings);
            Assert.Equal(section.Hardware.GetAxis(MotionAxis.Z) is not null, view.CurrentMotionHasZ);
            foreach (var (axis, signal) in section.Hardware.AxisSignals)
            {
                var row = Assert.Single(manual.Axes, row => row.Signal == signal);
                Assert.Equal(axis, row.Axis);
                Assert.Equal(section.Hardware.Group, row.Group);
            }
        }
        Assert.Equal(315, services.GetRequiredService<PcbSupplyHandler>().Feedback.GetRange(MotionAxis.Y)!.Value.Maximum);
        Assert.False(services.GetRequiredService<InspectionGantry>().Feedback.HasZ);
        var json = JsonSerializer.Serialize(settings.PcbSupplyHardware);
        Assert.DoesNotContain(nameof(MotionHardwareSettings.AxisSignals), json);
        Assert.DoesNotContain(nameof(MotionHardwareSettings.Group), json);
        var loaded = JsonSerializer.Deserialize<PcbSupplyHardwareSettings>(json)!;
        Assert.Equal(MachineAxis.PcbSupplyY, loaded.AxisSignals[MotionAxis.Y]);
        Assert.Equal(315, loaded.GetAxis(MotionAxis.Y)!.Maximum);
    }

    [Fact]
    public void TeachingIoFollowsSelectedUnitAndSharesRelatedSignals()
    {
        using var services = CreateServices(FlowSettings());
        var supply = services.GetRequiredService<SupplyTeachingViewModel>();
        var station = services.GetRequiredService<StationTeachingViewModel>();
        Assert.Equal(new[] { HardwareArea.PcbSupply, HardwareArea.PcbBuffer },
            supply.IoGroups.Select(group => group.Area));
        var buffer = supply.IoGroups.Single(group => group.Area == HardwareArea.PcbBuffer);
        var signals = services.GetRequiredService<IoSignals>();
        Assert.All(supply.IoGroups.SelectMany(group => group.Inputs),
            row => Assert.Same(signals.Inputs[row.Signal], row));
        Assert.All(supply.IoGroups.SelectMany(group => group.Outputs),
            row => Assert.Same(signals.Outputs[row.Signal], row));
        var notifications = 0;
        supply.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(supply.IoGroups)) notifications++;
        };
        supply.SelectedPoint = supply.Points.First(point => point.MotionGroup == MotionGroup.PcbPlacementHandler);
        station.SelectedMotionGroup = MotionGroup.PcbPlacementHandler;
        Assert.True(notifications > 0);
        Assert.Same(station.IoGroups, supply.IoGroups);
        Assert.Same(station.TeachingOutputs, supply.TeachingOutputs);
        Assert.Equal(HardwareArea.PcbPlacementStation, station.IoGroups[0].Area);
        Assert.Same(buffer, supply.IoGroups.Single(group => group.Area == HardwareArea.PcbBuffer));
        Assert.Contains(supply.IoGroups.SelectMany(group => group.Inputs),
            row => row.Signal == InputIo.PcbPlacementCarrierPresent);

        station.SelectedMotionGroup = MotionGroup.BoltFastening;
        Assert.Equal(new[] { HardwareArea.BoltFasteningStation, HardwareArea.BoltFastening, HardwareArea.BoltFeeder },
            station.IoGroups.Select(group => group.Area));
        Assert.Contains(station.IoGroups.SelectMany(group => group.Inputs),
            row => row.Signal == InputIo.BoltFasteningCarrierPresent);
        Assert.DoesNotContain(station.IoGroups.SelectMany(group => group.Inputs),
            row => row.Signal == InputIo.PcbPlacementCarrierPresent);

        station.SelectedMotionGroup = MotionGroup.InspectionGantry;
        Assert.Equal(new[] { HardwareArea.InspectionStation, HardwareArea.NgCarrierTransfer, HardwareArea.NgShuttle },
            station.IoGroups.Select(group => group.Area));
        Assert.Contains(station.IoGroups.SelectMany(group => group.Inputs),
            row => row.Signal == InputIo.InspectionCarrierPresent);
        Assert.DoesNotContain(OutputIo.NgShuttleDown, station.TeachingOutputs.Keys);
        Assert.Equal(new[] { OutputIo.NgCarrierPickupDown, OutputIo.NgCarrierGripperClose, OutputIo.InspectionBackupPlateUp },
            station.TeachingOutputs.Keys);
    }

    [Fact]
    public async Task TeachingOutputsWaitForFeedbackAndCancelWithoutReversingPneumatics()
    {
        using var services = CreateServices(FlowSettings());
        var machine = services.GetRequiredService<MachineController>();
        var teaching = services.GetRequiredService<SupplyTeachingViewModel>();
        var io = services.GetRequiredService<VirtualIoService>();
        var state = services.GetRequiredService<MachineState>();
        var nest = teaching.TeachingOutputs[OutputIo.PcbSupplyNestForward];
        Assert.False(teaching.SetOutputOnCommand.CanExecute(nest));
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        var handler = services.GetRequiredService<PcbSupplyHandler>();
        var rotation = teaching.TeachingOutputs[OutputIo.PcbSupplyRotate];
        await handler.MoveTeachingZAsync(5);
        await teaching.SetOutputOffCommand.ExecuteAsync(rotation);
        Assert.Equal(0, handler.Feedback.GetPosition().Z);
        Assert.Equal(PcbSupplyRotationState.Unrotated, handler.Rotation);
        await teaching.SetOutputOnCommand.ExecuteAsync(rotation);
        await handler.MoveXAsync(80);
        Assert.False(teaching.SetOutputOffCommand.CanExecute(rotation));
        Assert.False(teaching.StepCommand.CanExecute(TeachingDirection.ZPlus));
        await handler.MoveXAsync(0);
        io.AutoResponseEnabled = false;
        Assert.True(teaching.SetOutputOnCommand.CanExecute(nest));

        var pending = teaching.SetOutputOnCommand.ExecuteAsync(nest);
        Assert.True(io.GetOutput(nest.Signal));
        Assert.False(pending.IsCompleted);
        Assert.False(teaching.SetOutputOffCommand.CanExecute(nest));
        Assert.False(teaching.StepCommand.CanExecute(TeachingDirection.XPlus));
        var feedback = io.GetOutputFeedback(nest.Signal)!;
        io.SetInput(feedback.OnInput, true);
        Assert.False(pending.IsCompleted); // Both inputs ON is not completion.
        io.SetInput(feedback.OffInput, false);
        await pending.WaitAsync(TimeSpan.FromSeconds(2));
        await teaching.SetOutputOnCommand.ExecuteAsync(nest); // ON again is allowed.

        teaching.SelectedPoint = teaching.Points.Last(point => point.MotionGroup == MotionGroup.PcbSupply);
        var beforeSelection = handler.Feedback.GetPosition();
        var releasing = teaching.SetOutputOffCommand.ExecuteAsync(nest);
        Assert.False(releasing.IsCompleted);
        teaching.SelectNextPointCommand.Execute(null);
        await releasing.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(MotionGroup.PcbPlacementHandler, teaching.SelectedPoint!.MotionGroup);
        Assert.Equal(beforeSelection, handler.Feedback.GetPosition());
        Assert.False(io.GetOutput(nest.Signal));
        Assert.True(io.GetInput(feedback.OnInput));
        Assert.Equal(MachineAlarm.None, state.Alarm);
        Assert.False(teaching.SetOutputOnCommand.CanExecute(nest));

        var lift = teaching.TeachingOutputs[OutputIo.PcbPlacementHandlerDown];
        var lowering = teaching.SetOutputOnCommand.ExecuteAsync(lift);
        await teaching.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(2));
        await lowering;
        Assert.True(io.GetOutput(lift.Signal));
        Assert.False(services.GetRequiredService<OperationCancellation>().HasActiveOperations);
        Assert.Equal(MachineAlarm.None, state.Alarm);
    }

    [Fact]
    public async Task TeachingOutputsKeepOwnerMovementRulesAndReportFeedbackTimeout()
    {
        var settings = FlowSettings();
        using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var teaching = services.GetRequiredService<StationTeachingViewModel>();
        var io = services.GetRequiredService<VirtualIoService>();
        var state = services.GetRequiredService<MachineState>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        teaching.SelectedMotionGroup = MotionGroup.PcbPlacementHandler;
        var lift = teaching.TeachingOutputs[OutputIo.PcbPlacementHandlerDown];
        await teaching.SetOutputOnCommand.ExecuteAsync(lift);
        Assert.False(teaching.StepCommand.CanExecute(TeachingDirection.XPlus));
        Assert.False(teaching.SetOutputOnCommand.CanExecute(teaching.TeachingOutputs[OutputIo.PcbPlacementHandlerRotate]));
        await teaching.SetOutputOffCommand.ExecuteAsync(lift);
        Assert.True(teaching.StepCommand.CanExecute(TeachingDirection.XPlus));
        var ipm = teaching.TeachingOutputs[OutputIo.PcbPlacementIpmDown];
        await teaching.SetOutputOnCommand.ExecuteAsync(ipm);
        Assert.True(teaching.StepCommand.CanExecute(TeachingDirection.XPlus));

        teaching.SelectedMotionGroup = MotionGroup.BoltFastening;
        Assert.True(teaching.TeachingOutputs[OutputIo.ShootBolt].HoldToRun);
        Assert.DoesNotContain(OutputIo.ShootingEscapeForward, teaching.TeachingOutputs.Keys);
        var pickup = teaching.TeachingOutputs[OutputIo.PickupHeadDown];
        await teaching.SetOutputOnCommand.ExecuteAsync(pickup);
        Assert.False(services.GetRequiredService<BoltFasteningGantry>().CanMoveHorizontal);
        Assert.True(teaching.StepCommand.CanExecute(TeachingDirection.XPlus));
        await teaching.SetOutputOffCommand.ExecuteAsync(pickup);

        teaching.SelectedMotionGroup = MotionGroup.InspectionGantry;
        var ngLift = teaching.TeachingOutputs[OutputIo.NgCarrierPickupDown];
        settings.Units.NgCarrierTransfer = false;
        Assert.False(teaching.SetOutputOnCommand.CanExecute(ngLift));
        settings.Units.NgCarrierTransfer = true;
        io.AutoResponseEnabled = false;
        var pending = teaching.SetOutputOnCommand.ExecuteAsync(ngLift);
        teaching.JogStopCommand.Execute(null);
        await pending.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(MachineAlarm.None, state.Alarm);
        Assert.True(io.GetOutput(ngLift.Signal));

        settings.Options.TimeoutMilliseconds = 50;
        await teaching.SetOutputOnCommand.ExecuteAsync(ngLift);
        Assert.Equal(MachineAlarm.NgCarrierTransfer, state.Alarm);
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
            () => io.SetInput(InputIo.AutoMode, true),
        ];
        foreach (var stop in stopActions)
        {
            teaching.SelectedMotionGroup = MotionGroup.BoltFastening;
            Assert.True(teaching.SetOutputOnCommand.CanExecute(shoot));
            var holding = teaching.SetOutputOnCommand.ExecuteAsync(shoot);
            Assert.True(io.GetOutput(OutputIo.ShootBolt));
            Assert.False(holding.IsCompleted);
            Assert.False(state.ManualOutputsEnabled);
            Assert.False(machine.CanStart);
            stop();
            await holding.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(io.GetOutput(OutputIo.ShootBolt));
            io.SetInput(InputIo.AutoMode, false);
        }
        Assert.All(outputs, output => Assert.Equal(OutputIo.ShootBolt, output));
        Assert.Equal(MachineAlarm.None, state.Alarm);
        io.SetInput(InputIo.AutoMode, true);
        Assert.False(teaching.SetOutputOnCommand.CanExecute(shoot));
    }

    [Fact]
    public async Task StationTeachingControlsOnlyItsOwnBackupPlate()
    {
        var settings = FlowSettings();
        settings.Units.NgCarrierTransfer = false;
        using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var teaching = services.GetRequiredService<StationTeachingViewModel>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        (MotionGroup Group, TeachingTarget Target, InputIo Up, InputIo Down, OutputIo Output)[] stations =
        [
            (MotionGroup.PcbPlacementHandler, TeachingTarget.HeatSink1PcbPlacement,
                InputIo.PcbPlacementBackupPlateUp, InputIo.PcbPlacementBackupPlateDown, OutputIo.PcbPlacementBackupPlateUp),
            (MotionGroup.BoltFastening, TeachingTarget.ShootingHeadUpperLeftLocatingPin,
                InputIo.BoltFasteningBackupPlateUp, InputIo.BoltFasteningBackupPlateDown, OutputIo.BoltFasteningBackupPlateUp),
            (MotionGroup.InspectionGantry, TeachingTarget.CarrierUpperLeftLocatingPin,
                InputIo.InspectionBackupPlateUp, InputIo.InspectionBackupPlateDown, OutputIo.InspectionBackupPlateUp),
        ];
        foreach (var (group, target, up, down, output) in stations)
        {
            teaching.SelectedMotionGroup = group;
            teaching.SelectedPoint = teaching.FilteredPoints.Single(point => point.Target == target);
            var plate = teaching.TeachingOutputs[output];
            Assert.False(teaching.TeachCurrentPositionCommand.CanExecute(null));
            Assert.False(teaching.MoveToPointCommand.CanExecute(null));
            Assert.True(teaching.SetOutputOnCommand.CanExecute(plate));
            await teaching.SetOutputOnCommand.ExecuteAsync(plate);
            Assert.True(io.GetInput(up));
            Assert.False(io.GetInput(down));
            foreach (var other in stations.Where(station => station.Group != group))
            {
                Assert.False(teaching.TeachingOutputs.ContainsKey(other.Output));
                Assert.False(io.GetInput(other.Up));
            }
            await teaching.SetOutputOffCommand.ExecuteAsync(plate);
            Assert.False(io.GetInput(up));
            Assert.True(io.GetInput(down));
        }

        await machine.HomeAsync(CancellationToken.None);
        var supply = services.GetRequiredKeyedService<IAxisMotion>(MotionGroup.PcbSupply);
        var state = services.GetRequiredService<MachineState>();
        await supply.MoveXAsync(settings.PcbSupply.BufferHandoffPosition.X, 1_000);
        teaching.SelectedMotionGroup = MotionGroup.PcbPlacementHandler;
        Assert.True(state.SupplyInBufferArea);
        Assert.False(teaching.SetOutputOnCommand.CanExecute(teaching.TeachingOutputs[OutputIo.PcbPlacementHandlerDown]));
        var placementPlate = teaching.TeachingOutputs[OutputIo.PcbPlacementBackupPlateUp];
        Assert.True(teaching.SetOutputOnCommand.CanExecute(placementPlate));
        await teaching.SetOutputOnCommand.ExecuteAsync(placementPlate);
        await teaching.SetOutputOffCommand.ExecuteAsync(placementPlate);
        supply.SetServo(MotionAxis.X, false);
        Assert.False(state.ManualControlsEnabled);
        Assert.True(teaching.SetOutputOnCommand.CanExecute(placementPlate));
        io.SetInput(InputIo.AutoMode, true);
        Assert.False(teaching.SetOutputOnCommand.CanExecute(placementPlate));
        io.SetInput(InputIo.AutoMode, false);

        teaching.SelectedMotionGroup = MotionGroup.InspectionGantry;
        Assert.False(teaching.SetOutputOnCommand.CanExecute(teaching.TeachingOutputs[OutputIo.NgCarrierPickupDown]));
        settings.Options.TimeoutMilliseconds = 50;
        io.AutoResponseEnabled = false;
        await teaching.SetOutputOnCommand.ExecuteAsync(teaching.TeachingOutputs[OutputIo.InspectionBackupPlateUp]);
        Assert.Equal(MachineAlarm.MainConveyor, state.Alarm);
    }

    [Fact]
    public async Task PinTeachingEnablesCarrierScanWithoutImageReferenceTeaching()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.Inspection);
        settings.CarrierReference = new();
        using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var teaching = services.GetRequiredService<StationTeachingViewModel>();
        var gantry = services.GetRequiredService<InspectionGantry>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        teaching.RecipeEditor.Name = $"PinTeaching-{Guid.NewGuid():N}";
        Assert.False(teaching.CaptureCarrierImagesCommand.CanExecute(null));
        Assert.Equal(TeachingTarget.CarrierUpperLeftLocatingPin, teaching.SelectedPoint!.Target);
        Assert.Equal(TeachingSaveBehavior.CameraCenter, teaching.SaveBehavior);
        Assert.False(teaching.TeachImagePointCommand.CanExecute(new System.Windows.Point(2, 3)));
        Assert.True(teaching.TeachCurrentPositionCommand.CanExecute(null));

        await gantry.MoveToAsync(new() { X = 2, Y = 3 }, 1_000);
        await teaching.TeachCurrentPositionCommand.ExecuteAsync(null);
        Assert.Equal((2, 3), (settings.CarrierReference.UpperLeftLocatingPin!.X,
            settings.CarrierReference.UpperLeftLocatingPin.Y));
        Assert.Equal(TeachingTarget.CarrierLowerRightLocatingPin, teaching.SelectedPoint!.Target);
        Assert.False(teaching.CaptureCarrierImagesCommand.CanExecute(null));

        await gantry.MoveToAsync(new() { X = 32, Y = 23 }, 1_000);
        await teaching.TeachCurrentPositionCommand.ExecuteAsync(null);
        Assert.Equal(new System.Windows.Point(2, 3), teaching.ImageOrigin);
        Assert.Equal(3, teaching.ImageMarkers.Count); // Two pins and the shared barcode at the selected PCB.
        Assert.True(teaching.CaptureCarrierImagesCommand.CanExecute(null));
        teaching.AddBoltPointCommand.Execute(null);
        teaching.IsCameraLive = true;
        Assert.True(teaching.CaptureCarrierImagesCommand.CanExecute(null));
        await teaching.CaptureCarrierImagesCommand.ExecuteAsync(null);
        Assert.False(teaching.IsCameraLive);
        Assert.Null(teaching.CameraError);
        Assert.Equal(9, teaching.CarrierImages.Count);
        Assert.Equal(TeachingTarget.BoltReference, teaching.SelectedPoint!.Target);
        Assert.True(teaching.TeachImagePointCommand.CanExecute(new System.Windows.Point(10, 10)));
        Assert.False(teaching.TeachCurrentPositionCommand.CanExecute(null));

        teaching.SelectedPoint = teaching.FilteredPoints.Single(point => point.Target == TeachingTarget.PcbRegion);
        await teaching.TeachImageRegionCommand.ExecuteAsync(new System.Windows.Rect(4, 5, 16, 18));
        teaching.SelectedPcb = HeatSinkSlot.HeatSink2;
        Assert.False(teaching.TeachImageRegionCommand.CanExecute(System.Windows.Rect.Empty));
        Assert.True(teaching.TeachImagePointCommand.CanExecute(new System.Windows.Point(22, 5)));
        await teaching.TeachImagePointCommand.ExecuteAsync(new System.Windows.Point(22, 5));
        teaching.SelectedPcb = HeatSinkSlot.HeatSink1;
        teaching.SelectedPoint = teaching.FilteredPoints.Single(point => point.Target == TeachingTarget.BoltReference);
        teaching.MillimetersPerPixel = 0.025;
        await teaching.TeachImagePointCommand.ExecuteAsync(new System.Windows.Point(10, 10));
        var store = services.GetRequiredService<RecipeStore>();
        var saved = await store.LoadRecipeAsync(teaching.RecipeEditor.Name);
        Assert.Equal((6, 5), (saved.Pcb.BoltPoints[0].X, saved.Pcb.BoltPoints[0].Y));
        Assert.Equal(new PcbRegion(2, 2, 16, 18), saved.Pcb.GetRegion(HeatSinkSlot.HeatSink1));
        Assert.Equal(new PcbRegion(20, 2, 16, 18), saved.Pcb.GetRegion(HeatSinkSlot.HeatSink2));
        Assert.Single(saved.Pcb.BoltPoints);
        Assert.Equal(0.025, saved.CarrierImageMillimetersPerPixel);
        Assert.Equal(9, saved.CarrierImages.Count);

        await teaching.RecipeEditor.LoadCommand.ExecuteAsync(saved.Name);
        teaching.SelectedPoint = teaching.FilteredPoints.Single(point => point.Target == TeachingTarget.BoltReference);
        Assert.Equal((10, 10), (teaching.SelectedPoint.X, teaching.SelectedPoint.Y));
        teaching.SelectedPcb = HeatSinkSlot.HeatSink2;
        Assert.Equal((28, 10), (teaching.SelectedPoint!.X, teaching.SelectedPoint.Y));
        await teaching.MoveToPointCommand.ExecuteAsync(null);
        Assert.Equal((28, 10, 0), gantry.Feedback.GetPosition());

        teaching.SelectedPcb = HeatSinkSlot.HeatSink1;
        Assert.False(teaching.TeachImagePointCommand.CanExecute(new System.Windows.Point(21, 10)));
        await teaching.TeachImagePointCommand.ExecuteAsync(new System.Windows.Point(28, 10));
        Assert.Equal(HeatSinkSlot.HeatSink2, teaching.SelectedPcb);
        Assert.Equal((6d, 5d), (teaching.SelectedPoint!.Position.Bolt!.Point.X, teaching.SelectedPoint.Position.Bolt.Point.Y));
        Assert.Equal($"{6d:F3}, {5d:F3}", teaching.SelectedPoint.PositionLabel);

        teaching.SelectedPcb = HeatSinkSlot.HeatSink1;
        teaching.SelectedPoint = teaching.FilteredPoints.Single(point => point.Target == TeachingTarget.DataMatrix);
        Assert.False(teaching.TeachImageRegionCommand.CanExecute(new System.Windows.Rect(18, 9, 8, 4)));
        await teaching.TeachImageRegionCommand.ExecuteAsync(new System.Windows.Rect(26, 9, 4, 4));
        Assert.Equal(HeatSinkSlot.HeatSink2, teaching.SelectedPcb);
        Assert.Equal($"{6d:F3}, {6d:F3}", teaching.SelectedPoint!.PositionLabel);
        Assert.Equal((28, 11), (teaching.SelectedPoint.X, teaching.SelectedPoint.Y));
        teaching.SelectedPcb = HeatSinkSlot.HeatSink1;
        Assert.Equal($"{6d:F3}, {6d:F3}", teaching.SelectedPoint!.PositionLabel);
        Assert.Equal((10, 11), (teaching.SelectedPoint.X, teaching.SelectedPoint.Y));
        Assert.Equal(new PcbRegion(4, 4, 4, 4),
            (await store.LoadRecipeAsync(teaching.RecipeEditor.Name)).Pcb.DataMatrix);

        await teaching.TeachImageRegionCommand.ExecuteAsync(new System.Windows.Rect(5, 7, 10, 4));
        Assert.Equal("Data Matrix region must fit inside one camera FOV.", teaching.CameraError);
        Assert.Equal((8, 6), (teaching.CameraFieldOfView!.Value.Width, teaching.CameraFieldOfView.Value.Height));

        teaching.MillimetersPerPixel = 0.05;
        teaching.SelectedPoint = teaching.FilteredPoints.Single(point => point.Target == TeachingTarget.BoltReference);
        var selectedBolt = teaching.SelectedPoint;
        await teaching.CaptureInspectionCommand.ExecuteAsync(null);
        Assert.True(teaching.Preview.HasImage);
        Assert.NotNull(teaching.Preview.Result);
        await teaching.TeachImagePointCommand.ExecuteAsync(new System.Windows.Point(11, 10));
        Assert.Same(selectedBolt, teaching.SelectedPoint);
        Assert.False(teaching.Preview.HasImage);
        Assert.Null(teaching.Preview.Result);

        await teaching.CaptureInspectionCommand.ExecuteAsync(null);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            teaching.Preview.InspectAsync(new CancellationToken(true)));
        teaching.Preview.MinimumMaskPercent = 1;
        services.GetRequiredService<BoltTrainingSettings>().MaskThreshold = 0.6f;
        Assert.True(teaching.Preview.HasImage);
        Assert.Null(teaching.Preview.Result);
        Assert.Null(teaching.Preview.Overlay);
        await teaching.ReinspectImageCommand.ExecuteAsync(null);
        Assert.NotNull(teaching.Preview.Result);
        await teaching.CaptureCarrierImagesCommand.ExecuteAsync(null);
        Assert.Same(selectedBolt, teaching.SelectedPoint);
        Assert.False(teaching.Preview.HasImage);
        Assert.Null(teaching.Preview.Result);

        teaching.SelectedPcb = HeatSinkSlot.HeatSink2;
        teaching.RecipeEditor.NewCommand.Execute(null);
        Assert.Equal(HeatSinkSlot.HeatSink1, teaching.SelectedPcb);
        await teaching.CaptureCarrierImagesCommand.ExecuteAsync(null);
        Assert.Equal(TeachingTarget.PcbRegion, teaching.SelectedPoint!.Target);
        Assert.True(teaching.TeachImageRegionCommand.CanExecute(System.Windows.Rect.Empty));
    }

    [Fact]
    public async Task CarrierScanDeviceFailureStaysAtTheTeachingCommandBoundary()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.Inspection);
        using var services = new ServiceCollection().AddSingleton<RecipeStore>()
            .AddIbtmApplication(settings)
            .AddSingleton<ICamera>(new VirtualCamera(
                () => throw new InvalidOperationException("Camera SDK capture failed."), () => []))
            .BuildServiceProvider();
        var machine = services.GetRequiredService<MachineController>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        var teaching = services.GetRequiredService<StationTeachingViewModel>();
        teaching.RecipeEditor.Name = $"CaptureFailure-{Guid.NewGuid():N}";
        Assert.True(teaching.CaptureCarrierImagesCommand.CanExecute(null));
        await teaching.CaptureCarrierImagesCommand.ExecuteAsync(null);
        Assert.Equal("Camera SDK capture failed.", teaching.CameraError);
        Assert.False(services.GetRequiredService<MachineState>().IsRunning);
    }

    [Fact]
    public async Task FasteningBoltPositionsUseSharedSafeZWithoutDuplicateTeaching()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.BoltFastening);
        settings.BoltFastening.SafeZ = 5;
        using var services = CreateServices(settings);
        services.GetRequiredService<Recipe>().Pcb.BoltPoints =
            [new() { Number = 1, X = 10, Y = 15, Head = FasteningHead.Shooting }];
        var machine = services.GetRequiredService<MachineController>();
        var teaching = services.GetRequiredService<StationTeachingViewModel>();
        var gantry = services.GetRequiredService<BoltFasteningGantry>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        teaching.SelectedMotionGroup = MotionGroup.BoltFastening;
        teaching.SelectedPoint = teaching.FilteredPoints.Single(point =>
            point.Target == TeachingTarget.BoltPosition);
        Assert.Equal(5, teaching.SelectedPoint.Z);
        Assert.True(machine.TeachingReady);
        Assert.True(teaching.MoveToPointCommand.CanExecute(null));
        Assert.False(teaching.TeachCurrentPositionCommand.CanExecute(null));
        Assert.False(teaching.AddBoltPointCommand.CanExecute(null));
        Assert.False(teaching.RemoveBoltPointCommand.CanExecute(null));
        await gantry.MoveZAsync(12);
        await teaching.MoveToPointCommand.ExecuteAsync(null);
        Assert.Equal((10, 15, 5), gantry.Feedback.GetPosition());
        settings.BoltFastening.SafeZ = 7;
        await teaching.MoveToPointCommand.ExecuteAsync(null);
        Assert.Equal((10, 15, 7), gantry.Feedback.GetPosition());
        io.SetInput(InputIo.ShootingHeadUp, false);
        io.SetInput(InputIo.ShootingHeadDown, true);
        Assert.False(teaching.MoveToPointCommand.CanExecute(null));
    }

    [Fact]
    public async Task RecipeEditsRequireIdleManualWhilePhysicalTeachingAlsoRequiresHome()
    {
        using var services = CreateServices(FlowSettings());
        services.GetRequiredService<Recipe>().Pcb.BoltPoints.Add(new BoltPoint
        {
            Number = 1, Head = FasteningHead.Shooting,
            X = 10, Y = 10,
        });
        var teaching = services.GetRequiredService<StationTeachingViewModel>();
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<VirtualIoService>();
        teaching.SelectedMotionGroup = MotionGroup.InspectionGantry;
        teaching.SelectedPoint = teaching.FilteredPoints.Single(point => point.Target == TeachingTarget.BoltReference);
        Assert.False(teaching.CanEditTeaching);
        Assert.True(teaching.AddBoltPointCommand.CanExecute(null));
        Assert.True(teaching.RemoveBoltPointCommand.CanExecute(null));
        Assert.False(teaching.MoveToPointCommand.CanExecute(null));
        teaching.CarrierImages = [new(1, new(), InspectionPreview.CreateBitmap(
            services.GetRequiredService<VirtualCamera>().Capture(500, 0)))];
        Assert.True(teaching.TeachImagePointCommand.CanExecute(new System.Windows.Point(10, 10)));

        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        Assert.True(teaching.CanEditTeaching);
        Assert.True(teaching.AddBoltPointCommand.CanExecute(null));
        Assert.True(teaching.RemoveBoltPointCommand.CanExecute(null));
        teaching.RecipeEditor.Name = "";
        Assert.False(teaching.CaptureCarrierImagesCommand.CanExecute(null));
        Assert.False(teaching.TeachImagePointCommand.CanExecute(new System.Windows.Point(10, 10)));
        teaching.SelectedPoint = teaching.FilteredPoints.Single(point => point.Target == TeachingTarget.PcbRegion);
        Assert.False(teaching.TeachImageRegionCommand.CanExecute(System.Windows.Rect.Empty));
        teaching.RecipeEditor.Name = "Named recipe";
        Assert.True(teaching.TeachImageRegionCommand.CanExecute(System.Windows.Rect.Empty));
        teaching.SelectedPoint = teaching.FilteredPoints.Single(point => point.Target == TeachingTarget.BoltReference);
        using (services.GetRequiredService<OperationCancellation>().Link())
        {
            Assert.False(teaching.CanEditTeaching);
            Assert.False(teaching.AddBoltPointCommand.CanExecute(null));
            Assert.False(teaching.RemoveBoltPointCommand.CanExecute(null));
        }
        io.SetInput(InputIo.AutoMode, true);
        Assert.False(teaching.CanEditTeaching);
        Assert.False(teaching.AddBoltPointCommand.CanExecute(null));
        Assert.False(teaching.RemoveBoltPointCommand.CanExecute(null));
        io.SetInput(InputIo.AutoMode, false);
        Assert.True(teaching.CanEditTeaching);
        services.GetRequiredService<InspectionGantry>().SetServo(MotionAxis.X, false);
        Assert.False(teaching.CanEditTeaching);
        Assert.True(teaching.RemoveBoltPointCommand.CanExecute(null));
        Assert.False(teaching.MoveToPointCommand.CanExecute(null));
    }

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
        using var services = new ServiceCollection()
            .AddSingleton<RecipeStore>()
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

    [Fact]
    public async Task AutomaticAlarmWaitsForHeadStopAndKeepsFirstCause()
    {
        var settings = new MachineSettings
        {
            Home = FastHome(),
            Units = EnableOnly(MachineUnit.BoltFastening),
        };
        var head = new StoppingBoltHead();
        using var services = new ServiceCollection()
            .AddSingleton<RecipeStore>()
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
        io.SetInput(InputIo.AutoMode, true);

        var run = machine.StartAsync();
        await head.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        io.SetInput(InputIo.AirPressureLow, true);
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
        recipe.Pcb.Origins[HeatSinkSlot.HeatSink2] = new() { Y = 8 };
        recipe.Pcb.BoltPoints =
        [
            new() { Number = 1, Head = FasteningHead.Shooting, X = 12, Y = 11 },
            new() { Number = 2, Head = FasteningHead.Pickup, X = 28, Y = 11 },
        ];
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
        recipe.Pcb.BoltPoints =
        [
            new() { Number = 1, X = 10, Y = 10 },
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
            camera.BoltsPresent = false;
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
            Assert.Equal(2, inspection.Assemblies.Count());
            Assert.All(inspection.Assemblies, assembly =>
            {
                Assert.Equal(assembly.HeatSink == HeatSinkSlot.HeatSink1 ? "PCB-1" : "PCB-2", assembly.PcbBarcode);
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
        Assert.Contains(fault.Message, state.AlarmDetail);
        Assert.False(gantry.Feedback.IsMoving);
        Assert.False(io.GetOutput(OutputIo.NgConveyorRun));

        await machine.ResetAsync();
        Assert.Equal(MachineAlarm.None, state.Alarm);
        Assert.Null(state.AlarmDetail);
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
        io.SetInput(InputIo.PickupHeadUp, false);
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
            OutputIo.PcbPlacementHandlerDown,
            OutputIo.PickupHeadDown,
            OutputIo.ShootingHeadDown, OutputIo.NgCarrierPickupDown,
        ];
        await Task.WhenAll(cylinders.Select(output => signals.SetOutputAndWaitAsync(output, true)));
        await signals.SetOutputAndWaitAsync(OutputIo.PcbPlacementIpmDown, true);
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
        Assert.True(io.GetOutput(OutputIo.PcbPlacementIpmDown));
        Assert.True(io.GetInput(InputIo.PcbPlacementIpmDown));
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

    [Theory]
    [InlineData(MotionGroup.PcbPlacementHandler, InputIo.PcbPlacementHandlerUp, InputIo.PcbPlacementHandlerDown)]
    [InlineData(MotionGroup.BoltFastening, InputIo.PickupHeadUp, InputIo.PickupHeadDown)]
    [InlineData(MotionGroup.BoltFastening, InputIo.ShootingHeadUp, InputIo.ShootingHeadDown)]
    public async Task HorizontalMotionRequiresRaisedCylindersWhileZCanRetract(
        MotionGroup group, InputIo up, InputIo down)
    {
        var settings = FlowSettings();
        settings.PcbPlacementHandler.Motion.HorizontalSpeed = 10;
        settings.BoltFastening.Motion.HorizontalSpeed = 10;
        using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var placement = services.GetRequiredService<PcbPlacementHandler>();
        var fastening = services.GetRequiredService<BoltFasteningGantry>();
        var isPlacement = group == MotionGroup.PcbPlacementHandler;
        var feedback = isPlacement ? placement.Feedback : fastening.Feedback;
        Task MoveXY() => isPlacement
            ? placement.MoveToXYAsync(20, 20)
            : fastening.MoveToXYAsync(20, 20);
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        io.AutoResponseEnabled = false;

        io.SetInput(up, false);
        io.SetInput(down, true);
        if (isPlacement) await placement.MoveZAsync(1);
        else await fastening.MoveZAsync(1);
        Assert.Equal(1, feedback.GetPosition().Z);
        Assert.Equal(MachineAlarm.None, state.Alarm);
        await Assert.ThrowsAsync<InvalidOperationException>(MoveXY);
        await Assert.ThrowsAsync<InvalidOperationException>(() => isPlacement
            ? placement.HomeAxisAsync(MotionAxis.X, 100)
            : fastening.HomeAxisAsync(MotionAxis.X, 100));
        if (isPlacement)
            Assert.Throws<InvalidOperationException>(() => placement.Jog(MotionAxis.Y, 10));

        io.SetInput(up, true);
        await Assert.ThrowsAsync<InvalidOperationException>(MoveXY);
        io.SetInput(down, false);
        var move = MoveXY();
        await WaitUntilAsync(() => feedback.IsMovingHorizontal);
        io.SetInput(up, false);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => move);
        Assert.False(feedback.IsMoving);
        Assert.False(feedback.IsMovingHorizontal);
        Assert.Equal(isPlacement ? MachineAlarm.PcbPlacement : MachineAlarm.BoltFastening, state.Alarm);
    }

    [Fact]
    public async Task PlacementIpmDoesNotRestrictHomeOrHorizontalTravel()
    {
        var settings = FlowSettings();
        settings.PcbPlacementHandler.Motion.HorizontalSpeed = 100;
        using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var placement = services.GetRequiredService<PcbPlacementHandler>();
        await machine.InitializeAsync();
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.PcbPlacementIpmDown, true);
        Assert.True(machine.CanHome);
        await machine.HomeAsync(CancellationToken.None);
        Assert.True(state.Homed);
        Assert.True(placement.CanMoveHorizontal);
        Assert.True(io.GetInput(InputIo.PcbPlacementIpmDown));

        var move = placement.MoveToXYAsync(20, 20);
        await WaitUntilAsync(() => placement.Feedback.IsMovingHorizontal);
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.PcbPlacementIpmDown, false);
        await move;

        Assert.Equal((20, 20, settings.PcbPlacementHandler.BufferEntryZ), placement.Feedback.GetPosition());
        Assert.Equal(MachineAlarm.None, state.Alarm);
    }

    [Fact]
    public async Task ManualBoltPickupAndReturnPreserveOrderAndVacuumOnStopAndRetry()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.BoltFastening);
        settings.BoltFastening.SafeZ = 5;
        settings.BoltFastening.PickupPosition = new() { X = 40, Y = 30, Z = 12 };
        using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var gantry = services.GetRequiredService<BoltFasteningGantry>();
        var teaching = services.GetRequiredService<StationTeachingViewModel>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        await gantry.MoveZAsync(14);
        teaching.SelectedMotionGroup = MotionGroup.BoltFastening;
        teaching.SelectedPoint = teaching.FilteredPoints.Single(point => point.Target == TeachingTarget.BoltPickup);
        Assert.Equal(TeachingSaveBehavior.BoltPickup, teaching.SaveBehavior);
        Assert.True(teaching.MoveToPointCommand.CanExecute(null));

        io.AutoResponseEnabled = false;
        io.SetOutput(OutputIo.PickupHeadVacuumPump, true);
        var vacuumChanged = false;
        var movedXyWithHeadDown = false;
        var loweredAt = new ConcurrentQueue<(double X, double Y, double Z)>();
        io.OutputChanged += (output, value) =>
        {
            if (output is OutputIo.PickupHeadVacuumPump or OutputIo.ShootingHeadVacuumPump)
                vacuumChanged = true;
            if (output == OutputIo.PickupHeadDown && value)
                loweredAt.Enqueue(gantry.Feedback.GetPosition());
        };
        gantry.Feedback.PositionChanged += (_, _, _) =>
            movedXyWithHeadDown |= gantry.Feedback.IsMovingHorizontal && !gantry.CanMoveHorizontal;

        var move = teaching.MoveToPointCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => io.GetOutput(OutputIo.PickupHeadDown));
        Assert.Equal((40, 30, 5), gantry.Feedback.GetPosition());
        Assert.False(move.IsCompleted);
        Assert.False(io.GetInput(InputIo.PickupHeadDown));
        teaching.JogStopCommand.Execute(null);
        await move.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal((40, 30, 5), gantry.Feedback.GetPosition());
        Assert.False(gantry.Feedback.IsMoving);
        Assert.True(io.GetOutput(OutputIo.PickupHeadDown)); // Stop keeps pneumatic outputs.

        var retry = teaching.MoveToPointCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => io.GetOutput(OutputIo.PickupHeadDown));
        Assert.False(retry.IsCompleted);
        io.SetInput(InputIo.PickupHeadUp, false);
        io.SetInput(InputIo.PickupHeadDown, true);
        await retry.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal((40, 30, 12), gantry.Feedback.GetPosition());
        Assert.Equal(BoltCylinderState.Down, gantry.PickupHeadPosition);
        Assert.Equal(BoltCylinderState.Up, gantry.ShootingHeadPosition);
        Assert.True(teaching.ReturnFromPickupCommand.CanExecute(null));
        var stopAtSafeZ = true;
        gantry.Feedback.PositionChanged += (_, _, z) =>
        {
            if (stopAtSafeZ && Math.Abs(z - settings.BoltFastening.SafeZ) <= MotionService.PositionToleranceMillimeters)
            {
                stopAtSafeZ = false;
                teaching.JogStopCommand.Execute(null);
            }
        };
        await teaching.ReturnFromPickupCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(stopAtSafeZ);
        Assert.Equal((40, 30, 5), gantry.Feedback.GetPosition());
        Assert.True(io.GetOutput(OutputIo.PickupHeadDown)); // Cancellation must not advance to Head Up.
        Assert.False(gantry.Feedback.IsMoving);

        var returning = teaching.ReturnFromPickupCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => !io.GetOutput(OutputIo.PickupHeadDown));
        Assert.Equal((40, 30, 5), gantry.Feedback.GetPosition());
        Assert.False(returning.IsCompleted); // Up DO alone is not completion.
        io.SetInput(InputIo.PickupHeadDown, false);
        io.SetInput(InputIo.PickupHeadUp, true);
        await returning.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(gantry.CanMoveHorizontal);
        Assert.True(teaching.MoveToPointCommand.CanExecute(null));
        Assert.True(io.GetOutput(OutputIo.PickupHeadVacuumPump));
        Assert.False(io.GetOutput(OutputIo.ShootingHeadVacuumPump));
        Assert.False(vacuumChanged);
        Assert.False(movedXyWithHeadDown);
        Assert.Equal(2, loweredAt.Count);
        Assert.All(loweredAt, position => Assert.Equal((40, 30, 5), position));
        Assert.Equal(MachineAlarm.None, state.Alarm);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManualPickupAndReturnReportCylinderTimeout(bool returning)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.BoltFastening);
        using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var gantry = services.GetRequiredService<BoltFasteningGantry>();
        var teaching = services.GetRequiredService<StationTeachingViewModel>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        teaching.SelectedMotionGroup = MotionGroup.BoltFastening;
        teaching.SelectedPoint = teaching.FilteredPoints.Single(point => point.Target == TeachingTarget.BoltPickup);
        if (returning) await gantry.MoveToPickupPositionAsync();
        io.AutoResponseEnabled = false;
        settings.Options.TimeoutMilliseconds = 50;

        await (returning ? teaching.ReturnFromPickupCommand : teaching.MoveToPointCommand).ExecuteAsync(null);

        Assert.Equal(MachineAlarm.BoltFastening, state.Alarm);
        Assert.False(gantry.Feedback.IsMoving);
        Assert.Equal(settings.BoltFastening.SafeZ, gantry.Feedback.GetPosition().Z);
        Assert.False(io.GetOutput(OutputIo.PickupHeadVacuumPump));
        Assert.False(io.GetOutput(OutputIo.ShootingHeadVacuumPump));
    }

    [Fact]
    public async Task FasteningTeachingAdjustsOneAxisWithHeadsDownAndPreservesPositioningRules()
    {
        using var services = CreateServices(FlowSettings());
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var gantry = services.GetRequiredService<BoltFasteningGantry>();
        var teaching = services.GetRequiredService<StationTeachingViewModel>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        await gantry.MoveToXYAsync(20, 20);
        await gantry.MoveZAsync(10);
        await Task.WhenAll(
            ((IIoService)io).SetOutputAndWaitAsync(OutputIo.PickupHeadDown, true),
            ((IIoService)io).SetOutputAndWaitAsync(OutputIo.ShootingHeadDown, true));
        teaching.SelectedMotionGroup = MotionGroup.BoltFastening;
        teaching.SelectedPoint = teaching.FilteredPoints.Single(point => point.Target == TeachingTarget.BoltPickup);
        teaching.JogSpeed = 1;
        teaching.StepDistance = 0.1;
        Assert.False(teaching.MoveToPointCommand.CanExecute(null));
        Assert.True(teaching.TeachCurrentPositionCommand.CanExecute(null));
        Assert.Equal(HomeBlockReason.FasteningNotRaised, machine.HomeBlock);
        Assert.True(teaching.StepCommand.CanExecute(TeachingDirection.XPlus));

        await teaching.StepCommand.ExecuteAsync(TeachingDirection.XPlus);
        await teaching.StepCommand.ExecuteAsync(TeachingDirection.YMinus);
        var adjusted = gantry.Feedback.GetPosition();
        Assert.Equal(20.1, adjusted.X, 6);
        Assert.Equal(19.9, adjusted.Y, 6);
        Assert.Equal(10, adjusted.Z);
        var jog = teaching.JogCommand.ExecuteAsync(TeachingDirection.XPlus);
        await WaitUntilAsync(() => gantry.Feedback.GetPosition().X > 20.1);
        Assert.Equal(MotionCommand.Adjustment, gantry.Feedback.Command);
        teaching.JogStopCommand.Execute(null);
        await jog.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(MotionCommand.None, gantry.Feedback.Command);
        var stopped = gantry.Feedback.GetPosition();
        await Task.Delay(30);
        Assert.Equal(stopped, gantry.Feedback.GetPosition());
        Assert.Equal(adjusted.Y, stopped.Y);
        Assert.Equal(10, stopped.Z);
        Assert.True(io.GetInput(InputIo.PickupHeadDown));
        Assert.True(io.GetInput(InputIo.ShootingHeadDown));
        Assert.False(state.IsRunning);
        Assert.Equal(MachineAlarm.None, state.Alarm);
        await Assert.ThrowsAsync<InvalidOperationException>(() => gantry.MoveToXYAsync(30, 30));
        await Assert.ThrowsAsync<InvalidOperationException>(() => gantry.HomeHorizontalAsync(100));

        var maximum = gantry.Feedback.GetRange(MotionAxis.X)!.Value.Maximum;
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => gantry.AdjustAxisAsync(MotionAxis.X, maximum + 1, 100));
        Assert.Equal(MotionCommand.None, gantry.Feedback.Command);
        await gantry.AdjustAxisAsync(MotionAxis.X, maximum - 0.1, 1000);
        await gantry.JogAsync(MotionAxis.X, 10).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal((maximum, stopped.Y, stopped.Z), gantry.Feedback.GetPosition());
        Assert.False(teaching.StepCommand.CanExecute(TeachingDirection.XPlus));

        await gantry.RaiseCylindersAsync();
        Assert.True(teaching.MoveToPointCommand.CanExecute(null));
        MotionCommand positioning = MotionCommand.None;
        gantry.Feedback.MovingChanged += moving =>
        {
            if (moving) positioning = gantry.Feedback.Command;
        };
        await gantry.MoveToXYAsync(maximum - 1, stopped.Y);
        Assert.Equal(MotionCommand.Positioning, positioning);
        Assert.Equal(MotionCommand.None, gantry.Feedback.Command);

        var fail = true;
        gantry.Feedback.PositionChanged += (_, _, _) =>
        {
            if (!fail) return;
            fail = false;
            throw new MotionException("Injected teaching move", new IOException());
        };
        await teaching.JogCommand.ExecuteAsync(TeachingDirection.XMinus);
        Assert.Equal(MachineAlarm.MotionUnavailable, state.Alarm);
        Assert.False(gantry.Feedback.IsMoving);
        Assert.Equal(MotionCommand.None, gantry.Feedback.Command);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FasteningAdjustmentStopsOnModeOrServoLoss(bool autoMode)
    {
        using var services = CreateServices(FlowSettings());
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<VirtualIoService>();
        var gantry = services.GetRequiredService<BoltFasteningGantry>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        await gantry.MoveZAsync(10);
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.ShootingHeadDown, true);
        var jog = gantry.JogAsync(MotionAxis.X, 1);
        await WaitUntilAsync(() => gantry.Feedback.GetPosition().X > 0);
        if (autoMode) io.SetInput(InputIo.AutoMode, true);
        else gantry.SetServo(MotionAxis.X, false);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => jog.WaitAsync(TimeSpan.FromSeconds(2)));
        var stopped = gantry.Feedback.GetPosition();
        Assert.Equal(MotionCommand.None, gantry.Feedback.Command);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gantry.AdjustAxisAsync(MotionAxis.X, 20, 1));
        Assert.Equal(stopped, gantry.Feedback.GetPosition());
        Assert.True(io.GetInput(InputIo.ShootingHeadDown));
    }

    [Fact]
    public async Task CylinderFeedbackIsRecheckedBetweenTravelZAndXy()
    {
        var settings = FlowSettings();
        using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var gantry = services.GetRequiredService<BoltFasteningGantry>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        await gantry.MoveZAsync(10);
        var before = gantry.Feedback.GetPosition();
        io.AutoResponseEnabled = false;
        gantry.Feedback.MovingChanged += moving =>
        {
            if (moving && !gantry.Feedback.IsMovingHorizontal)
                io.SetInput(InputIo.PickupHeadUp, false);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            gantry.MoveToXYAsync(20, 20));

        var after = gantry.Feedback.GetPosition();
        Assert.Equal(before.X, after.X);
        Assert.Equal(before.Y, after.Y);
        Assert.Equal(settings.BoltFastening.SafeZ, after.Z);
        Assert.False(gantry.Feedback.IsMoving);
        Assert.Equal(MachineAlarm.BoltFastening, state.Alarm);
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
            .AddSingleton<RecipeStore>()
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopOrFailureDuringFirstUnitOutputPreventsLaterStarts(bool failure)
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
                if (failure) throw error;
                machine.Stop();
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
        settings.Units.MainConveyor = true;
        settings.BoltFeeder.ShootingTimeoutMilliseconds = 50;
        using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();

        await machine.InitializeAsync();
        io.SetInput(InputIo.AutoMode, true);
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
        io.SetInput(InputIo.AutoMode, true);
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
    [InlineData(InputIo.PickupHeadUp, false)]
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
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task FailedHomeReportsCauseAndStopsOtherHomingAxes(bool exception, bool individual)
    {
        var settings = FlowSettings();
        settings.Home.ZSpeed = 1;
        HomeResultMotion? homeResult = null;
        using var services = new ServiceCollection()
            .AddSingleton<RecipeStore>()
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
        var manual = services.GetRequiredService<ManualHardwareViewModel>();
        await machine.InitializeAsync();
        await placement.MoveZAsync(50, 10_000);

        var homing = individual
            ? manual.HomeAxisCommand.ExecuteAsync(manual.Axes.Single(row => row.Signal == MachineAxis.BoltFasteningZ))
            : machine.HomeAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => individual ? state.IsHoming : placement.IsMoving && supply.IsMoving);
            if (exception)
            {
                homeResult!.Result.SetException(new MotionException(
                    "Home", new InvalidOperationException("Home command failed.")));
            }
            else
            {
                homeResult!.Result.SetResult(false);
            }
            await homing.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(MachineAlarm.HomeFailed, state.Alarm);
            if (exception) Assert.Contains("Home command failed.", state.AlarmDetail);
            Assert.False(manual.HomeAxisCommand.CanExecute(
                manual.Axes.Single(row => row.Signal == MachineAxis.BoltFasteningZ)));
            Assert.False(state.IsHoming);
            Assert.False(placement.IsMoving);
            Assert.False(supply.IsMoving);
            Assert.False(placement.GetAxisState(MotionAxis.Z).Homed);
            Assert.Equal(0, homeResult.HorizontalHomeCalls);
        }
        finally
        {
            manual.HomeAxisCommand.Cancel();
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
        public TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
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
            Started.SetResult();
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
            .AddSingleton<RecipeStore>()
            .AddIbtmApplication(settings, new Recipe { Pcb = VirtualTest.TaughtPcbLayout() })
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
        recipe.Pcb = VirtualTest.TaughtPcbLayout();
        recipe.Pcb.BoltPoints.Add(new BoltPoint
        {
            Number = 1,
            Head = FasteningHead.Shooting,
            X = 10,
            Y = 10,
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
