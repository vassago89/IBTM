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
    [Theory]
    [InlineData(true, MachineAlarm.Inspection)]
    [InlineData(false, MachineAlarm.NgCarrierTransfer)]
    public async Task ManualInspectionGantryIoFailureUsesTheEnabledUnitAlarm(
        bool inspectionEnabled,
        MachineAlarm expectedAlarm)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(inspectionEnabled ? MachineUnit.Inspection : MachineUnit.NgCarrierTransfer);
        using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        try
        {
            Assert.True(machine.CanUseManualMotion(MotionGroup.InspectionGantry));
            await machine.RunManualMotionAsync(
                MotionGroup.InspectionGantry,
                _ => Task.FromException(new IOException("Manual gantry I/O failure.")),
                CancellationToken.None,
                CancellationToken.None);
            Assert.Equal(expectedAlarm, state.Alarm);
            Assert.Equal("Manual gantry I/O failure.", state.AlarmMessage);
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task IndividualHomeReportsReadFailureBeforeMotionStarts()
    {
        using var services = CreateDisplayServices(out var feedback);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        await machine.InitializeAsync();
        var manual = services.GetRequiredService<MotionWindowViewModel>();
        var axis = manual.Axes.Single(
            row => row.Group == MotionGroup.InspectionGantry && row.Axis == MotionAxis.X);
        feedback.BeforeRead = () => throw new IOException("Home feedback read failed.");

        await manual.HomeAxisCommand.ExecuteAsync(axis);

        Assert.Equal(MachineAlarm.HomeFailed, state.Alarm);
        Assert.Contains("Home feedback read failed.", state.AlarmDetail);
        Assert.False(state.IsHoming);
        Assert.False(services.GetRequiredService<OperationCancellation>().HasActiveOperations);
        Assert.False(services.GetRequiredService<InspectionGantry>().Feedback.IsMoving);
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
            Units = EnableOnly(MachineUnit.NgCarrierTransfer),
        };
        FastHomes(settings);
        using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var gantry = services.GetRequiredService<InspectionGantry>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);

        var fault = new InvalidOperationException("Jog feedback failed.");
        var failed = 0;
        gantry.Feedback.PositionChanged += (_, _, _) =>
        {
            if (gantry.Feedback.IsMoving && Interlocked.Exchange(ref failed, 1) == 0)
            {
                io.SetOutput(OutputIo.NgConveyorRun, true);
                throw fault;
            }
        };
        var teaching = services.GetRequiredService<StationTeachingViewModel>();
        teaching.SelectedMotionGroup = MotionGroup.InspectionGantry;
        await WaitUntilAsync(() => teaching.JogCommand.CanExecute(TeachingDirection.XPlus));
        await teaching.JogCommand.ExecuteAsync(TeachingDirection.XPlus)
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(MachineAlarm.MotionUnavailable, state.Alarm);
        Assert.Contains(fault.Message, state.AlarmDetail);
        Assert.False(gantry.Feedback.IsMoving);
        Assert.False(io.GetOutput(OutputIo.NgConveyorRun));

        await machine.ResetAsync();
        Assert.Equal(MachineAlarm.None, state.Alarm);
        Assert.Null(state.AlarmDetail);
        using var stopped = new CancellationTokenSource();
        var jog = gantry.JogAsync(MotionAxis.X, 10, stopped.Token);
        Assert.True(gantry.Feedback.IsMoving);
        stopped.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => jog.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(gantry.Feedback.IsMoving);
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
        var manual = services.GetRequiredService<MotionWindowViewModel>();
        await machine.InitializeAsync();
        Assert.True(machine.CanHome);
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

        foreach (var input in new[]
        {
            InputIo.MainConveyorEntryCarrierDetected,
            InputIo.PcbPlacementCarrierPresent,
            InputIo.BoltFasteningCarrierPresent,
            InputIo.InspectionCarrierPresent,
            InputIo.MainConveyorExitCarrierDetected,
            InputIo.NgCarrierDetected,
            InputIo.NgShuttleCarrierDetected,
            InputIo.NgConveyorPosition1Occupied,
            InputIo.NgConveyorPosition2Occupied,
        })
        {
            await WaitUntilAsync(
                () => state.Display is { HomeBlock: HomeBlockReason.None, HomeableAxes.Count: > 0 });
            io.SetInput(input, true);
            Assert.Equal(HomeBlockReason.CarrierDetected, machine.HomeBlock);
            Assert.False(machine.CanHome);
            await WaitUntilAsync(
                () => state.Display is { HomeBlock: HomeBlockReason.CarrierDetected, HomeableAxes.Count: 0 });
            Assert.All(manual.Axes, axis => Assert.False(manual.HomeAxisCommand.CanExecute(axis)));
            await machine.HomeAsync(CancellationToken.None);
            await manual.HomeAxisCommand.ExecuteAsync(manual.Axes[3]);
            io.SetInput(input, false);
        }

        foreach (var (up, down, reason) in new[]
        {
            (
                InputIo.PcbPlacementHandlerUp,
                InputIo.PcbPlacementHandlerDown,
                HomeBlockReason.PlacementNotRaised),
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
        services.GetRequiredService<MachineState>().RequestDisplayRefresh();
        await WaitUntilAsync(() => manual.HomeAxisCommand.CanExecute(manual.Axes[9]));
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
        OutputIo[] cylinders = [
            OutputIo.PcbPlacementHandlerDown,
            OutputIo.PickupHeadDown,
            OutputIo.ShootingHeadDown,
            OutputIo.NgCarrierPickupDown,
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
        foreach (var motion in motions)
            motion.MovingChanged += moving => moved |= moving;
        var outputChanges = new ConcurrentQueue<OutputIo>();
        io.OutputChanged += (output, _) => outputChanges.Enqueue(output);

        io.SetInput(InputIo.NgShuttleCarrierDetected, true);
        Assert.False(machine.CanRaiseCylinders);
        await machine.RaiseCylindersAsync(CancellationToken.None);
        Assert.Empty(outputChanges);
        io.SetInput(InputIo.NgShuttleCarrierDetected, false);
        Assert.True(machine.CanRaiseCylinders);
        Assert.False(machine.CanHome);

        var raising = machine.RaiseCylindersAsync(CancellationToken.None);
        Assert.True(state.IsRunning);
        Assert.False(state.IsHoming);
        Assert.False(state.ManualSetupEnabled);
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
        };
        FastHomes(settings);
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
        };
        FastHomes(settings);
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
        await Assert.ThrowsAsync<InvalidOperationException>(() => gantry.HomeAxisAsync(MotionAxis.X));
        Assert.True(io.GetOutput(OutputIo.NgCarrierGripperClose));
        Assert.False(state.Homed);

        io.SetInput(InputIo.NgCarrierDetected, false);
        Assert.False(machine.CanHome);
        Assert.False(gantry.CanMove);
        await machine.HomeAsync(CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => gantry.HomeAxisAsync(MotionAxis.X));
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
                unsafeMovement |= !gantry.CanMove
                    || !gantry.CanHome
                    || !io.GetInput(InputIo.NgCarrierGripperOpen);
            }
        };
        await machine.HomeAsync(CancellationToken.None);
        Assert.True(state.Homed);
        Assert.False(unsafeMovement);
        Assert.True(gantry.CanMove);

        io.SetInput(InputIo.NgCarrierPickupUp, false);
        io.SetInput(InputIo.NgCarrierPickupDown, true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => gantry.JogAsync(MotionAxis.X, 10));
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await gantry.MoveToAsync(new AxisPosition { X = 20, Y = 10 }, 100));
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

    [Theory]
    [InlineData(MotionGroup.PcbPlacementHandler, InputIo.PcbPlacementHandlerUp, InputIo.PcbPlacementHandlerDown)]
    [InlineData(MotionGroup.BoltFastening, InputIo.PickupHeadUp, InputIo.PickupHeadDown)]
    [InlineData(MotionGroup.BoltFastening, InputIo.ShootingHeadUp, InputIo.ShootingHeadDown)]
    public async Task HorizontalMotionRequiresRaisedCylindersWhileZCanRetract(
        MotionGroup group,
        InputIo up,
        InputIo down)
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
        Task MoveXY()
        {
            return isPlacement ? placement.MoveToXYAsync(20, 20) : fastening.MoveToXYAsync(20, 20);
        }

        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        io.AutoResponseEnabled = false;

        io.SetInput(up, false);
        io.SetInput(down, true);
        if (isPlacement)
            await placement.MoveAxisAsync(MotionAxis.Z, 1);
        else
            await fastening.MoveZAsync(1);
        Assert.Equal(1, feedback.GetPosition().Z);
        Assert.Equal(MachineAlarm.None, state.Alarm);
        await Assert.ThrowsAsync<InvalidOperationException>(MoveXY);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => isPlacement
                ? placement.HomeAxisAsync(MotionAxis.X)
                : fastening.HomeAxisAsync(MotionAxis.X));
        if (isPlacement)
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => placement.JogAsync(MotionAxis.Y, 10));

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

        Assert.Equal(
            (20, 20, settings.PcbPlacementHandler.BufferEntryZ),
            placement.Feedback.GetPosition());
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
        teaching.SelectedPoint = teaching.FilteredPoints.Single(
            point => point.Position.Target == TeachingTarget.BoltPickup);
        Assert.Equal(TeachingSaveBehavior.BoltPickup, teaching.SaveBehavior);
        await WaitUntilAsync(() => teaching.MoveToPointCommand.CanExecute(null));

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
            movedXyWithHeadDown |= gantry.Feedback.IsMovingHorizontal
                && !gantry.CanMoveHorizontal;

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
        await WaitUntilAsync(() => teaching.ReturnFromPickupCommand.CanExecute(null));
        var stopAtSafeZ = true;
        gantry.Feedback.PositionChanged += (_, _, z) =>
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
        await WaitUntilAsync(() => teaching.MoveToPointCommand.CanExecute(null));
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
        teaching.SelectedPoint = teaching.FilteredPoints.Single(
            point => point.Position.Target == TeachingTarget.BoltPickup);
        if (returning)
            await gantry.MoveToPickupPositionAsync();
        io.AutoResponseEnabled = false;
        settings.Options.TimeoutMilliseconds = 50;

        await (returning ? teaching.ReturnFromPickupCommand : teaching.MoveToPointCommand).ExecuteAsync(null);

        Assert.Equal(MachineAlarm.BoltFastening, state.Alarm);
        Assert.False(gantry.Feedback.IsMoving);
        Assert.Equal(settings.BoltFastening.SafeZ, gantry.Feedback.GetPosition().Z);
        Assert.False(io.GetOutput(OutputIo.PickupHeadVacuumPump));
        Assert.False(io.GetOutput(OutputIo.ShootingHeadVacuumPump));
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
        if (autoMode)
            io.SetInput(InputIo.AutoMode, false);
        else
            gantry.SetServo(MotionAxis.X, false);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => jog.WaitAsync(TimeSpan.FromSeconds(2)));
        var stopped = gantry.Feedback.GetPosition();
        Assert.Equal(MotionCommand.None, gantry.Feedback.Command);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => gantry.AdjustAxisAsync(MotionAxis.X, 20, 1));
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

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gantry.MoveToXYAsync(20, 20));

        var after = gantry.Feedback.GetPosition();
        Assert.Equal(before.X, after.X);
        Assert.Equal(before.Y, after.Y);
        Assert.Equal(settings.BoltFastening.SafeZ, after.Z);
        Assert.False(gantry.Feedback.IsMoving);
        Assert.Equal(MachineAlarm.BoltFastening, state.Alarm);
    }

    [Theory]
    [InlineData(MachineUnit.PcbSupply, MotionGroup.PcbSupply)]
    [InlineData(MachineUnit.PcbPlacement, MotionGroup.PcbPlacementHandler)]
    [InlineData(MachineUnit.BoltFastening, MotionGroup.BoltFastening)]
    [InlineData(MachineUnit.Inspection, MotionGroup.InspectionGantry)]
    [InlineData(MachineUnit.NgCarrierTransfer, MotionGroup.InspectionGantry)]
    public async Task OnlyEnabledMotionsAreInitializedReadResetAndHomed(
        MachineUnit unit,
        MotionGroup group)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(unit);
        using var services = CreateMotionScopeServices(settings, out var probes);
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
        Assert.True(state.Display.Available);
        Assert.False(state.Faulted);
        Assert.True(machine.CanHome);
        await machine.HomeAsync(CancellationToken.None);
        Assert.True(state.Ready);
        await WaitUntilAsync(() => state.ManualControlsEnabled);

        probes[group].Motion.SetServo(MotionAxis.X, false);
        await WaitUntilAsync(() => machine.CanReset);
        await machine.ResetAsync();
        Assert.True(state.Ready);
        Assert.Equal(1, probes[group].ResetCalls);

        if (group == MotionGroup.BoltFastening)
        {
            var teaching = services.GetRequiredService<StationTeachingViewModel>();
            teaching.SelectedMotionGroup = group;
            teaching.StepDistance = 0.1;
            await WaitUntilAsync(() => teaching.StepCommand.CanExecute(TeachingDirection.XPlus));
            var before = probes[group].Motion.GetPosition();
            await teaching.StepCommand.ExecuteAsync(TeachingDirection.XPlus);
            Assert.Equal(before.X + 0.1, probes[group].Motion.GetPosition().X, precision: 6);
        }

        if (group is MotionGroup.PcbSupply or MotionGroup.PcbPlacementHandler)
        {
            var buffer = services.GetRequiredService<BufferStage>();
            Assert.False(buffer.CanSupplyLower);
            Assert.False(buffer.CanPlacementEnter);
        }

        foreach (var row in manual.Axes.Where(row => row.Group != group))
        {
            Assert.False(manual.ToggleServoCommand.CanExecute(row));
            Assert.False(manual.HomeAxisCommand.CanExecute(row));
            Assert.Null(row.Diagnostics.Snapshot.State);
            // Bypassing CanExecute still must not command a disabled drive.
            manual.ToggleServoCommand.Execute(row);
            await manual.HomeAxisCommand.ExecuteAsync(row);
        }

        Assert.All(
            probes.Where(item => item.Key != group),
            item => Assert.Equal(0, item.Value.HardwareCalls));
        Assert.Equal(MachineAlarm.None, state.Alarm);
    }

    [Fact]
    public async Task DisablingAFailedMotionAllowsResetWithoutHidingAnEnabledMotionFailure()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.PcbSupply);
        using var services = CreateMotionScopeServices(settings, out var probes);
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
        Assert.True(state.Faulted);
        Assert.False(state.ManualControlsEnabled);
        var placementResets = probes[MotionGroup.PcbPlacementHandler].ResetCalls;
        await machine.ResetAsync();
        Assert.Equal(MachineAlarm.MotionUnavailable, state.Alarm);
        Assert.Equal(placementResets + 1, probes[MotionGroup.PcbPlacementHandler].ResetCalls);
    }

    [Theory]
    [InlineData(InputIo.NgConveyorPosition2Occupied, true)]
    [InlineData(InputIo.PickupHeadUp, false)]
    public async Task HomeStopsWhenItsCarrierOrCylinderConditionChanges(InputIo input, bool value)
    {
        var settings = FlowSettings();
        foreach (var motionSettings in MotionSettingsOf(settings))
            motionSettings.ZHome.SearchSpeed = 20;
        using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var placement = services.GetRequiredService<PcbPlacementHandler>();
        var fastening = services.GetRequiredService<BoltFasteningGantry>();
        await machine.InitializeAsync();
        await Task.WhenAll(placement.MoveAxisAsync(MotionAxis.Z, 50), fastening.MoveZAsync(50));
        var homing = machine.HomeAsync(CancellationToken.None);
        await WaitUntilAsync(() => placement.Feedback.IsMoving && fastening.Feedback.IsMoving);
        Assert.False(state.ManualSetupEnabled);
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
        foreach (var motionSettings in MotionSettingsOf(settings))
            motionSettings.ZHome.SearchSpeed = 20;
        using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var placement = (VirtualMotionService)services.GetRequiredKeyedService<IXyMotion>(
            MotionGroup.PcbPlacementHandler);
        var fastening = (VirtualMotionService)services.GetRequiredKeyedService<IXyMotion>(
            MotionGroup.BoltFastening);
        await machine.InitializeAsync();

        fastening.SetAlarm(MotionAxis.X, true);
        Assert.False(machine.CanHome);
        await machine.ResetAsync();
        Assert.True(machine.CanHome);

        await Task.WhenAll(
            placement.MoveAxisAsync(MotionAxis.Z, 50, 10_000),
            fastening.MoveAxisAsync(MotionAxis.Z, 50, 10_000));
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
    [InlineData(true, true)]
    public async Task FailedHomeReportsCauseAndStopsOtherHomingAxes(bool exception, bool individual)
    {
        var settings = FlowSettings();
        foreach (var motionSettings in MotionSettingsOf(settings))
            motionSettings.ZHome.SearchSpeed = 1;
        HomeResultMotion? homeResult = null;
        using var services = new ServiceCollection().AddSingleton(_ => VirtualTest.OpenMachineStore())
            .AddIbtmApplication(settings)
            .AddSingleton(
                provider =>
                {
                    var motion = DispatchProxy.Create<IXyMotion, HomeResultMotion>();
                    homeResult = (HomeResultMotion)motion;
                    homeResult.Motion = provider.GetRequiredKeyedService<IXyMotion>(MotionGroup.BoltFastening);
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
        var placement = services.GetRequiredKeyedService<IXyMotion>(MotionGroup.PcbPlacementHandler);
        var supply = services.GetRequiredKeyedService<IAxisMotion>(MotionGroup.PcbSupply);
        var manual = services.GetRequiredService<MotionWindowViewModel>();
        await machine.InitializeAsync();
        await placement.MoveAxisAsync(MotionAxis.Z, 50, 10_000);

        var axisRow = manual.Axes.Single(
            row => row.Group == MotionGroup.BoltFastening && row.Axis == MotionAxis.Z);
        var homing = individual
            ? manual.HomeAxisCommand.ExecuteAsync(axisRow)
            : machine.HomeAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(
                () => individual ? state.IsHoming : placement.IsMoving && supply.IsMoving);
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

            Assert.Equal(MachineAlarm.HomeFailed, state.Alarm);
            if (exception)
                Assert.Contains("Home command failed.", state.AlarmDetail);
            await WaitUntilAsync(() => state.Display.Alarm == MachineAlarm.HomeFailed);
            Assert.False(manual.HomeAxisCommand.CanExecute(axisRow));
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
}
