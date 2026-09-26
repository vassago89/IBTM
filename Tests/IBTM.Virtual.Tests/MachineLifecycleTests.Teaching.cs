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
using IBTM.NgConveyor;
using IBTM.PcbPlacement;
using IBTM.PcbSupply;
using IBTM.Storage;
using IBTM.UI;
using IBTM.Virtual;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static IBTM.Virtual.Tests.VirtualTest;

namespace IBTM.Virtual.Tests;

public sealed partial class MachineLifecycleTests
{
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
}
