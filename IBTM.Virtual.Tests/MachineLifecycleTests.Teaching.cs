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
    public async Task ManualTeachingAndCameraDoNotRequireUnrelatedMotionReadiness()
    {
        using var services = CreateServices(FlowSettings());
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var teaching = services.GetRequiredService<StationTeachingViewModel>();
        var unrelated = (VirtualMotionService)services.GetRequiredKeyedService<IAxisMotion>(
            MotionGroup.PcbSupply);
        var inspection = (VirtualMotionService)services.GetRequiredKeyedService<IXyMotion>(
            MotionGroup.InspectionGantry);
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        teaching.SelectedMotionGroup = MotionGroup.InspectionGantry;
        try
        {
            unrelated.SetAlarm(MotionAxis.X, true);
            typeof(MachineState).GetMethod("SetError", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(
                state,
                [MachineAlarm.MotionUnavailable, new IOException("Supply axis alarm.")]);
            await WaitUntilAsync(() => teaching.StepCommand.CanExecute(TeachingDirection.XPlus));
            var before = inspection.GetPosition();
            await teaching.StepCommand.ExecuteAsync(TeachingDirection.XPlus);
            Assert.Equal(before.X + teaching.StepDistance, inspection.GetPosition().X, 3);
            Assert.Equal(MachineAlarm.MotionUnavailable, state.Alarm);

            var monitor = services.GetRequiredService<MotionWindowViewModel>();
            var axis = monitor.Axes.Single(
                row => row.Group == MotionGroup.InspectionGantry && row.Axis == MotionAxis.X);
            await WaitUntilAsync(() => monitor.HomeAxisCommand.CanExecute(axis));
            await monitor.HomeAxisCommand.ExecuteAsync(axis);
            Assert.True(inspection.GetAxisState(MotionAxis.X).Homed);
            Assert.Equal(MachineAlarm.MotionUnavailable, state.Alarm);

            inspection.SetServo(MotionAxis.X, false);
            teaching.SelectedPoint = teaching.FilteredPoints.Single(
                point => point.Target == TeachingTarget.CarrierUpperLeftLocatingPin);
            var taughtPoint = teaching.SelectedPoint;
            await WaitUntilAsync(() => teaching.TeachCurrentPositionCommand.CanExecute(null));
            Assert.False(teaching.StepCommand.CanExecute(TeachingDirection.XPlus));
            await teaching.TeachCurrentPositionCommand.ExecuteAsync(null);
            Assert.Equal(inspection.GetPosition().X, taughtPoint.X, 3);

            Assert.True(teaching.ToggleLiveViewCommand.CanExecute(null));
        }
        finally
        {
            await teaching.ShutdownAsync();
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TeachingRechecksFeedbackBeforeJogOrSavingPosition(bool savePosition)
    {
        using var services = CreateDisplayServices(out var feedback);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var teaching = services.GetRequiredService<StationTeachingViewModel>();
        teaching.SelectedMotionGroup = MotionGroup.InspectionGantry;
        teaching.SelectedPoint = teaching.FilteredPoints.Single(
            point => point.Target == TeachingTarget.CarrierUpperLeftLocatingPin);
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        await WaitUntilAsync(() => teaching.TeachCurrentPositionCommand.CanExecute(null));
        var point = teaching.SelectedPoint;
        var before = (point.X, point.Y, point.Z);
        if (savePosition)
            feedback.BeforePositionRead = () => throw new IOException("Teaching feedback read failed.");
        else
            feedback.BeforeRead = () => throw new IOException("Teaching feedback read failed.");

        if (savePosition)
            await teaching.TeachCurrentPositionCommand.ExecuteAsync(null);
        else
            await teaching.JogCommand.ExecuteAsync(TeachingDirection.XPlus);

        Assert.Equal(
            savePosition ? MachineAlarm.IoCommunication : MachineAlarm.NgCarrierTransfer,
            state.Alarm);
        Assert.Contains("Teaching feedback read failed.", state.AlarmDetail);
        Assert.Equal(before, (point.X, point.Y, point.Z));
        Assert.False(teaching.Motion.IsMoving);
        Assert.False(services.GetRequiredService<OperationCancellation>().HasActiveOperations);
    }

    [Theory]
    [InlineData(TeachingDirection.XPlus, nameof(IAxisMotion.MoveXAsync), 10.1, 20)]
    [InlineData(TeachingDirection.YPlus, nameof(IAxisMotion.MoveYAsync), 10, 20.1)]
    public async Task InspectionTeachingStepMovesOnlyTheSelectedAxis(
        TeachingDirection direction,
        string expectedMove,
        double x,
        double y)
    {
        using var services = CreateDisplayServices(out var feedback);
        var machine = services.GetRequiredService<MachineController>();
        var gantry = services.GetRequiredService<InspectionGantry>();
        var teaching = services.GetRequiredService<StationTeachingViewModel>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        await gantry.MoveToAsync(new() { X = 10, Y = 20 }, 10_000);
        teaching.StepDistance = 0.1;
        await WaitUntilAsync(() => teaching.StepCommand.CanExecute(direction));

        await teaching.StepCommand.ExecuteAsync(direction);

        Assert.Equal(expectedMove, feedback.LastMove);
        Assert.Equal((x, y, 0), gantry.Feedback.GetPosition());

        await services.GetRequiredService<IIoService>()
            .SetOutputAndWaitAsync(OutputIo.NgCarrierPickupDown, true);
        await WaitUntilAsync(() => !teaching.StepCommand.CanExecute(direction));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => gantry.MoveAxisAsync(MotionAxis.X, 30, 1_000));
        Assert.Equal((x, y, 0), gantry.Feedback.GetPosition());
    }

    [Theory]
    [InlineData(MotionGroup.PcbSupply)]
    [InlineData(MotionGroup.PcbPlacementHandler)]
    [InlineData(MotionGroup.InspectionGantry)]
    public async Task TeachingJogStopsWhenTeachingContextChanges(MotionGroup group)
    {
        using var services = CreateServices(FlowSettings());
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        TeachingMotionViewModel teaching;
        IMotionFeedback feedback;
        if (group == MotionGroup.PcbSupply)
        {
            teaching = services.GetRequiredService<SupplyTeachingViewModel>();
            feedback = services.GetRequiredService<PcbSupplyHandler>().Feedback;
        }
        else
        {
            var station = services.GetRequiredService<StationTeachingViewModel>();
            station.SelectedMotionGroup = group;
            teaching = station;
            feedback = group == MotionGroup.PcbPlacementHandler
                ? services.GetRequiredService<PcbPlacementHandler>().Feedback
                : services.GetRequiredService<InspectionGantry>().Feedback;
        }

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
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task TeachingCaptureStopsOnModeChangeOrViewShutdown(
        bool scanCarrier,
        bool closeTeaching)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.Inspection);
        settings.InspectionGantry.Motion.HorizontalSpeed = 1;
        using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var gantry = services.GetRequiredService<InspectionGantry>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        await gantry.MoveToAsync(new() { X = 50, Y = 50 }, 10_000);
        var teaching = services.GetRequiredService<StationTeachingViewModel>();
        teaching.RecipeEditor.Name = $"CancelledScan-{Guid.NewGuid():N}";
        teaching.SelectedPoint = teaching.FilteredPoints.Single(
            point => point.Target == TeachingTarget.DataMatrix);
        var selected = teaching.SelectedPoint;
        var command = scanCarrier ? teaching.CaptureCarrierImagesCommand : teaching.CaptureInspectionCommand;
        await WaitUntilAsync(() => command.CanExecute(null));
        var capture = command.ExecuteAsync(null);
        try
        {
            await WaitUntilAsync(() => gantry.Feedback.IsMoving);
            if (closeTeaching)
                await teaching.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(2));
            else
                io.SetInput(InputIo.AutoMode, false);
            await capture.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(gantry.Feedback.IsMoving);
            Assert.False(services.GetRequiredService<OperationCancellation>().HasActiveOperations);
            Assert.Same(selected, teaching.SelectedPoint);
            Assert.False(teaching.Preview.HasImage);
            Assert.Null(teaching.CameraError);
            Assert.Equal(MachineAlarm.None, services.GetRequiredService<MachineState>().Alarm);

            io.SetInput(InputIo.AutoMode, true);
            settings.InspectionGantry.Motion.HorizontalSpeed = 10_000;
            await WaitUntilAsync(() => command.CanExecute(null));
            await command.ExecuteAsync(null);
            Assert.Null(teaching.CameraError);
            Assert.True(scanCarrier ? teaching.HasCarrierImages : teaching.Preview.HasImage);

            await gantry.MoveToAsync(new() { X = 50, Y = 50 }, 10_000);
            settings.InspectionGantry.Motion.HorizontalSpeed = 1;
            teaching.SelectedPoint = selected;
            await WaitUntilAsync(() => command.CanExecute(null));
            capture = command.ExecuteAsync(null);
            await WaitUntilAsync(() => gantry.Feedback.IsMoving);
            await machine.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(2));
            await capture.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(gantry.Feedback.IsMoving);
            Assert.False(services.GetRequiredService<OperationCancellation>().HasActiveOperations);
            Assert.Null(teaching.CameraError);
        }
        finally
        {
            machine.Stop();
            await capture.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task TeachingOutputsWaitForFeedbackAndCancelWithoutReversingPneumatics()
    {
        using var services = CreateServices(FlowSettings());
        var machine = services.GetRequiredService<MachineController>();
        var teaching = services.GetRequiredService<SupplyTeachingViewModel>();
        var io = services.GetRequiredService<VirtualIoService>();
        var state = services.GetRequiredService<MachineState>();
        var gripper = teaching.TeachingOutputs[OutputIo.PcbSupplyGripperClosed];
        await WaitUntilAsync(() => !teaching.SetOutputOnCommand.CanExecute(gripper));
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
        await WaitUntilAsync(() => !teaching.SetOutputOffCommand.CanExecute(rotation));
        await WaitUntilAsync(() => !teaching.StepCommand.CanExecute(TeachingDirection.ZPlus));
        await handler.MoveXAsync(0);
        io.AutoResponseEnabled = false;
        await WaitUntilAsync(() => teaching.SetOutputOnCommand.CanExecute(gripper));

        var pending = teaching.SetOutputOnCommand.ExecuteAsync(gripper);
        Assert.True(io.GetOutput(gripper.Signal));
        Assert.False(pending.IsCompleted);
        await WaitUntilAsync(() => !teaching.SetOutputOffCommand.CanExecute(gripper));
        Assert.True(state.IsRunning); // The feedback wait owns a machine operation.
        var feedback = io.GetOutputFeedback(gripper.Signal)!;
        io.SetInput(feedback.OnInput, true);
        Assert.False(pending.IsCompleted); // Both inputs ON is not completion.
        io.SetInput(feedback.OffInput, false);
        await pending.WaitAsync(TimeSpan.FromSeconds(2));
        await teaching.SetOutputOnCommand.ExecuteAsync(gripper); // ON again is allowed.

        teaching.SelectedPoint = teaching.Points.Last(
            point => point.MotionGroup == MotionGroup.PcbSupply);
        var beforeSelection = handler.Feedback.GetPosition();
        var releasing = teaching.SetOutputOffCommand.ExecuteAsync(gripper);
        Assert.False(releasing.IsCompleted);
        teaching.SelectNextPointCommand.Execute(null);
        await releasing.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(MotionGroup.PcbPlacementHandler, teaching.SelectedPoint!.MotionGroup);
        Assert.Equal(beforeSelection, handler.Feedback.GetPosition());
        Assert.False(io.GetOutput(gripper.Signal));
        Assert.True(io.GetInput(feedback.OnInput));
        Assert.Equal(MachineAlarm.None, state.Alarm);
        await WaitUntilAsync(() => !teaching.SetOutputOnCommand.CanExecute(gripper));

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
        await WaitUntilAsync(() => !teaching.StepCommand.CanExecute(TeachingDirection.XPlus));
        await WaitUntilAsync(
            () => !teaching.SetOutputOnCommand.CanExecute(
                teaching.TeachingOutputs[OutputIo.PcbPlacementHandlerRotate]));
        await teaching.SetOutputOffCommand.ExecuteAsync(lift);
        await WaitUntilAsync(() => teaching.StepCommand.CanExecute(TeachingDirection.XPlus));
        var ipm = teaching.TeachingOutputs[OutputIo.PcbPlacementIpmDown];
        await teaching.SetOutputOnCommand.ExecuteAsync(ipm);
        await WaitUntilAsync(() => teaching.StepCommand.CanExecute(TeachingDirection.XPlus));

        teaching.SelectedMotionGroup = MotionGroup.BoltFastening;
        Assert.True(teaching.TeachingOutputs[OutputIo.ShootBolt].HoldToRun);
        Assert.DoesNotContain(OutputIo.ShootingEscapeForward, teaching.TeachingOutputs.Keys);
        var pickup = teaching.TeachingOutputs[OutputIo.PickupHeadDown];
        await teaching.SetOutputOnCommand.ExecuteAsync(pickup);
        Assert.False(services.GetRequiredService<BoltFasteningGantry>().CanMoveHorizontal);
        await WaitUntilAsync(() => teaching.StepCommand.CanExecute(TeachingDirection.XPlus));
        await teaching.SetOutputOffCommand.ExecuteAsync(pickup);

        teaching.SelectedMotionGroup = MotionGroup.InspectionGantry;
        var ngLift = teaching.TeachingOutputs[OutputIo.NgCarrierPickupDown];
        settings.Units.NgCarrierTransfer = false;
        await WaitUntilAsync(() => teaching.SetOutputOnCommand.CanExecute(ngLift));
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
    public async Task StationTeachingControlsOnlyItsOwnBackupPlate()
    {
        var settings = FlowSettings();
        settings.Units.NgCarrierTransfer = false;
        using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var teaching = services.GetRequiredService<StationTeachingViewModel>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        (MotionGroup Group, TeachingTarget Target, InputIo Up, InputIo Down, OutputIo Output)[] stations = [
            (
                MotionGroup.PcbPlacementHandler,
                TeachingTarget.HeatSink1PcbPlacement,
                InputIo.PcbPlacementBackupPlateUp,
                InputIo.PcbPlacementBackupPlateDown,
                OutputIo.PcbPlacementBackupPlateUp),
            (
                MotionGroup.BoltFastening,
                TeachingTarget.ShootingHeadUpperLeftLocatingPin,
                InputIo.BoltFasteningBackupPlateUp,
                InputIo.BoltFasteningBackupPlateDown,
                OutputIo.BoltFasteningBackupPlateUp),
            (
                MotionGroup.InspectionGantry,
                TeachingTarget.CarrierUpperLeftLocatingPin,
                InputIo.InspectionBackupPlateUp,
                InputIo.InspectionBackupPlateDown,
                OutputIo.InspectionBackupPlateUp),
        ];
        foreach (var (group, target, up, down, output) in stations)
        {
            teaching.SelectedMotionGroup = group;
            teaching.SelectedPoint = teaching.FilteredPoints.Single(point => point.Target == target);
            var plate = teaching.TeachingOutputs[output];
            await WaitUntilAsync(() => !teaching.TeachCurrentPositionCommand.CanExecute(null));
            await WaitUntilAsync(() => !teaching.MoveToPointCommand.CanExecute(null));
            await WaitUntilAsync(() => teaching.SetOutputOnCommand.CanExecute(plate));
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
        await WaitUntilAsync(
            () => !teaching.SetOutputOnCommand.CanExecute(
                teaching.TeachingOutputs[OutputIo.PcbPlacementHandlerDown]));
        var placementPlate = teaching.TeachingOutputs[OutputIo.PcbPlacementBackupPlateUp];
        await WaitUntilAsync(() => teaching.SetOutputOnCommand.CanExecute(placementPlate));
        await teaching.SetOutputOnCommand.ExecuteAsync(placementPlate);
        await teaching.SetOutputOffCommand.ExecuteAsync(placementPlate);
        supply.SetServo(MotionAxis.X, false);
        Assert.False(state.ManualControlsEnabled);
        await WaitUntilAsync(() => teaching.SetOutputOnCommand.CanExecute(placementPlate));
        io.SetInput(InputIo.AutoMode, false);
        await WaitUntilAsync(() => !teaching.SetOutputOnCommand.CanExecute(placementPlate));
        io.SetInput(InputIo.AutoMode, true);

        teaching.SelectedMotionGroup = MotionGroup.InspectionGantry;
        await WaitUntilAsync(
            () => !teaching.SetOutputOnCommand.CanExecute(
                teaching.TeachingOutputs[OutputIo.NgCarrierPickupDown]));
        settings.Options.TimeoutMilliseconds = 50;
        io.AutoResponseEnabled = false;
        await teaching.SetOutputOnCommand.ExecuteAsync(
            teaching.TeachingOutputs[OutputIo.InspectionBackupPlateUp]);
        Assert.Equal(MachineAlarm.MainConveyor, state.Alarm);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TeachingSelectionIgnoresLateCameraCompletion(bool cameraFails)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.Inspection);
        var capturing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        using var services = new ServiceCollection().AddIbtmApplication(
            settings,
            new Recipe { Pcb = VirtualTest.TaughtPcbLayout() })
            .AddSingleton<ICamera>(
                new VirtualCamera(
                    () =>
                    {
                        capturing.SetResult();
                        release.Wait();
                        if (cameraFails)
                            throw new InvalidOperationException("Previous point capture failed.");
                        return (0, 0, 0);
                    },
                    () => []))
            .BuildServiceProvider();
        var machine = services.GetRequiredService<MachineController>();
        var teaching = services.GetRequiredService<StationTeachingViewModel>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        teaching.SelectedPoint = teaching.FilteredPoints.Single(
            point => point.Target == TeachingTarget.DataMatrix);
        await WaitUntilAsync(() => teaching.CaptureInspectionCommand.CanExecute(null));
        var next = teaching.FilteredPoints.Single(
            point => point.Target == TeachingTarget.CarrierUpperLeftLocatingPin);
        var recipeBefore = JsonSerializer.Serialize(teaching.RecipeEditor.Recipe);
        var settingsBefore = JsonSerializer.Serialize(settings);
        using var trace = new StringWriter();
        using var listener = new TextWriterTraceListener(trace);
        Trace.Listeners.Add(listener);
        try
        {
            var capture = teaching.CaptureInspectionCommand.ExecuteAsync(null);
            try
            {
                await capturing.Task.WaitAsync(TimeSpan.FromSeconds(2));
                teaching.SelectedPoint = next;
            }
            finally
            {
                release.Set();
            }

            await capture.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }

        Assert.Equal(
            cameraFails,
            trace.ToString().Contains("Previous point capture failed.", StringComparison.Ordinal));
        Assert.Same(next, teaching.SelectedPoint);
        Assert.False(teaching.Preview.HasImage);
        Assert.Null(teaching.Preview.Result);
        Assert.Null(teaching.CameraError);
        Assert.Equal(recipeBefore, JsonSerializer.Serialize(teaching.RecipeEditor.Recipe));
        Assert.Equal(settingsBefore, JsonSerializer.Serialize(settings));
        Assert.False(services.GetRequiredService<OperationCancellation>().HasActiveOperations);
    }

    [Fact]
    public async Task CarrierScanDeviceFailureStaysAtTheTeachingCommandBoundary()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.Inspection);
        using var services = new ServiceCollection().AddSingleton(_ => VirtualTest.OpenMachineStore())
            .AddIbtmApplication(settings)
            .AddSingleton<ICamera>(
                new VirtualCamera(
                    () => throw new InvalidOperationException("Camera SDK capture failed."),
                    () => []))
            .BuildServiceProvider();
        var machine = services.GetRequiredService<MachineController>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        var teaching = services.GetRequiredService<StationTeachingViewModel>();
        teaching.RecipeEditor.Name = $"CaptureFailure-{Guid.NewGuid():N}";
        await WaitUntilAsync(() => teaching.CaptureCarrierImagesCommand.CanExecute(null));
        await teaching.CaptureCarrierImagesCommand.ExecuteAsync(null);
        Assert.Equal("Camera SDK capture failed.", teaching.CameraError);
        Assert.False(services.GetRequiredService<MachineState>().IsRunning);
    }

    [Fact]
    public async Task BufferSetupRequiresIdleManualControl()
    {
        var settings = FlowSettings();
        var store = VirtualTest.OpenMachineStore(
            Path.Combine(Path.GetTempPath(), $"IBTM-buffer-teaching-{Guid.NewGuid():N}.db"));
        using var services = new ServiceCollection().AddSingleton(store)
            .AddIbtmApplication(settings, new Recipe { Pcb = VirtualTest.TaughtPcbLayout() })
            .BuildServiceProvider();
        var machine = services.GetRequiredService<MachineController>();
        var teaching = services.GetRequiredService<SupplyTeachingViewModel>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        await WaitUntilAsync(() => teaching.SaveBufferSetupCommand.CanExecute(null));
        teaching.Points.Single(point => point.Target == TeachingTarget.SupplyBufferBoundary1)
            .Teach(70, 0, 0);
        teaching.Points.Single(point => point.Target == TeachingTarget.PlacementBufferBoundary1)
            .Teach(75, 25, 0);

        using (services.GetRequiredService<OperationCancellation>().Link())
        {
            await WaitUntilAsync(() => !teaching.SaveBufferSetupCommand.CanExecute(null));
            await teaching.SaveBufferSetupCommand.ExecuteAsync(null);
            Assert.Equal(60, settings.PcbBuffer.SupplyBoundary1);
        }

        await WaitUntilAsync(() => teaching.SaveBufferSetupCommand.CanExecute(null));

        io.SetInput(InputIo.AutoMode, false);
        await WaitUntilAsync(() => !teaching.SaveBufferSetupCommand.CanExecute(null));
        await teaching.SaveBufferSetupCommand.ExecuteAsync(null);
        Assert.Equal(60, settings.PcbBuffer.SupplyBoundary1);

        io.SetInput(InputIo.AutoMode, true);
        await WaitUntilAsync(() => teaching.SaveBufferSetupCommand.CanExecute(null));
        settings.Units.PcbSupply = false;
        await teaching.SaveBufferSetupCommand.ExecuteAsync(null);

        Assert.Null(teaching.SaveError);
        Assert.Equal(70, settings.PcbBuffer.SupplyBoundary1);
        Assert.Equal(75, settings.PcbBuffer.PlacementBoundary1.X);
        var saved = store.LoadSettings().Get<PcbBufferSettings>();
        Assert.Equal(70, saved.SupplyBoundary1);
        Assert.Equal(75, saved.PlacementBoundary1.X);
        Assert.Equal(25, saved.PlacementBoundary1.Y);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BufferSetupCancelledBeforeExecutionDoesNotApply(bool closeTeaching)
    {
        var settings = FlowSettings();
        using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var teaching = services.GetRequiredService<SupplyTeachingViewModel>();
        var operations = services.GetRequiredService<OperationCancellation>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        await WaitUntilAsync(() => teaching.SaveBufferSetupCommand.CanExecute(null));
        teaching.Points.Single(point => point.Target == TeachingTarget.SupplyBufferBoundary1)
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
        await teaching.SaveBufferSetupCommand.ExecuteAsync(null);
        operations.ActivityChanged -= CancelWhenStarted;

        Assert.Equal(60, settings.PcbBuffer.SupplyBoundary1);
        Assert.Null(teaching.SaveError);
        Assert.False(operations.HasActiveOperations);
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
        teaching.SelectedPoint = teaching.FilteredPoints.Single(
            point => point.Target == TeachingTarget.BoltPickup);
        teaching.JogSpeed = 1;
        teaching.StepDistance = 0.1;
        await WaitUntilAsync(() => !teaching.MoveToPointCommand.CanExecute(null));
        await WaitUntilAsync(() => teaching.TeachCurrentPositionCommand.CanExecute(null));
        Assert.Equal(HomeBlockReason.FasteningNotRaised, machine.HomeBlock);
        await WaitUntilAsync(() => teaching.StepCommand.CanExecute(TeachingDirection.XPlus));

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
        await Assert.ThrowsAsync<InvalidOperationException>(() => gantry.HomeHorizontalAsync());

        var maximum = gantry.Feedback.GetRange(MotionAxis.X)!.Value.Maximum;
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => gantry.AdjustAxisAsync(MotionAxis.X, maximum + 1, 100));
        Assert.Equal(MotionCommand.None, gantry.Feedback.Command);
        await gantry.AdjustAxisAsync(MotionAxis.X, maximum - 0.1, 1000);
        await gantry.JogAsync(MotionAxis.X, 10).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal((maximum, stopped.Y, stopped.Z), gantry.Feedback.GetPosition());
        await WaitUntilAsync(() => !teaching.StepCommand.CanExecute(TeachingDirection.XPlus));

        await gantry.RaiseCylindersAsync();
        await WaitUntilAsync(() => teaching.MoveToPointCommand.CanExecute(null));
        MotionCommand positioning = MotionCommand.None;
        gantry.Feedback.MovingChanged += moving =>
        {
            if (moving)
                positioning = gantry.Feedback.Command;
        };
        await gantry.MoveToXYAsync(maximum - 1, stopped.Y);
        Assert.Equal(MotionCommand.Positioning, positioning);
        Assert.Equal(MotionCommand.None, gantry.Feedback.Command);

        var fail = true;
        gantry.Feedback.PositionChanged += (_, _, _) =>
        {
            if (!fail)
                return;
            fail = false;
            throw new MotionException("Injected teaching move", new IOException());
        };
        await teaching.JogCommand.ExecuteAsync(TeachingDirection.XMinus);
        Assert.Equal(MachineAlarm.MotionUnavailable, state.Alarm);
        Assert.False(gantry.Feedback.IsMoving);
        Assert.Equal(MotionCommand.None, gantry.Feedback.Command);
    }
}
