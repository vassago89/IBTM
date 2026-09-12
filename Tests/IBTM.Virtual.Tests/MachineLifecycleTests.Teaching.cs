using System;
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
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TeachingHomeUsesSharedAxesAndCancelsTheWholeUnit(bool stopButton)
    {
        var settings = FlowSettings();
        settings.InspectionGantry.Motion.HorizontalHome.SearchSpeed = 1;
        using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var gantry = services.GetRequiredService<InspectionGantry>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        io.SetInput(InputIo.Door1Open, false);
        Assert.True(machine.CanHome);
        await machine.HomeAsync(CancellationToken.None);
        Assert.True(state.Homed);
        io.SetInput(InputIo.Door1Open, true);
        var teaching = services.GetRequiredService<StationTeachingViewModel>();
        teaching.SelectedTeachingUnit = HardwareArea.NgCarrierTransfer;
        await gantry.MoveToAsync(new() { X = 10, Y = 7 }, 10_000);
        await WaitUntilAsync(() => teaching.HomeCommand.CanExecute(null));

        var home = teaching.HomeCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => gantry.Feedback.IsMoving);
        io.SetInput(InputIo.Door1Open, false);
        Assert.Equal(HomeBlockReason.None, teaching.HomeBlock);
        Assert.True(gantry.Feedback.IsMoving);
        if (stopButton)
            teaching.JogStopCommand.Execute(null);
        else
            teaching.SelectedTeachingUnit = HardwareArea.BoltFastening;
        await home.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(gantry.Feedback.IsMoving);
        Assert.False(state.IsHoming);
        Assert.False(gantry.Feedback.GetAxisState(MotionAxis.X).Homed);
        Assert.False(services.GetRequiredService<OperationCancellation>().HasActiveOperations);
        Assert.NotEqual((0, 0, 0), gantry.Feedback.GetPosition());

        settings.InspectionGantry.Motion.HorizontalHome.SearchSpeed = 10_000;
        teaching.SelectedTeachingUnit = HardwareArea.NgCarrierTransfer;
        await WaitUntilAsync(() => teaching.HomeCommand.CanExecute(null));
        await teaching.HomeCommand.ExecuteAsync(null);
        Assert.True(gantry.Feedback.GetAxisState(MotionAxis.X).Homed);
        Assert.True(gantry.Feedback.GetAxisState(MotionAxis.Y).Homed);
        Assert.Equal((0, 0, 0), gantry.Feedback.GetPosition());

        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.NgCarrierPickupUp, false);
        Assert.False(teaching.HomeCommand.CanExecute(null));
        await teaching.HomeCommand.ExecuteAsync(null);
        Assert.Equal((0, 0, 0), gantry.Feedback.GetPosition());
    }

    [Theory]
    [InlineData(HardwareArea.PcbPlacementHandler, MotionGroup.PcbPlacementHandler)]
    [InlineData(HardwareArea.BoltFastening, MotionGroup.BoltFastening)]
    public async Task TeachingHomeCompletesZAndSafeHeightBeforeXY(
        HardwareArea unit,
        MotionGroup group)
    {
        var settings = FlowSettings();
        settings.PcbPlacementHandler.BufferEntryZ = 8;
        settings.BoltFastening.SafeZ = 8;
        using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        await machine.InitializeAsync();
        var motion = services.GetRequiredKeyedService<IXyMotion>(group);
        await machine.HomeAxisAsync(group, MotionAxis.Z, CancellationToken.None);
        await motion.MoveToXYAsync(10, 7, 10_000);
        await motion.MoveAxisAsync(MotionAxis.Z, 20, 10_000);
        var positions = new ConcurrentQueue<(double X, double Y, double Z, bool ZHomed)>();
        motion.PositionChanged += (x, y, z) =>
            positions.Enqueue((x, y, z, motion.GetAxisState(MotionAxis.Z).Homed));
        var teaching = services.GetRequiredService<StationTeachingViewModel>();
        teaching.SelectedTeachingUnit = unit;
        await WaitUntilAsync(() => teaching.HomeCommand.CanExecute(null));

        await teaching.HomeCommand.ExecuteAsync(null);

        var samples = positions.ToArray();
        var firstHorizontal = Array.FindIndex(samples, position => position.X != 10 || position.Y != 7);
        Assert.True(firstHorizontal > 0);
        Assert.Contains(samples.Take(firstHorizontal), position => position.Z == 0);
        Assert.All(samples.Skip(firstHorizontal), position =>
        {
            Assert.True(position.ZHomed);
            Assert.Equal(8, position.Z);
        });
        Assert.Equal((0, 0, 8), motion.GetPosition());
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
    public async Task NgTransferTeachingHasSeparatePointsAndUsesTheInspectionAxes()
    {
        using var services = CreateDisplayServices(out var feedback);
        var transferSettings = services.GetRequiredService<NgCarrierTransferSettings>();
        transferSettings.Speed = 1_234;
        transferSettings.PickupSafeX = null;
        var machine = services.GetRequiredService<MachineController>();
        var gantry = services.GetRequiredService<InspectionGantry>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        var teaching = services.GetRequiredService<StationTeachingViewModel>();
        var inspectionMotion = teaching.Motion;
        Assert.Equal(4, teaching.TeachingUnits.Length);
        Assert.DoesNotContain(teaching.FilteredPoints,
            point => point.Position.Target is TeachingTarget.NgCarrierPickup or TeachingTarget.NgShuttlePlace);

        teaching.SelectedTeachingUnit = HardwareArea.NgCarrierTransfer;

        Assert.Same(inspectionMotion, teaching.Motion);
        Assert.Equal(MotionGroup.InspectionGantry, teaching.ActiveMotionGroup);
        Assert.Equal(
            new[] { TeachingTarget.NgPickupSafeX, TeachingTarget.NgCarrierPickup, TeachingTarget.NgShuttlePlace },
            teaching.FilteredPoints.Select(point => point.Position.Target));
        Assert.False(teaching.IsInspectionSelected);
        Assert.False(teaching.BoltPointEditorVisible);
        Assert.False(teaching.BoltPresetEditorVisible);
        Assert.False(teaching.ToggleLiveViewCommand.CanExecute(null));
        Assert.False(teaching.CaptureInspectionCommand.CanExecute(null));
        Assert.False(teaching.AddBoltPointCommand.CanExecute(null));
        Assert.Contains(teaching.TeachingIoGroups, group => group.Area == HardwareArea.NgCarrierTransfer);
        Assert.Contains(teaching.TeachingIoGroups, group => group.Area == HardwareArea.NgShuttle);

        foreach (var point in teaching.FilteredPoints)
        {
            teaching.SelectedPoint = point;
            var x = point.X + 1;
            var y = point.Y + 2;
            await gantry.MoveToAsync(new() { X = x, Y = y }, 10_000);
            await WaitUntilAsync(() => teaching.TeachCurrentPositionCommand.CanExecute(null));
            await teaching.TeachCurrentPositionCommand.ExecuteAsync(null);
            var saved = services.GetRequiredService<MachineStore>().LoadSettings().Get<NgCarrierTransferSettings>();
            var position = point.Position.Target switch
            {
                TeachingTarget.NgPickupSafeX => new AxisPosition { X = saved.PickupSafeX!.Value },
                TeachingTarget.NgCarrierPickup => saved.CarrierPickupPosition,
                _ => saved.ShuttlePlacePosition,
            };
            if (point.Position.Mode != TeachMode.YOnly)
                Assert.Equal(x, position.X);
            Assert.Equal(point.Position.Mode == TeachMode.XOnly ? 0 : y, position.Y);

            await gantry.MoveToAsync(new() { X = x + 5, Y = y + 5 }, 10_000);
            await WaitUntilAsync(() => teaching.MoveToPointCommand.CanExecute(null));
            feedback.AxisMoves.Clear();
            await teaching.MoveToPointCommand.ExecuteAsync(null);
            var targetX = point.Position.Mode == TeachMode.YOnly
                ? transferSettings.PickupSafeX!.Value
                : x;
            Assert.Equal((targetX, point.Position.Mode == TeachMode.XOnly ? y + 5 : y, 0), gantry.Feedback.GetPosition());
            if (point.Position.Target == TeachingTarget.NgCarrierPickup)
            {
                Assert.Equal(TeachMode.YOnly, point.Position.Mode);
                Assert.Equal(
                    new[] { (MotionAxis.X, transferSettings.PickupSafeX!.Value), (MotionAxis.Y, y) },
                    feedback.AxisMoves);
            }
            if (point.Position.Target == TeachingTarget.NgShuttlePlace)
                Assert.Empty(feedback.AxisMoves);
            Assert.Equal(transferSettings.Speed, feedback.LastMoveVelocity);
        }

        var beforeStep = gantry.Feedback.GetPosition();
        teaching.StepDistance = 0.1;
        await WaitUntilAsync(() => teaching.StepCommand.CanExecute(TeachingDirection.XPlus));
        await teaching.StepCommand.ExecuteAsync(TeachingDirection.XPlus);
        Assert.Equal(transferSettings.Speed, feedback.LastMoveVelocity);
        Assert.Equal(beforeStep.X + 0.1, gantry.Feedback.GetPosition().X, 6);
        Assert.Equal(beforeStep.Y, gantry.Feedback.GetPosition().Y);

        var shuttle = teaching.TeachingOutputs[OutputIo.NgShuttleUp];
        await WaitUntilAsync(() => teaching.ToggleOutputCommand.CanExecute(shuttle));
        await teaching.ToggleOutputCommand.ExecuteAsync(shuttle);
        Assert.True(io.GetInput(InputIo.NgShuttleDown));
        await teaching.ToggleOutputCommand.ExecuteAsync(shuttle);
        Assert.True(io.GetInput(InputIo.NgShuttleUp));

        teaching.SelectedTeachingUnit = HardwareArea.InspectionGantry;
        Assert.Same(inspectionMotion, teaching.Motion);
        Assert.True(teaching.IsInspectionSelected);
        Assert.DoesNotContain(OutputIo.NgShuttleUp, teaching.TeachingOutputs.Keys);
        Assert.DoesNotContain(teaching.TeachingIoGroups, group => group.Area == HardwareArea.NgShuttle);
        Assert.DoesNotContain(teaching.FilteredPoints,
            point => point.Position.Target is TeachingTarget.NgCarrierPickup or TeachingTarget.NgShuttlePlace);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TeachingRechecksFeedbackBeforeJogOrSavingPosition(bool savePosition)
    {
        using var services = CreateDisplayServices(out var feedback);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        var teaching = services.GetRequiredService<StationTeachingViewModel>();
        teaching.SelectedTeachingUnit = HardwareArea.InspectionGantry;
        teaching.SelectedPoint = teaching.FilteredPoints.Single(
            point => point.Position.Target == TeachingTarget.CarrierUpperLeftLocatingPin);
        await WaitUntilAsync(() => teaching.TeachCurrentPositionCommand.CanExecute(null));
        var point = teaching.SelectedPoint;
        Assert.True(state.Alarm == MachineAlarm.None, state.AlarmDetail);
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
            savePosition ? MachineAlarm.IoCommunication : MachineAlarm.Inspection,
            state.Alarm);
        Assert.Contains("Teaching feedback read failed.", state.AlarmDetail);
        Assert.Equal(before, (point.X, point.Y, point.Z));
        Assert.False(teaching.Motion.IsMoving);
        Assert.False(services.GetRequiredService<OperationCancellation>().HasActiveOperations);
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
        using var services = CreateDisplayServices(out var feedback);
        var machine = services.GetRequiredService<MachineController>();
        var gantry = services.GetRequiredService<InspectionGantry>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        var teaching = services.GetRequiredService<StationTeachingViewModel>();
        await gantry.MoveToAsync(new() { X = 10, Y = 20 }, 10_000);
        teaching.StepDistance = 0.1;
        await WaitUntilAsync(() => teaching.StepCommand.CanExecute(direction));

        await teaching.StepCommand.ExecuteAsync(direction);

        Assert.Equal(expectedAxis, feedback.LastMovedAxis);
        Assert.Equal((x, y, 0), gantry.Feedback.GetPosition());

        await services.GetRequiredService<IIoService>()
            .SetOutputAndWaitAsync(OutputIo.NgCarrierPickupUp, false);
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
            station.SelectedTeachingUnit = group == MotionGroup.PcbPlacementHandler
                ? HardwareArea.PcbPlacementHandler
                : HardwareArea.InspectionGantry;
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


    [Fact]
    public async Task TeachingOutputsWaitForFeedbackAndCancelWithoutReversingPneumatics()
    {
        using var services = CreateServices(FlowSettings());
        var machine = services.GetRequiredService<MachineController>();
        var teaching = services.GetRequiredService<SupplyTeachingViewModel>();
        var io = services.GetRequiredService<VirtualIoService>();
        var state = services.GetRequiredService<MachineState>();
        var gripper = teaching.TeachingOutputs[OutputIo.PcbSupplyGripperClosed];
        await WaitUntilAsync(() => !teaching.ToggleOutputCommand.CanExecute(gripper));
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        var handler = services.GetRequiredService<PcbSupplyHandler>();
        var rotation = teaching.TeachingOutputs[OutputIo.PcbSupplyRotate];
        await handler.MoveTeachingZAsync(5);
        await WaitUntilAsync(() => teaching.ToggleOutputCommand.CanExecute(rotation));
        await teaching.ToggleOutputCommand.ExecuteAsync(rotation);
        Assert.Equal(0, handler.Feedback.GetPosition().Z);
        Assert.Equal(PcbSupplyRotationState.Unrotated, handler.Rotation);
        await teaching.ToggleOutputCommand.ExecuteAsync(rotation);
        await handler.MoveXAsync(80);
        await WaitUntilAsync(() => !teaching.ToggleOutputCommand.CanExecute(rotation));
        await WaitUntilAsync(() => !teaching.StepCommand.CanExecute(TeachingDirection.ZPlus));
        await handler.MoveXAsync(0);
        io.AutoResponseEnabled = false;
        await WaitUntilAsync(() => teaching.ToggleOutputCommand.CanExecute(gripper));

        var pending = teaching.ToggleOutputCommand.ExecuteAsync(gripper);
        Assert.True(io.GetOutput(gripper.Signal));
        Assert.False(pending.IsCompleted);
        await WaitUntilAsync(() => !teaching.ToggleOutputCommand.CanExecute(gripper));
        Assert.True(state.IsRunning); // The feedback wait owns a machine operation.
        var feedback = io.GetOutputFeedback(gripper.Signal)!;
        io.SetInput(feedback.OnInput, true);
        Assert.False(pending.IsCompleted); // Both inputs ON is not completion.
        io.SetInput(feedback.OffInput, false);
        await pending.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => teaching.ToggleOutputCommand.CanExecute(gripper));

        teaching.SelectedPoint = teaching.Points.Last(
            point => point.Position.MotionGroup == MotionGroup.PcbSupply);
        var beforeSelection = handler.Feedback.GetPosition();
        var releasing = teaching.ToggleOutputCommand.ExecuteAsync(gripper);
        Assert.False(releasing.IsCompleted);
        teaching.SelectNextPointCommand.Execute(null);
        await releasing.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(MotionGroup.PcbPlacementHandler, teaching.SelectedPoint!.Position.MotionGroup);
        Assert.Equal(beforeSelection, handler.Feedback.GetPosition());
        Assert.False(io.GetOutput(gripper.Signal));
        Assert.True(io.GetInput(feedback.OnInput));
        Assert.Equal(MachineAlarm.None, state.Alarm);
        await WaitUntilAsync(() => !teaching.ToggleOutputCommand.CanExecute(gripper));

        var lift = teaching.TeachingOutputs[OutputIo.PcbPlacementHandlerDown];
        var lowering = teaching.ToggleOutputCommand.ExecuteAsync(lift);
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
        var io = services.GetRequiredService<VirtualIoService>();
        var state = services.GetRequiredService<MachineState>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        var teaching = services.GetRequiredService<StationTeachingViewModel>();
        Assert.True(state.Ready, state.AlarmDetail);
        teaching.SelectedTeachingUnit = HardwareArea.PcbPlacementHandler;
        var lift = teaching.TeachingOutputs[OutputIo.PcbPlacementHandlerDown];
        await teaching.ToggleOutputCommand.ExecuteAsync(lift);
        await WaitUntilAsync(() => !teaching.StepCommand.CanExecute(TeachingDirection.XPlus));
        await WaitUntilAsync(
            () => !teaching.ToggleOutputCommand.CanExecute(
                teaching.TeachingOutputs[OutputIo.PcbPlacementHandlerRotate]));
        await teaching.ToggleOutputCommand.ExecuteAsync(lift);
        await WaitUntilAsync(() => teaching.StepCommand.CanExecute(TeachingDirection.XPlus));
        var ipm = teaching.TeachingOutputs[OutputIo.PcbPlacementIpmDown];
        await teaching.ToggleOutputCommand.ExecuteAsync(ipm);
        await WaitUntilAsync(() => teaching.StepCommand.CanExecute(TeachingDirection.XPlus));

        teaching.SelectedTeachingUnit = HardwareArea.BoltFastening;
        Assert.Contains(OutputIo.ShootBolt, teaching.TeachingOutputs.Keys);
        Assert.DoesNotContain(OutputIo.ShootingEscapeForward, teaching.TeachingOutputs.Keys);
        foreach (var output in new[] { OutputIo.PickupHeadUp, OutputIo.ShootingHeadUp })
        {
            var head = teaching.TeachingOutputs[output];
            await teaching.ToggleOutputCommand.ExecuteAsync(head);
            Assert.False(io.GetOutput(output));
            Assert.False(services.GetRequiredService<BoltFasteningGantry>().CanMoveHorizontal);
            await WaitUntilAsync(() => teaching.StepCommand.CanExecute(TeachingDirection.XPlus));
            await teaching.ToggleOutputCommand.ExecuteAsync(head);
            Assert.True(io.GetOutput(output));
            Assert.True(services.GetRequiredService<BoltFasteningGantry>().CanMoveHorizontal);
        }

        teaching.SelectedTeachingUnit = HardwareArea.NgCarrierTransfer;
        var ngLift = teaching.TeachingOutputs[OutputIo.NgCarrierPickupUp];
        settings.Units.NgCarrierTransfer = false;
        await WaitUntilAsync(() => teaching.ToggleOutputCommand.CanExecute(ngLift));
        settings.Units.NgCarrierTransfer = true;
        io.AutoResponseEnabled = false;
        var pending = teaching.ToggleOutputCommand.ExecuteAsync(ngLift);
        teaching.JogStopCommand.Execute(null);
        await pending.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(MachineAlarm.None, state.Alarm);
        Assert.False(io.GetOutput(ngLift.Signal));

        settings.Options.TimeoutMilliseconds = 50;
        io.SetOutput(ngLift.Signal, true); // External output change; toggle must read the current DO.
        await teaching.ToggleOutputCommand.ExecuteAsync(ngLift);
        Assert.Equal(MachineAlarm.NgCarrierTransfer, state.Alarm);
    }

    [Fact]
    public async Task StationTeachingReportsMotionAndHomeBlocks()
    {
        var settings = FlowSettings();
        using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<VirtualIoService>();
        var teaching = services.GetRequiredService<StationTeachingViewModel>();
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
        teaching.SelectedTeachingUnit = HardwareArea.NgCarrierTransfer;
        Assert.Equal(HomeBlockReason.None, teaching.HomeBlock);
        await WaitUntilAsync(() => teaching.MotionHint == TeachingMotionHint.HomeRequired);
        Assert.True(teaching.HomeCommand.CanExecute(null));
    }

    [Fact]
    public async Task StationTeachingControlsOnlyItsOwnStopper()
    {
        using var services = CreateServices(FlowSettings());
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<VirtualIoService>();
        var teaching = services.GetRequiredService<StationTeachingViewModel>();
        await machine.InitializeAsync();
        (HardwareArea Unit, OutputIo Output, InputIo Down, InputIo Up)[] stoppers = [
            (HardwareArea.PcbPlacementHandler, OutputIo.PcbPlacementStopperDown,
                InputIo.PcbPlacementStopperDown, InputIo.PcbPlacementStopperUp),
            (HardwareArea.BoltFastening, OutputIo.BoltFasteningStopperDown,
                InputIo.BoltFasteningStopperDown, InputIo.BoltFasteningStopperUp),
            (HardwareArea.NgCarrierTransfer, OutputIo.InspectionStopperDown,
                InputIo.InspectionStopperDown, InputIo.InspectionStopperUp),
        ];
        var changed = new ConcurrentQueue<OutputIo>();
        io.OutputChanged += (signal, _) => changed.Enqueue(signal);

        foreach (var (unit, output, down, up) in stoppers)
        {
            teaching.SelectedTeachingUnit = unit;
            var stopper = teaching.TeachingOutputs[output];
            Assert.Contains(teaching.TeachingIoGroups.SelectMany(group => group.Outputs),
                row => row.Output == stopper);
            await WaitUntilAsync(() => teaching.ToggleOutputCommand.CanExecute(stopper));
            await teaching.ToggleOutputCommand.ExecuteAsync(stopper);
            Assert.True(io.GetInput(down));
            Assert.False(io.GetInput(up));
            await teaching.ToggleOutputCommand.ExecuteAsync(stopper);
            Assert.False(io.GetInput(down));
            Assert.True(io.GetInput(up));
            Assert.All(changed, signal => Assert.Equal(output, signal));
            changed.Clear();
        }
    }

    [Fact]
    public async Task StationTeachingControlsOnlyItsOwnBackupPlate()
    {
        var settings = FlowSettings();
        settings.Units.NgCarrierTransfer = false;
        using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        var teaching = services.GetRequiredService<StationTeachingViewModel>();
        (HardwareArea Group, TeachingTarget Target, InputIo Up, InputIo Down, OutputIo Output)[] stations = [
            (
                HardwareArea.PcbPlacementHandler,
                TeachingTarget.HeatSink1PcbPlacement,
                InputIo.PcbPlacementBackupPlateUp,
                InputIo.PcbPlacementBackupPlateDown,
                OutputIo.PcbPlacementBackupPlateDown),
            (
                HardwareArea.BoltFastening,
                TeachingTarget.ShootingHeadUpperLeftLocatingPin,
                InputIo.BoltFasteningBackupPlateUp,
                InputIo.BoltFasteningBackupPlateDown,
                OutputIo.BoltFasteningBackupPlateDown),
            (
                HardwareArea.InspectionGantry,
                TeachingTarget.CarrierUpperLeftLocatingPin,
                InputIo.InspectionBackupPlateUp,
                InputIo.InspectionBackupPlateDown,
                OutputIo.InspectionBackupPlateDown),
        ];
        foreach (var station in stations)
        {
            await ((IIoService)io).SetOutputAndWaitAsync(station.Output, true);
        }

        foreach (var (group, target, up, down, output) in stations)
        {
            teaching.SelectedTeachingUnit = group;
            teaching.SelectedPoint = teaching.FilteredPoints.Single(point => point.Position.Target == target);
            var plate = teaching.TeachingOutputs[output];
            await WaitUntilAsync(() => teaching.ToggleOutputCommand.CanExecute(plate));
            await teaching.ToggleOutputCommand.ExecuteAsync(plate);
            Assert.True(io.GetInput(up));
            Assert.False(io.GetInput(down));
            foreach (var other in stations.Where(station => station.Group != group))
            {
                Assert.False(teaching.TeachingOutputs.ContainsKey(other.Output));
                Assert.False(io.GetInput(other.Up));
            }

            await teaching.ToggleOutputCommand.ExecuteAsync(plate);
            Assert.False(io.GetInput(up));
            Assert.True(io.GetInput(down));
        }

        var state = services.GetRequiredService<MachineState>();
        Assert.True(machine.CanHome);
        await machine.HomeAsync(CancellationToken.None);
        var supply = services.GetRequiredKeyedService<IAxisMotion>(MotionGroup.PcbSupply);
        await supply.MoveAxisAsync(MotionAxis.X, settings.PcbSupply.BufferHandoffPosition.X, 1_000);
        teaching.SelectedTeachingUnit = HardwareArea.PcbPlacementHandler;
        Assert.True(state.SupplyInBufferArea);
        await WaitUntilAsync(() => !teaching.JogCommand.CanExecute(TeachingDirection.XPlus));
        var placementPlate = teaching.TeachingOutputs[OutputIo.PcbPlacementBackupPlateDown];
        await WaitUntilAsync(() => teaching.ToggleOutputCommand.CanExecute(placementPlate));
        await teaching.ToggleOutputCommand.ExecuteAsync(placementPlate);
        await teaching.ToggleOutputCommand.ExecuteAsync(placementPlate);
        supply.SetServo(MotionAxis.X, false);
        Assert.False(state.ManualControlsEnabled);
        await WaitUntilAsync(() => teaching.ToggleOutputCommand.CanExecute(placementPlate));
        io.SetInput(InputIo.AutoMode, false);
        await WaitUntilAsync(() => !teaching.ToggleOutputCommand.CanExecute(placementPlate));
        io.SetInput(InputIo.AutoMode, true);

        teaching.SelectedTeachingUnit = HardwareArea.InspectionGantry;
        settings.Options.TimeoutMilliseconds = 50;
        io.AutoResponseEnabled = false;
        await teaching.ToggleOutputCommand.ExecuteAsync(
            teaching.TeachingOutputs[OutputIo.InspectionBackupPlateDown]);
        Assert.Equal(MachineAlarm.MainConveyor, state.Alarm);
    }

    // Runs inside the existing WPF test host; this view model requires its UI Dispatcher.
    internal async Task VerifyTeachingSelectionIgnoresLateCameraCompletionAsync(bool cameraFails)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.Inspection);
        var capturing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var recipe = new Recipe();
        TeachInspectionFovs(settings, recipe);
        using var services = new ServiceCollection().AddIbtmApplication(
            settings,
            recipe)
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
            point => point.Position.Target == TeachingTarget.DataMatrix);
        await WaitUntilAsync(() => teaching.CaptureInspectionCommand.CanExecute(null));
        var next = teaching.FilteredPoints.Single(
            point => point.Position.Target == TeachingTarget.CarrierUpperLeftLocatingPin);
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
    public async Task BufferSetupRequiresIdleManualControl()
    {
        var settings = FlowSettings();
        var store = VirtualTest.OpenMachineStore(
            Path.Combine(Path.GetTempPath(), $"IBTM-buffer-teaching-{Guid.NewGuid():N}.db"));
        using var services = new ServiceCollection().AddSingleton(store)
            .AddIbtmApplication(settings, new Recipe())
            .BuildServiceProvider();
        var machine = services.GetRequiredService<MachineController>();
        var teaching = services.GetRequiredService<SupplyTeachingViewModel>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        await WaitUntilAsync(() => teaching.SaveBufferSetupCommand.CanExecute(null));
        teaching.Points.Single(point => point.Position.Target == TeachingTarget.SupplyBufferBoundary1)
            .Teach(70, 0, 0);
        teaching.Points.Single(point => point.Position.Target == TeachingTarget.PlacementBufferBoundary1)
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
        teaching.Points.Single(point => point.Position.Target == TeachingTarget.SupplyBufferBoundary1)
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
        var settings = FlowSettings();
        settings.Units.Inspection = false;
        using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var gantry = services.GetRequiredService<BoltFasteningGantry>();
        var teaching = services.GetRequiredService<StationTeachingViewModel>();
        await machine.InitializeAsync();
        await gantry.RaiseCylindersAsync();
        Assert.True(await gantry.HomeAxisAsync(MotionAxis.Z));
        Assert.True(await gantry.HomeHorizontalAsync());
        await gantry.MoveToXYAsync(20, 20);
        await gantry.MoveZAsync(10);
        await Task.WhenAll(
            ((IIoService)io).SetOutputAndWaitAsync(OutputIo.PickupHeadUp, false),
            ((IIoService)io).SetOutputAndWaitAsync(OutputIo.ShootingHeadUp, false));
        teaching.SelectedTeachingUnit = HardwareArea.BoltFastening;
        teaching.SelectedPoint = teaching.FilteredPoints.Single(
            point => point.Position.Target == TeachingTarget.BoltPickup);
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
        await WaitUntilAsync(() => !state.IsRunning);
        Assert.Equal(MachineAlarm.None, state.Alarm);
        await Assert.ThrowsAsync<InvalidOperationException>(() => gantry.MoveToXYAsync(30, 30));
        await Assert.ThrowsAsync<InvalidOperationException>(() => gantry.HomeHorizontalAsync());

        await gantry.AdjustAxisAsync(MotionAxis.X, -56.561, 10_000);
        Assert.Equal(-56.561, gantry.Feedback.GetPosition().X, 6);
        Assert.Equal(MotionCommand.None, gantry.Feedback.Command);
        await gantry.AdjustAxisAsync(MotionAxis.X, 201, 10_000);
        using var jogStop = new CancellationTokenSource();
        var beyondOldMaximum = gantry.JogAsync(MotionAxis.X, 10, jogStop.Token);
        await WaitUntilAsync(() => gantry.Feedback.GetPosition().X > 201.1);
        jogStop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => beyondOldMaximum);
        Assert.Equal(stopped.Y, gantry.Feedback.GetPosition().Y);
        Assert.Equal(stopped.Z, gantry.Feedback.GetPosition().Z);
        await WaitUntilAsync(() => teaching.StepCommand.CanExecute(TeachingDirection.XPlus));

        await gantry.RaiseCylindersAsync();
        await WaitUntilAsync(() => teaching.MoveToPointCommand.CanExecute(null));
        MotionCommand positioning = MotionCommand.None;
        gantry.Feedback.MovingChanged += moving =>
        {
            if (moving)
                positioning = gantry.Feedback.Command;
        };
        await gantry.MoveToXYAsync(200, stopped.Y);
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
