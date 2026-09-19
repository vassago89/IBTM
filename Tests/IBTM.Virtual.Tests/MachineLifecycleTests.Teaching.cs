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
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var gantry = services.GetRequiredService<InspectionGantry>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        io.SetInput(InputIo.Door1Open, false);
        Assert.True(machine.IsHomeAllowed);
        await machine.HomeAsync(CancellationToken.None);
        Assert.True(state.Homed);
        io.SetInput(InputIo.Door1Open, true);
        var teaching = services.GetRequiredService<TeachingViewModel>();
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

        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.NgCarrierPickupDown, true);
        var homeStarted = false;
        gantry.Feedback.MovingChanged += moving => homeStarted |= moving;
        await teaching.HomeCommand.ExecuteAsync(null);
        Assert.True(homeStarted);
        Assert.True(io.GetInput(InputIo.NgCarrierPickupUp));
        Assert.Equal((0, 0, 0), gantry.Feedback.GetPosition());
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
        settings.PcbPlacementHandler.BufferHandoffPosition.Z = 8;
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
        Assert.Equal((0, 0, 0), motion.GetPosition());
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
        await using var services = CreateDisplayServices(out var feedback);
        var transferSettings = services.GetRequiredService<NgCarrierTransferSettings>();
        transferSettings.Speed = 1_234;
        transferSettings.PickupSafeX = null;
        var machine = services.GetRequiredService<MachineController>();
        var gantry = services.GetRequiredService<InspectionGantry>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        var teaching = services.GetRequiredService<TeachingViewModel>();
        var inspectionMotion = teaching.Motion;
        Assert.Contains(HardwareArea.NgCarrierTransfer, teaching.TeachingUnits);
        Assert.DoesNotContain(teaching.FilteredPoints,
            point => point.Position.Target is TeachingTarget.NgCarrierPickup or TeachingTarget.NgShuttlePlace);

        teaching.SelectedTeachingUnit = HardwareArea.NgCarrierTransfer;

        Assert.Same(inspectionMotion, teaching.Motion);
        Assert.Equal(MotionGroup.InspectionGantry, teaching.ActiveMotionGroup);
        var teachingTargets = new[]
        {
            TeachingTarget.NgPickupSafeX, TeachingTarget.NgCarrierPickup, TeachingTarget.NgShuttlePlace,
        };
        Assert.Equal(
            teachingTargets.Order(),
            teaching.FilteredPoints.Select(point => point.Position.Target).Order());
        Assert.False(teaching.IsInspectionSelected);
        Assert.False(teaching.BoltPointEditorVisible);
        Assert.False(teaching.IsFasteningSelected);
        Assert.False(teaching.ToggleLiveViewCommand.CanExecute(null));
        Assert.False(teaching.CaptureInspectionCommand.CanExecute(null));
        Assert.False(teaching.AddBoltPointCommand.CanExecute(null));
        Assert.Contains(teaching.TeachingIoGroups, group => group.Area == HardwareArea.NgCarrierTransfer);
        Assert.Contains(teaching.TeachingIoGroups, group => group.Area == HardwareArea.NgShuttle);

        foreach (var target in teachingTargets)
        {
            var point = teaching.FilteredPoints.Single(point => point.Position.Target == target);
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

        var shuttle = TeachingRows(teaching)[OutputIo.NgShuttleDown];
        await WaitUntilAsync(() => shuttle.ToggleOutputCommand.CanExecute(null));
        await shuttle.ToggleOutputCommand.ExecuteAsync(null);
        Assert.True(io.GetInput(InputIo.NgShuttleDown));
        await shuttle.ToggleOutputCommand.ExecuteAsync(null);
        Assert.True(io.GetInput(InputIo.NgShuttleUp));

        teaching.SelectedTeachingUnit = HardwareArea.InspectionGantry;
        Assert.Same(inspectionMotion, teaching.Motion);
        Assert.True(teaching.IsInspectionSelected);
        Assert.DoesNotContain(OutputIo.NgShuttleDown, TeachingRows(teaching).Keys);
        Assert.DoesNotContain(teaching.TeachingIoGroups, group => group.Area == HardwareArea.NgShuttle);
        Assert.DoesNotContain(teaching.FilteredPoints,
            point => point.Position.Target is TeachingTarget.NgCarrierPickup or TeachingTarget.NgShuttlePlace);
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
            await services.GetRequiredService<PcbPlacementHandler>().MoveToHorizontalZAsync();
        var teaching = services.GetRequiredService<TeachingViewModel>();
        teaching.SelectedTeachingUnit = unit;
        await WaitUntilAsync(() => teaching.JogCommand.CanExecute(TeachingDirection.XPlus));
        var before = teaching.Motion.Feedback.GetPosition();
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
            Assert.Equal(before, teaching.Motion.Feedback.GetPosition());
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
        var motion = services.GetRequiredService<InspectionGantry>().Feedback;
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
        var viewToken = (CancellationToken)typeof(TeachingViewModel)
            .GetProperty("ViewCancellation", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(teaching)!;
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
        var gantry = services.GetRequiredService<InspectionGantry>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        var teaching = services.GetRequiredService<TeachingViewModel>();
        await gantry.MoveToAsync(new() { X = 10, Y = 20 }, 10_000);
        teaching.StepDistance = 0.1;
        await WaitUntilAsync(() => teaching.StepCommand.CanExecute(direction));

        await teaching.StepCommand.ExecuteAsync(direction);

        Assert.Equal(expectedAxis, feedback.LastMovedAxis);
        Assert.Equal((x, y, 0), gantry.Feedback.GetPosition());

        await services.GetRequiredService<IIoService>()
            .SetOutputAndWaitAsync(OutputIo.NgCarrierPickupDown, true);
        await WaitUntilAsync(() => !teaching.StepCommand.CanExecute(direction));
        await Assert.ThrowsAsync<MotionInterlockException>(
            () => gantry.MoveAxisAsync(MotionAxis.X, 30, 1_000));
        Assert.Equal((x, y, 0), gantry.Feedback.GetPosition());
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
            await services.GetRequiredService<PcbPlacementHandler>().MoveToHorizontalZAsync();
        var teaching = services.GetRequiredService<TeachingViewModel>();
        teaching.SelectedTeachingUnit = group switch
        {
            MotionGroup.PcbSupply => HardwareArea.PcbSupply,
            MotionGroup.PcbPlacementHandler => HardwareArea.PcbPlacementHandler,
            _ => HardwareArea.InspectionGantry,
        };
        IMotionFeedback feedback = group switch
        {
            MotionGroup.PcbSupply => services.GetRequiredService<PcbSupplyHandler>().Feedback,
            MotionGroup.PcbPlacementHandler => services.GetRequiredService<PcbPlacementHandler>().Feedback,
            _ => services.GetRequiredService<InspectionGantry>().Feedback,
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

    [Fact]
    public async Task ImageRulerCalibratesBothHeatSinksWithoutMovingOrChangingRois()
    {
        await using var services = CreateServices(FlowSettings());
        await services.GetRequiredService<MachineController>().InitializeAsync();
        var recipe = services.GetRequiredService<RecipeManager>().Current;
        var reference = services.GetRequiredService<CarrierReferenceSettings>();
        reference.UpperLeftLocatingPin = new() { X = 2, Y = 3 };
        reference.LowerRightLocatingPin = new() { X = 32, Y = 23 };
        recipe.Pcb.BoltPoints.AddRange([
            new() { Number = 1, HeatSink = HeatSinkSlot.HeatSink1, X = 999, Y = 999 },
            new() { Number = 1, HeatSink = HeatSinkSlot.HeatSink2, X = 999, Y = 999 },
        ]);
        CarrierImageTile[] metadata = [
            new() { Number = 1, BoltNumber = 1, HeatSink = HeatSinkSlot.HeatSink1, Center = new() { X = 10, Y = 20 }, Region = new(60, 10, 20, 10) },
            new() { Number = 2, BoltNumber = 1, HeatSink = HeatSinkSlot.HeatSink2, Center = new() { X = 30, Y = 40 }, Region = new(5, 45, 10, 10) },
            new() { Number = 3, IsBarcode = true, HeatSink = HeatSinkSlot.HeatSink1, Center = new() { X = 50, Y = 60 }, Region = new(10, 10, 20, 20) },
        ];
        var image = await Task.Run(() => InspectionPreview.CreateBitmap(new ImageFrame(100, 80, 300, new byte[24_000])));
        var teaching = services.GetRequiredService<TeachingViewModel>();
        var images = metadata.Select(tile => new CarrierImageTileView(tile, image)).ToArray();
        Assert.True(await teaching.RecipeEditor.SaveCarrierImagesAsync(images));
        teaching.CarrierImages = images;
        teaching.SelectedFov = images[0];
        var roi = teaching.FovRegion;
        var moves = 0;
        services.GetRequiredService<InspectionGantry>().Feedback.PositionChanged += (_, _, _) => moves++;

        teaching.IsMeasuring = true;
        Assert.False(teaching.DrawFovRegionCommand.CanExecute(System.Windows.Rect.Empty));
        Assert.False(teaching.TeachFovRegionCommand.CanExecute(roi));
        var ruler = new ImageRuler(new(10, 10), new(40, 50)); // Diagonal 30/40/50 in source pixels.
        teaching.MeasureImageCommand.Execute(ruler);
        Assert.Equal(50, teaching.Ruler!.PixelLength);
        foreach (var distance in new[] { 0d, -1, double.NaN, double.PositiveInfinity })
        {
            teaching.RulerMillimeters = distance;
            Assert.Null(teaching.RulerResolution);
            Assert.False(teaching.ApplyRulerResolutionCommand.CanExecute(null));
        }
        teaching.RulerMillimeters = 5;
        Assert.Equal(0.1, teaching.RulerResolution);
        Assert.Equal(Recipe.DefaultCarrierImageMillimetersPerPixel, recipe.CarrierImageMillimetersPerPixel);
        Assert.True(teaching.ApplyRulerResolutionCommand.CanExecute(null));
        await teaching.ApplyRulerResolutionCommand.ExecuteAsync(null);

        Assert.Null(teaching.CameraError);
        Assert.Null(teaching.RecipeEditor.Error);
        Assert.Equal(roi, teaching.FovRegion);
        Assert.Equal(0, moves);
        var saved = services.GetRequiredService<MachineStore>().LoadRecipe<Recipe>(teaching.RecipeEditor.ActiveName);
        Assert.Equal(0.1, saved.CarrierImageMillimetersPerPixel);
        Assert.Equal((10d, 14.5), (saved.Pcb.BoltPoints[0].X, saved.Pcb.BoltPoints[0].Y));
        Assert.Equal((24d, 38d), (saved.Pcb.BoltPoints[1].X, saved.Pcb.BoltPoints[1].Y));
        Assert.Equal(metadata.Select(tile => tile.Region), saved.CarrierImages.Select(tile => tile.Region));
        Assert.Equal(metadata.Select(tile => (tile.Center.X, tile.Center.Y)), saved.CarrierImages.Select(tile => (tile.Center.X, tile.Center.Y)));

        teaching.SelectedFov = images[1];
        Assert.Null(teaching.Ruler);
        Assert.Null(teaching.RulerMillimeters);
        Assert.False(teaching.ApplyRulerResolutionCommand.CanExecute(null));
        teaching.IsMeasuring = false;
        Assert.True(teaching.DrawFovRegionCommand.CanExecute(System.Windows.Rect.Empty));
        Assert.False(teaching.MeasureImageCommand.CanExecute(ruler));
    }

    [Fact]
    public async Task LoadedRecipeIsVisibleToExistingConsumers()
    {
        await using var services = CreateServices(FlowSettings());
        var recipes = services.GetRequiredService<RecipeManager>();
        var inspector = services.GetRequiredService<BoltInspector>();
        var editor = services.GetRequiredService<RecipeEditor>();
        var preview = new InspectionPreview(inspector, recipes);
        var frame = new ImageFrame(1, 1, 3, [160, 160, 160]);
        var region = new PixelRegion(0, 0, 1, 1);
        recipes.Current.BoltInspection.BrightnessThreshold = 128;
        Assert.Equal(1, inspector.Check(frame, region, new()).BrightRatio);
        Assert.False(inspector.HasBarcodeRegion(HeatSinkSlot.HeatSink1));
        var saved = new Recipe
        {
            Name = "Other",
            BoltInspection = new() { BrightnessThreshold = 200 },
            CarrierImages = [new() { Number = 1, IsBarcode = true, Region = region }],
        };
        services.GetRequiredService<MachineStore>().SaveRecipe(saved.Name, saved, [1]);

        await recipes.LoadAsync(saved.Name);

        Assert.Equal("Other", editor.ActiveName);
        Assert.Equal("Other", editor.Name);
        Assert.Equal(200, preview.BrightnessThreshold);
        Assert.Equal(0, inspector.Check(frame, region, new()).BrightRatio);
        Assert.True(inspector.HasBarcodeRegion(HeatSinkSlot.HeatSink1));
        Assert.Same(recipes.Current.CarrierImages[0], inspector.GetBarcodeFov(HeatSinkSlot.HeatSink1));
    }

    [Fact]
    public async Task InspectionTargetSelectionOwnsFovRoiAndPerBoltPreview()
    {
        await using var services = CreateServices(FlowSettings());
        var recipe = services.GetRequiredService<RecipeManager>().Current;
        recipe.BoltInspection.BrightnessThreshold = 128;
        recipe.BoltInspection.MinimumBrightRatio = 0.5;
        recipe.Pcb.BoltPoints.AddRange([
            new() { Number = 1, HeatSink = HeatSinkSlot.HeatSink1 },
            new() { Number = 2, HeatSink = HeatSinkSlot.HeatSink1, BrightnessThreshold = 200, MinimumBrightRatio = 0.5 },
            new() { Number = 3, HeatSink = HeatSinkSlot.HeatSink1 },
            new() { Number = 1, HeatSink = HeatSinkSlot.HeatSink2, BrightnessThreshold = 50, MinimumBrightRatio = 0.25 },
        ]);
        CarrierImageTile[] metadata = [
            new() { Number = 1, BoltNumber = 1, HeatSink = HeatSinkSlot.HeatSink1, Center = new() { X = 10, Y = 20 }, Region = new(0, 0, 2, 1) },
            new() { Number = 2, BoltNumber = 2, HeatSink = HeatSinkSlot.HeatSink1, Center = new() { X = 30, Y = 40 }, Region = new(1, 0, 1, 1) },
            new() { Number = 3, IsBarcode = true, HeatSink = HeatSinkSlot.HeatSink1, Center = new() { X = 50, Y = 60 }, Region = new(0, 0, 1, 1) },
            new() { Number = 4, BoltNumber = 1, HeatSink = HeatSinkSlot.HeatSink2, Center = new() { X = 70, Y = 80 }, Region = new(0, 0, 2, 1) },
        ];
        recipe.CarrierImages.AddRange(metadata);
        var image = await Task.Run(() => InspectionPreview.CreateBitmap(new ImageFrame(2, 1, 6, [80, 80, 80, 160, 160, 160])));
        var otherImage = await Task.Run(() => InspectionPreview.CreateBitmap(new ImageFrame(2, 1, 6, [60, 60, 60, 160, 160, 160])));
        var teaching = services.GetRequiredService<TeachingViewModel>();
        var moves = 0;
        services.GetRequiredService<InspectionGantry>().Feedback.PositionChanged += (_, _, _) => moves++;
        teaching.CarrierImages = metadata.Select(tile => new CarrierImageTileView(tile, tile.Number == 1 ? image : otherImage)).ToArray();
        var first = teaching.FilteredPoints.Single(point => point.BoltNumber == 1);
        var second = teaching.FilteredPoints.Single(point => point.BoltNumber == 2);
        var untaught = teaching.FilteredPoints.Single(point => point.BoltNumber == 3);
        var barcode = teaching.FilteredPoints.Single(point => point.Position.Target == TeachingTarget.DataMatrix);

        teaching.SelectedPoint = first;
        Assert.Same(metadata[0], teaching.SelectedFov!.Metadata);
        Assert.Same(image, teaching.Preview.Image);
        Assert.Equal((10, 20), (first.X, first.Y));
        Assert.Equal(new System.Windows.Rect(0, 0, 2, 1), teaching.FovRegion);
        Assert.Equal("OK · Bright 50%", teaching.Preview.Result);
        Assert.Equal(128, teaching.Preview.BrightnessThreshold); // Legacy recipe fallback.
        teaching.Preview.BrightnessThreshold = 100;
        teaching.Preview.MinimumBrightPercent = 75;
        Assert.Equal("NG · Bright 50%", teaching.Preview.Result);
        Assert.Equal(128, recipe.BoltInspection.BrightnessThreshold);
        Assert.Equal(0.5, recipe.BoltInspection.MinimumBrightRatio);
        teaching.FovRegion = new(1, 0, 1, 1);
        Assert.Equal("OK · Bright 100%", teaching.Preview.Result);
        Assert.Equal(new PixelRegion(0, 0, 2, 1), metadata[0].Region); // Preview does not save a draft ROI.

        teaching.SelectedPoint = second;
        Assert.Same(metadata[1], teaching.SelectedFov!.Metadata);
        Assert.Same(otherImage, teaching.Preview.Image);
        Assert.Equal(200, teaching.Preview.BrightnessThreshold);
        Assert.Equal(50, teaching.Preview.MinimumBrightPercent);
        Assert.Equal("NG · Bright 0%", teaching.Preview.Result);
        Assert.Equal(1, teaching.Preview.Overlay!.PixelWidth);
        var black = InspectionPreview.CreateFrame(teaching.Preview.Overlay);
        Assert.All(black.Pixels, pixel => Assert.Equal(0, pixel));
        teaching.Preview.BrightnessThreshold = 150;
        Assert.Equal("OK · Bright 100%", teaching.Preview.Result);

        teaching.SelectedPoint = barcode;
        Assert.Same(metadata[2], teaching.SelectedFov!.Metadata);
        Assert.Equal(new System.Windows.Rect(0, 0, 1, 1), teaching.FovRegion);
        Assert.False(teaching.IsBoltSelected);
        Assert.True(teaching.IsDataMatrixSelected);
        Assert.Null(teaching.Preview.Overlay);
        Assert.Null(teaching.Preview.Result);
        teaching.SelectedPoint = untaught;
        Assert.Null(teaching.SelectedFov);
        Assert.Null(teaching.Preview.Image);
        Assert.Null(teaching.FovRegion);

        teaching.SelectedPoint = first;
        Assert.Same(image, teaching.Preview.Image);
        Assert.Equal(100, teaching.Preview.BrightnessThreshold);
        Assert.Equal(75, teaching.Preview.MinimumBrightPercent);
        Assert.Equal("NG · Bright 50%", teaching.Preview.Result);
        teaching.SelectedFov = teaching.CarrierImages[3];
        Assert.Equal(HeatSinkSlot.HeatSink2, teaching.SelectedPcb);
        Assert.Equal(1, teaching.SelectedPoint!.BoltNumber);
        Assert.Equal(50, teaching.Preview.BrightnessThreshold);
        Assert.Equal("OK · Bright 100%", teaching.Preview.Result);
        Assert.Equal((70, 80), (teaching.SelectedPoint.X, teaching.SelectedPoint.Y));
        Assert.Equal(0, moves);
        Assert.Null(teaching.CameraError);
        Assert.Throws<ArgumentOutOfRangeException>(() => teaching.Preview.BrightnessThreshold = 256);
        Assert.Throws<ArgumentOutOfRangeException>(() => teaching.Preview.MinimumBrightPercent = double.NaN);

        await services.GetRequiredService<MachineController>().InitializeAsync();
        var draft = new CarrierImageTileView(new() { Number = 5, Center = new() { X = 90, Y = 100 } }, image);
        teaching.CarrierImages = [.. teaching.CarrierImages, draft];
        teaching.SelectedFov = draft;
        teaching.FovRegion = new(1, 0, 1, 1);
        Assert.True(teaching.AddBoltPointCommand.CanExecute(null));
        teaching.AddBoltPointCommand.Execute(null);
        Assert.Equal(HeatSinkSlot.HeatSink2, teaching.SelectedPcb);
        Assert.Equal(2, teaching.SelectedPoint!.BoltNumber);
        Assert.Same(draft, teaching.SelectedFov);
        Assert.Equal(new System.Windows.Rect(1, 0, 1, 1), teaching.FovRegion);
    }

    [Fact]
    public async Task TeachingUnitSelectionOwnsHandoffAxesAndCancelsJog()
    {
        await using var services = CreateServices(FlowSettings());
        var machine = services.GetRequiredService<MachineController>();
        var teaching = services.GetRequiredService<TeachingViewModel>();
        var supply = services.GetRequiredService<PcbSupplyHandler>();
        var placement = services.GetRequiredService<PcbPlacementHandler>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        Assert.True(services.GetRequiredService<MachineState>().Homed,
            services.GetRequiredService<MachineState>().AlarmDetail);
        teaching.SelectedTeachingUnit = HardwareArea.PcbSupply;
        Assert.All(teaching.FilteredPoints, point => Assert.Equal(MotionGroup.PcbSupply, point.Position.MotionGroup));
        teaching.JogSpeed = 1;
        await WaitUntilAsync(() => teaching.JogCommand.CanExecute(TeachingDirection.XPlus));
        var jog = teaching.JogCommand.ExecuteAsync(TeachingDirection.XPlus);
        Assert.True(supply.Feedback.IsMoving);

        teaching.SelectedPoint = teaching.FilteredPoints.Single(
            point => point.Position.Target == TeachingTarget.SupplyBufferHandoff);
        await jog.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(supply.Feedback.IsMoving);
        Assert.Equal(MotionGroup.PcbSupply, teaching.ActiveMotionGroup);
        Assert.Same(supply.Feedback, teaching.Motion.Feedback);
        Assert.Contains(OutputIo.PcbSupplyGripperClosed, TeachingRows(teaching).Keys);

        await WaitUntilAsync(() => teaching.JogCommand.CanExecute(TeachingDirection.XPlus));
        jog = teaching.JogCommand.ExecuteAsync(TeachingDirection.XPlus);
        Assert.True(supply.Feedback.IsMoving);
        teaching.SelectedTeachingUnit = HardwareArea.PcbPlacementHandler;
        await jog.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(supply.Feedback.IsMoving);
        Assert.False(placement.Feedback.IsMoving);
        Assert.Equal(MotionGroup.PcbPlacementHandler, teaching.ActiveMotionGroup);
        Assert.All(teaching.FilteredPoints, point => Assert.Equal(MotionGroup.PcbPlacementHandler, point.Position.MotionGroup));
        teaching.SelectedPoint = teaching.FilteredPoints.Single(
            point => point.Position.Target == TeachingTarget.PlacementBufferHandoff);
        Assert.Equal(HardwareArea.PcbPlacementHandler, teaching.SelectedTeachingUnit);
        Assert.Same(placement.Feedback, teaching.Motion.Feedback);
        Assert.Contains(OutputIo.PcbPlacementIpmGripperClose, TeachingRows(teaching).Keys);
        Assert.DoesNotContain(OutputIo.PcbSupplyGripperClosed, TeachingRows(teaching).Keys);

        teaching.SelectedTeachingUnit = HardwareArea.InspectionGantry;
        Assert.True(teaching.IsInspectionSelected);
        Assert.Equal(MotionGroup.InspectionGantry, teaching.ActiveMotionGroup);
        Assert.False(services.GetRequiredService<OperationCancellation>().HasActiveOperations);
    }

    [Fact]
    public async Task PlacementHandoffTeachingRequiresXyzHomeAndKeepsTheCommonZStaged()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.PcbPlacement);
        await using var services = CreateMotionScopeServices(settings, out var probes);
        var machine = services.GetRequiredService<MachineController>();
        var teaching = services.GetRequiredService<TeachingViewModel>();
        var placement = services.GetRequiredService<PcbPlacementHandler>();
        await machine.InitializeAsync();
        var probe = probes[MotionGroup.PcbPlacementHandler];
        teaching.SelectedTeachingUnit = HardwareArea.PcbPlacementHandler;
        Assert.DoesNotContain(teaching.FilteredPoints, point => point.Position.Target == TeachingTarget.SafeZ);
        var handoff = teaching.FilteredPoints.Single(point => point.Position.Target == TeachingTarget.PlacementBufferHandoff);
        teaching.SelectedPoint = handoff;
        await placement.MoveAxisAsync(MotionAxis.Z, 7);
        await WaitUntilAsync(() => teaching.IsTeachingEditAllowed && teaching.Motion.Axes[MotionAxis.Z].State is not null);
        var originalZ = settings.PcbPlacementHandler.BufferHandoffPosition.Z;
        try
        {
            Assert.False(teaching.TeachCurrentPositionCommand.CanExecute(null));
            await teaching.TeachCurrentPositionCommand.ExecuteAsync(null);
            Assert.Equal(originalZ, settings.PcbPlacementHandler.BufferHandoffPosition.Z);

            // The common Z is part of the receiving XYZ, so teaching requires all three axes.
            Assert.True(await placement.HomeAxisAsync(MotionAxis.Z));
            Assert.False(placement.Feedback.GetAxisState(MotionAxis.X).Homed);
            await placement.MoveAxisAsync(MotionAxis.Z, 7);
            Assert.False(teaching.TeachCurrentPositionCommand.CanExecute(null));
            await teaching.TeachCurrentPositionCommand.ExecuteAsync(null);
            Assert.Equal(originalZ, handoff.Z);

            await placement.MoveToHorizontalZAsync();
            Assert.True(await placement.HomeHorizontalAsync());
            await placement.MoveAxisAsync(MotionAxis.Z, 7);
            await WaitUntilAsync(() => teaching.TeachCurrentPositionCommand.CanExecute(null));
            await teaching.TeachCurrentPositionCommand.ExecuteAsync(null);
            Assert.Equal(7, handoff.Z);
            Assert.Equal(originalZ, settings.PcbPlacementHandler.BufferHandoffPosition.Z);
            await teaching.SaveHandoffSetupCommand.ExecuteAsync(null);
            Assert.Equal(7, settings.PcbPlacementHandler.BufferHandoffPosition.Z);
            Assert.True(placement.IsAtHorizontalZ());
            Assert.Null(teaching.SaveError);

            await placement.MoveAxisAsync(MotionAxis.Z, 9);
            await WaitUntilAsync(() => teaching.TeachCurrentPositionCommand.CanExecute(null));
            probe.OverrideState = state => state with { Homed = false };
            await teaching.TeachCurrentPositionCommand.ExecuteAsync(null);

            Assert.Equal(7, handoff.Z);
            Assert.Equal(7, settings.PcbPlacementHandler.BufferHandoffPosition.Z);
            Assert.Equal(7, services.GetRequiredService<MachineStore>().LoadSettings()
                .Get<PcbPlacementHandlerSettings>().BufferHandoffPosition.Z);
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
        await WaitUntilAsync(() => !gripper.ToggleOutputCommand.CanExecute(null));
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        var handler = services.GetRequiredService<PcbSupplyHandler>();
        Assert.True(state.Homed, state.AlarmDetail);
        var rotation = TeachingRows(teaching)[OutputIo.PcbSupplyRotate];
        var wasRotated = io.GetOutput(OutputIo.PcbSupplyRotate);
        await handler.MoveAxisAsync(MotionAxis.Z, 5);
        await WaitUntilAsync(() => rotation.ToggleOutputCommand.CanExecute(null));
        await rotation.ToggleOutputCommand.ExecuteAsync(null);
        Assert.Equal(0, handler.Feedback.GetPosition().Z);
        Assert.Equal(wasRotated ? PcbSupplyRotationState.Unrotated : PcbSupplyRotationState.Rotated, handler.Rotation);
        await rotation.ToggleOutputCommand.ExecuteAsync(null);
        await handler.MoveAxisAsync(MotionAxis.X, 80);
        await WaitUntilAsync(() => !rotation.ToggleOutputCommand.CanExecute(null));
        await WaitUntilAsync(() => !teaching.StepCommand.CanExecute(TeachingDirection.ZPlus));
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

        var beforeSelection = handler.Feedback.GetPosition();
        var releasing = gripper.ToggleOutputCommand.ExecuteAsync(null);
        Assert.False(releasing.IsCompleted);
        teaching.SelectedTeachingUnit = HardwareArea.PcbPlacementHandler;
        await releasing.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(MotionGroup.PcbPlacementHandler, teaching.SelectedPoint!.Position.MotionGroup);
        Assert.Equal(beforeSelection, handler.Feedback.GetPosition());
        Assert.False(io.GetOutput(gripper.Io.Signal));
        Assert.True(io.GetInput(feedback.OnInput));
        Assert.Equal(MachineAlarm.None, state.Alarm);
        await WaitUntilAsync(() => !gripper.ToggleOutputCommand.CanExecute(null));

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
            var rotation = teaching.TeachingIoGroups.SelectMany(group => group.Outputs)
                .Single(row => row.Io.Signal == OutputIo.PcbPlacementHandlerRotate);
            Assert.False(rotation.ToggleOutputCommand.CanExecute(null));
            await rotation.ToggleOutputCommand.ExecuteAsync(null);
            var output = new OutputWindowRow(rotation.Io, machine);
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
        await services.GetRequiredService<PcbPlacementHandler>().MoveToHorizontalZAsync();
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
        foreach (var output in new[] { OutputIo.PickupHeadDown, OutputIo.ShootingHeadDown })
        {
            var head = TeachingRows(teaching)[output];
            await head.ToggleOutputCommand.ExecuteAsync(null);
            Assert.True(io.GetOutput(output));
            Assert.False(services.GetRequiredService<BoltFasteningGantry>().IsHorizontalMoveAllowed);
            await WaitUntilAsync(() => teaching.StepCommand.CanExecute(TeachingDirection.XPlus));
            await head.ToggleOutputCommand.ExecuteAsync(null);
            Assert.False(io.GetOutput(output));
            Assert.True(services.GetRequiredService<BoltFasteningGantry>().IsHorizontalMoveAllowed);
        }

        teaching.SelectedTeachingUnit = HardwareArea.NgCarrierTransfer;
        var ngGripper = TeachingRows(teaching)[OutputIo.NgCarrierGripperClose];
        await ngGripper.ToggleOutputCommand.ExecuteAsync(null);
        Assert.True(io.GetOutput(ngGripper.Io.Signal));
        Assert.True(io.GetInput(InputIo.NgCarrierGripperClosed));
        await ngGripper.ToggleOutputCommand.ExecuteAsync(null);
        Assert.False(io.GetOutput(ngGripper.Io.Signal));
        Assert.True(io.GetInput(InputIo.NgCarrierGripperOpen));

        var ngLift = TeachingRows(teaching)[OutputIo.NgCarrierPickupDown];
        settings.Units.NgCarrierTransfer = false;
        await WaitUntilAsync(() => ngLift.ToggleOutputCommand.CanExecute(null));
        settings.Units.NgCarrierTransfer = true;
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
        teaching.SelectedTeachingUnit = HardwareArea.NgCarrierTransfer;
        Assert.Equal(HomeBlockReason.None, teaching.HomeBlock);
        await WaitUntilAsync(() => teaching.MotionHint == TeachingMotionHint.HomeRequired);
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
            (HardwareArea.NgCarrierTransfer, OutputIo.InspectionStopperUp,
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
        var supply = services.GetRequiredService<PcbSupplyHandler>();
        var placement = services.GetRequiredService<PcbPlacementHandler>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        try
        {
            io.SetInput(InputIo.PcbSupplyPcbDetected, true);
            Assert.True(machine.IsHomeAllowed);
            await machine.HomeAsync(CancellationToken.None);
            await supply.MoveToHandoffAsync(CancellationToken.None);
            await placement.MoveAboveBufferAsync();
            Assert.True(state.Buffer.IsSupplyAtHandoff());
            Assert.True(state.Buffer.IsPlacementAtHandoff());

            foreach (var group in new[] { HardwareArea.PcbSupply, HardwareArea.PcbPlacementHandler })
            {
                teaching.SelectedTeachingUnit = group;
                await WaitUntilAsync(() => teaching.StepCommand.CanExecute(TeachingDirection.XPlus));
                var feedback = group == HardwareArea.PcbSupply ? supply.Feedback : placement.Feedback;
                var expectedX = feedback.GetPosition().X + teaching.StepDistance;

                await teaching.StepCommand.ExecuteAsync(TeachingDirection.XPlus);

                Assert.Equal(expectedX, feedback.GetPosition().X, 6);
                Assert.Equal(MachineAlarm.None, state.Alarm);
            }

            var supplyPosition = supply.Feedback.GetPosition();
            var manual = services.GetRequiredService<MotionWindowViewModel>();
            var placementX = manual.Axes.Single(
                row => row.Group == MotionGroup.PcbPlacementHandler && row.Axis == MotionAxis.X);
            await WaitUntilAsync(() => placementX.HomeCommand.CanExecute(null));
            await placementX.HomeCommand.ExecuteAsync(null);
            Assert.Equal(0, placement.Feedback.GetPosition().X);

            await WaitUntilAsync(() => teaching.HomeCommand.CanExecute(null));
            await teaching.HomeCommand.ExecuteAsync(null);
            Assert.Equal((0, 0, settings.PcbPlacementHandler.BufferHandoffPosition.Z), placement.Feedback.GetPosition());
            Assert.Equal(supplyPosition, supply.Feedback.GetPosition());
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
        settings.Units.NgCarrierTransfer = false;
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
        await supply.MoveAxisAsync(MotionAxis.X, settings.PcbSupply.BufferHandoffPosition.X, 1_000);
        teaching.SelectedTeachingUnit = HardwareArea.PcbPlacementHandler;
        Assert.Equal(settings.PcbSupply.BufferHandoffPosition.X, supply.GetPosition().X);
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

    // Runs inside the existing WPF test host; this view model requires its UI Dispatcher.
    internal async Task VerifyTeachingSelectionIgnoresLateCameraCompletionAsync(bool cameraFails)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.Inspection);
        var capturing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        await using var services = new ServiceCollection()
            .AddSingleton(VirtualTest.OpenMachineStore())
            .AddIbtmApplication(settings)
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
        TeachInspectionFovs(settings, services.GetRequiredService<RecipeManager>().Current);
        var machine = services.GetRequiredService<MachineController>();
        var teaching = services.GetRequiredService<TeachingViewModel>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        teaching.SelectedPoint = teaching.FilteredPoints.Single(
            point => point.Position.Target == TeachingTarget.DataMatrix);
        await WaitUntilAsync(() => teaching.CaptureInspectionCommand.CanExecute(null));
        var next = teaching.FilteredPoints.Single(
            point => point.Position.Target == TeachingTarget.CarrierUpperLeftLocatingPin);
        var recipeBefore = JsonSerializer.Serialize(teaching.Recipes.Current);
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
        Assert.Equal(recipeBefore, JsonSerializer.Serialize(teaching.Recipes.Current));
        Assert.Equal(settingsBefore, JsonSerializer.Serialize(settings));
        Assert.False(services.GetRequiredService<OperationCancellation>().HasActiveOperations);
    }

    [Fact]
    public async Task CancelledInspectionCaptureKeepsPreviewAfterWaitingForLiveStop()
    {
        var settings = FlowSettings();
        await using var services = new ServiceCollection()
            .AddSingleton(_ => VirtualTest.OpenMachineStore())
            .AddIbtmApplication(settings)
            .BuildServiceProvider();
        TeachInspectionFovs(settings, services.GetRequiredService<RecipeManager>().Current);
        var machine = services.GetRequiredService<MachineController>();
        var teaching = services.GetRequiredService<TeachingViewModel>();
        var inspector = services.GetRequiredService<BoltInspector>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        teaching.SelectedPoint = teaching.FilteredPoints.Single(
            point => point.Position.Target == TeachingTarget.DataMatrix);
        await WaitUntilAsync(() => teaching.CaptureInspectionCommand.CanExecute(null));
        var frame = await services.GetRequiredService<ICamera>().CaptureAsync();
        await teaching.Preview.SetImageAsync(frame, CancellationToken.None);
        var image = teaching.Preview.Image;
        var stopped = false;
        void CancelCapture()
        {
            inspector.LiveViewChanged -= CancelCapture;
            stopped = true;
            teaching.CaptureInspectionCommand.Cancel();
        }

        inspector.LiveViewChanged += CancelCapture;
        try
        {
            await teaching.CaptureInspectionCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(2));

            Assert.True(stopped);
            Assert.True(teaching.Preview.HasImage);
            Assert.Same(image, teaching.Preview.Image);
            Assert.Null(teaching.CameraError);
            Assert.False(services.GetRequiredService<OperationCancellation>().HasActiveOperations);
        }
        finally
        {
            inspector.LiveViewChanged -= CancelCapture;
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task BufferSetupRequiresIdleManualControl()
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
        await WaitUntilAsync(() => teaching.SaveHandoffSetupCommand.CanExecute(null));
        teaching.FilteredPoints.Single(point => point.Position.Target == TeachingTarget.SupplyBufferHandoff)
            .Teach(70, 0, 0);
        teaching.SelectedTeachingUnit = HardwareArea.PcbPlacementHandler;
        teaching.FilteredPoints.Single(point => point.Position.Target == TeachingTarget.PlacementBufferHandoff)
            .Teach(75, 25, 0);

        teaching.SelectedTeachingUnit = HardwareArea.PcbSupply;
        await teaching.TeachCurrentPositionCommand.ExecuteAsync(null);
        Assert.Equal(70, teaching.FilteredPoints.Single(
            point => point.Position.Target == TeachingTarget.SupplyBufferHandoff).X);
        teaching.SelectedTeachingUnit = HardwareArea.PcbPlacementHandler;
        Assert.Equal(75, teaching.FilteredPoints.Single(
            point => point.Position.Target == TeachingTarget.PlacementBufferHandoff).X);
        Assert.Equal(80, settings.PcbSupply.BufferHandoffPosition.X);

        using (services.GetRequiredService<OperationCancellation>().Link())
        {
            await WaitUntilAsync(() => !teaching.SaveHandoffSetupCommand.CanExecute(null));
            await teaching.SaveHandoffSetupCommand.ExecuteAsync(null);
            Assert.Equal(80, settings.PcbSupply.BufferHandoffPosition.X);
        }

        await WaitUntilAsync(() => teaching.SaveHandoffSetupCommand.CanExecute(null));

        io.SetInput(InputIo.AutoMode, false);
        await WaitUntilAsync(() => !teaching.SaveHandoffSetupCommand.CanExecute(null));
        await teaching.SaveHandoffSetupCommand.ExecuteAsync(null);
        Assert.Equal(80, settings.PcbSupply.BufferHandoffPosition.X);

        io.SetInput(InputIo.AutoMode, true);
        await WaitUntilAsync(() => teaching.SaveHandoffSetupCommand.CanExecute(null));
        settings.Units.PcbSupply = false;
        await teaching.SaveHandoffSetupCommand.ExecuteAsync(null);

        Assert.Null(teaching.SaveError);
        Assert.Equal(70, settings.PcbSupply.BufferHandoffPosition.X);
        Assert.Equal(75, settings.PcbPlacementHandler.BufferHandoffPosition.X);
        var saved = store.LoadSettings().Get<PcbSupplySettings>();
        var savedPlacement = store.LoadSettings().Get<PcbPlacementHandlerSettings>();
        Assert.Equal(70, saved.BufferHandoffPosition.X);
        Assert.Equal(75, savedPlacement.BufferHandoffPosition.X);
        Assert.Equal(25, savedPlacement.BufferHandoffPosition.Y);

        teaching.SelectedTeachingUnit = HardwareArea.PcbSupply;
        teaching.FilteredPoints.Single(point => point.Position.Target == TeachingTarget.SupplyBufferHandoff)
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
            await teaching.SaveHandoffSetupCommand.ExecuteAsync(null);
        }
        finally
        {
            teaching.PropertyChanged -= StopBeforeWriting;
        }

        Assert.Equal(80, settings.PcbSupply.BufferHandoffPosition.X);
        Assert.Equal(70, store.LoadSettings().Get<PcbSupplySettings>().BufferHandoffPosition.X);
        Assert.Contains("cancelled", teaching.SaveError);
        await WaitUntilAsync(() => teaching.SaveHandoffSetupCommand.CanExecute(null));
        await teaching.SaveHandoffSetupCommand.ExecuteAsync(null);
        Assert.Null(teaching.SaveError);
        Assert.Equal(80, store.LoadSettings().Get<PcbSupplySettings>().BufferHandoffPosition.X);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BufferSetupCancelledBeforeExecutionDoesNotApply(bool closeTeaching)
    {
        var settings = FlowSettings();
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var teaching = services.GetRequiredService<TeachingViewModel>();
        teaching.SelectedTeachingUnit = HardwareArea.PcbSupply;
        var operations = services.GetRequiredService<OperationCancellation>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        await WaitUntilAsync(() => teaching.SaveHandoffSetupCommand.CanExecute(null));
        teaching.FilteredPoints.Single(point => point.Position.Target == TeachingTarget.SupplyBufferHandoff)
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
        await teaching.SaveHandoffSetupCommand.ExecuteAsync(null);
        operations.ActivityChanged -= CancelWhenStarted;

        Assert.Equal(80, settings.PcbSupply.BufferHandoffPosition.X);
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
        var gantry = services.GetRequiredService<BoltFasteningGantry>();
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
        await Assert.ThrowsAsync<MotionInterlockException>(() => gantry.MoveToXYAsync(30, 30));
        await Assert.ThrowsAsync<MotionInterlockException>(() => gantry.HomeHorizontalAsync());

        await gantry.AdjustAxisAsync(MotionAxis.X, -56.561, 10_000);
        Assert.Equal(-56.561, gantry.Feedback.GetPosition().X, 6);
        Assert.Equal(MotionCommand.None, gantry.Feedback.Command);
        await gantry.AdjustAxisAsync(MotionAxis.X, 201, 10_000);
        using var jogStop = new CancellationTokenSource();
        var motion = services.GetRequiredKeyedService<IXyMotion>(MotionGroup.BoltFastening);
        var beyondOldMaximum = motion.JogAsync(MotionAxis.X, 10, jogStop.Token, atCurrentHeight: true);
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

    public enum TeachingStopAction
    {
        Stop,
        ChangeUnit,
        Close,
    }
}
