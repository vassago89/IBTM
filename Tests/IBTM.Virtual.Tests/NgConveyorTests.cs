using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Device;
using IBTM.Inspection;
using IBTM.NgConveyor;
using IBTM.Virtual;
using Xunit;
using static IBTM.Virtual.Tests.VirtualTest;

namespace IBTM.Virtual.Tests;

public sealed class NgConveyorTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OneEnableControlsShuttleAndBelt(bool enabled)
    {
        var system = CreateSystem(units: new() { NgConveyor = enabled });
        system.Io.SetInput(InputIo.NgShuttleCarrierDetected, true);
        var shuttleCommands = 0;
        var beltStarts = 0;
        system.Io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.NgShuttleDown && on)
                shuttleCommands++;
            if (output == OutputIo.NgConveyorRun && on)
                beltStarts++;
        };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var run = system.Conveyor.RunAsync(stop.Token);
        try
        {
            if (enabled)
                await system.Signals.WaitForInputAsync(InputIo.NgConveyorPosition1Occupied, true, stop.Token);
            else
                Assert.Equal(NgConveyorState.WaitingForCarrier, system.Conveyor.State);
            Assert.Equal(enabled ? 1 : 0, shuttleCommands);
            Assert.Equal(enabled ? 1 : 0, beltStarts);
        }
        finally
        {
            stop.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(1));
        }
    }

    [Fact]
    public void ReceiveRequiresAvailableConveyorAndRaisedEmptyShuttle()
    {
        var system = CreateSystem();
        Assert.True(system.Conveyor.IsReceiveAllowed());
        system.Io.SetInput(InputIo.NgConveyorPosition2Occupied, true);
        Assert.False(system.Conveyor.IsReceiveAllowed());
        system.Io.SetInput(InputIo.NgConveyorPosition2Occupied, false);
        Assert.True(system.Conveyor.IsReceiveAllowed());
        system.Io.SetInput(InputIo.NgShuttleCarrierDetected, true);
        Assert.False(system.Conveyor.IsReceiveAllowed());
        system.Io.SetInput(InputIo.NgShuttleCarrierDetected, false);
        system.Io.SetInput(InputIo.NgShuttleUp, false);
        system.Io.SetInput(InputIo.NgShuttleDown, true);
        Assert.False(system.Conveyor.IsReceiveAllowed());
    }

    [Fact]
    public async Task EmptyNgRepeatCanBeStoppedWhileWaiting()
    {
        var system = CreateSystem();
        using var stop = new CancellationTokenSource();
        var run = system.Conveyor.RunRepeatAsync(stop.Token);

        Assert.False(run.IsCompleted);
        Assert.False(system.Io.GetOutput(OutputIo.NgShuttleDown));
        Assert.False(system.Conveyor.RunCommandOn);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => run.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task EmptyNgRepeatWaitsThenStartsWhenACarrierArrives()
    {
        var system = CreateSystem();
        using var stop = new CancellationTokenSource();
        var lowering = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        system.Io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.NgShuttleDown && on)
                lowering.TrySetResult();
        };
        var run = system.Conveyor.RunRepeatAsync(stop.Token);
        try
        {
            Assert.False(run.IsCompleted);
            Assert.False(system.Io.GetOutput(OutputIo.NgShuttleDown));
            Assert.False(system.Conveyor.RunCommandOn);

            system.Io.SetInput(InputIo.NgShuttleCarrierDetected, true);
            await lowering.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => run.WaitAsync(TimeSpan.FromSeconds(2)));
        }
    }

    [Fact]
    public async Task NgReverseReturnLowersShuttleBeforeStartingBelt()
    {
        var system = CreateSystem();
        system.Io.SetInput(InputIo.NgConveyorPosition1Occupied, true);
        var beltStarted = false;
        system.Io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.NgConveyorRun && on)
            {
                Assert.Equal(NgShuttleLiftState.Down, system.Conveyor.ShuttleLift);
                beltStarted = true;
            }
        };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await system.Conveyor.ReturnFromConveyorAsync(stop.Token);
        Assert.True(beltStarted);
        Assert.False(system.Conveyor.RunCommandOn);
        Assert.True(system.Conveyor.Position3Occupied);
        Assert.Equal(NgShuttleLiftState.Up, system.Conveyor.ShuttleLift);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NgReverseReturnUsesShuttleFeedbackWithoutCountingCarriers(bool severalOccupiedSensors)
    {
        var system = CreateSystem();
        await system.Conveyor.SetShuttleDownAsync(true);
        await system.Signals.SetOutputAndWaitAsync(OutputIo.NgConveyorStopperUp, false);
        system.Io.AutoResponseEnabled = false;
        system.Io.SetInputs(
            (InputIo.NgConveyorPosition1Occupied, severalOccupiedSensors),
            (InputIo.NgConveyorPosition2Occupied, severalOccupiedSensors));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var run = system.Conveyor.ReturnFromConveyorAsync(stop.Token);
        try
        {
            await WaitForOutputAsync(system.Io, OutputIo.NgConveyorRun, true);
            Assert.True(system.Io.GetOutput(OutputIo.NgConveyorReverse));
            Assert.False(run.IsCompleted);
            system.Io.SetInput(InputIo.NgShuttleCarrierDetected, true);
            await WaitForOutputAsync(system.Io, OutputIo.NgShuttleDown, false);
            Assert.False(system.Conveyor.RunCommandOn);
            system.Io.SetInputs((InputIo.NgShuttleDown, false), (InputIo.NgShuttleUp, true));
            await run.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(severalOccupiedSensors, system.Io.GetInput(InputIo.NgConveyorPosition1Occupied));
            Assert.Equal(severalOccupiedSensors, system.Io.GetInput(InputIo.NgConveyorPosition2Occupied));
        }
        finally
        {
            stop.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(1));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NgReturnStopsBeforeBeltWhenCancelledOrPickupDropsDuringDescent(bool losePickupClearance)
    {
        var system = CreateSystem();
        system.Io.SetInput(InputIo.NgConveyorPosition1Occupied, true);
        using var stop = new CancellationTokenSource();
        var downCommands = 0;
        var upCommands = 0;
        var motorStarts = 0;
        system.Io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.NgConveyorRun && on)
                motorStarts++;
            if (output != OutputIo.NgShuttleDown)
                return;
            if (!on)
            {
                upCommands++;
                return;
            }
            downCommands++;
            if (losePickupClearance)
                system.Io.SetInput(InputIo.NgCarrierPickupUp, false);
            else
                stop.Cancel();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => system.Conveyor.ReturnFromConveyorAsync(stop.Token));
        Assert.Equal(1, downCommands);
        Assert.Equal(0, upCommands);
        Assert.Equal(0, motorStarts);
        Assert.False(system.Conveyor.RunCommandOn);
    }

    [Fact]
    public async Task NgActuatorOutputsOnLowerPickupCloseGripperAndLowerShuttle()
    {
        var system = CreateSystem();
        var pickup = system.Pickup;

        await pickup.SetLiftUpAsync(false);
        Assert.True(system.Io.GetOutput(OutputIo.NgCarrierPickupDown));
        Assert.Equal(NgTransferLiftState.Down, pickup.Lift);
        await pickup.SetLiftUpAsync(true);
        Assert.False(system.Io.GetOutput(OutputIo.NgCarrierPickupDown));
        Assert.Equal(NgTransferLiftState.Up, pickup.Lift);

        await pickup.SetGripperOpenAsync(false);
        Assert.True(system.Io.GetOutput(OutputIo.NgCarrierGripperClose));
        Assert.Equal(NgTransferGripperState.Closed, pickup.Gripper);
        await pickup.SetGripperOpenAsync(true);
        Assert.False(system.Io.GetOutput(OutputIo.NgCarrierGripperClose));
        Assert.Equal(NgTransferGripperState.Open, pickup.Gripper);

        await system.Conveyor.SetShuttleDownAsync(true);
        Assert.True(system.Io.GetOutput(OutputIo.NgShuttleDown));
        Assert.True(system.Io.GetInput(InputIo.NgShuttleDown));
        Assert.False(system.Io.GetInput(InputIo.NgShuttleUp));
        await system.Conveyor.SetShuttleDownAsync(false);
        Assert.False(system.Io.GetOutput(OutputIo.NgShuttleDown));
        Assert.True(system.Io.GetInput(InputIo.NgShuttleUp));
        Assert.False(system.Io.GetInput(InputIo.NgShuttleDown));
    }

    [Fact]
    public async Task NgConveyorEjectsAfterStopperInputFeedback()
    {
        var system = CreateSystem();
        var settings = new NgConveyorHardwareSettings();
        var hardware = settings.Outputs[OutputIo.NgConveyorStopperUp];
        Assert.Equal(70, hardware.Number);
        Assert.Equal(71, hardware.OffNumber);
        Assert.Equal(InputIo.NgConveyorStopperUp, hardware.Feedback!.OnInput);
        Assert.Equal(InputIo.NgConveyorStopperDown, hardware.Feedback.OffInput);
        Assert.Equal(88, settings.Inputs[hardware.Feedback.OnInput]);
        Assert.Equal(87, settings.Inputs[hardware.Feedback.OffInput!.Value]);
        system.Io.SetInput(InputIo.NgConveyorPosition1Occupied, true);
        var loweredBeforeRun = false;
        system.Io.OutputChanged += (output, value) =>
        {
            if (output == OutputIo.NgConveyorRun && value)
            {
                loweredBeforeRun = system.Io.GetInput(InputIo.NgConveyorStopperDown)
                    && !system.Io.GetInput(InputIo.NgConveyorStopperUp);
            }
        };

        using var stop = new CancellationTokenSource(System.TimeSpan.FromSeconds(3));
        var run = system.Conveyor.RunAsync(stop.Token);
        system.Io.SetInput(InputIo.NgCarrierEjectButton, true);
        await WaitForOutputAsync(system.Io, OutputIo.NgCarrierEjectCompleteLamp, true);
        Assert.True(loweredBeforeRun);
        Assert.False(system.Conveyor.Position1Occupied);
        Assert.True(system.Io.GetOutput(OutputIo.NgConveyorStopperUp));
        Assert.True(system.Io.GetInput(InputIo.NgConveyorStopperUp));
        Assert.False(system.Io.GetInput(InputIo.NgConveyorStopperDown));
        Assert.Equal(NgConveyorState.WaitingForEjectConfirmation, system.Conveyor.State);
        system.Io.SetInput(InputIo.NgCarrierEjectCompleteButton, true);
        await WaitForOutputAsync(system.Io, OutputIo.NgCarrierEjectCompleteLamp, false);
        Assert.Equal(NgConveyorState.WaitingForEjectButtonRelease, system.Conveyor.State);
        system.Io.SetInput(InputIo.NgCarrierEjectButton, false);
        system.Io.SetInput(InputIo.NgCarrierEjectCompleteButton, false);
        Assert.Equal(NgConveyorState.WaitingForCarrier, system.Conveyor.State);
        stop.Cancel();
        await run;
    }

    [Fact]
    public async Task StoppedCompactionStartsFromCurrentPresenceWithoutReset()
    {
        var system = CreateSystem();
        await system.Signals.SetOutputAndWaitAsync(OutputIo.NgConveyorStopperUp, true);
        system.Io.AutoResponseEnabled = false;
        system.Io.SetInput(InputIo.NgShuttleUp, true);
        system.Io.SetInput(InputIo.NgShuttleDown, false);
        system.Io.SetInput(InputIo.NgConveyorPosition2Occupied, true);
        using var stop = new CancellationTokenSource();
        void StopBetweenSensors(OutputIo output, bool value)
        {
            if (output == OutputIo.NgConveyorRun && value)
            {
                system.Io.SetInput(InputIo.NgConveyorPosition2Occupied, false);
                stop.Cancel();
            }
        }

        system.Io.OutputChanged += StopBetweenSensors;
        await system.Conveyor.RunAsync(stop.Token);
        system.Io.OutputChanged -= StopBetweenSensors;
        Assert.False(system.Conveyor.RunCommandOn);
        Assert.Equal(NgConveyorState.WaitingForCarrier, system.Conveyor.State);
        system.Io.SetInput(InputIo.NgConveyorPosition1Occupied, true);
        Assert.True(system.Conveyor.Position1Occupied);
        Assert.False(system.Conveyor.RunCommandOn);
        using var nextStop = new CancellationTokenSource();
        var nextRun = system.Conveyor.RunAsync(nextStop.Token);
        try
        {
            Assert.Equal(NgConveyorState.ReadyToEject, system.Conveyor.State);
            Assert.False(system.Conveyor.RunCommandOn);
        }
        finally
        {
            nextStop.Cancel();
            await nextRun.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task StopDuringMotorSetupCannotTurnRunBackOn()
    {
        var system = CreateSystem();
        await system.Signals.SetOutputAndWaitAsync(OutputIo.NgShuttleDown, true);
        system.Io.SetInput(InputIo.NgShuttleCarrierDetected, true);
        system.Io.SetOutput(OutputIo.NgConveyorReverse, true);
        using var stop = new CancellationTokenSource();
        var started = false;
        system.Io.OutputChanged += (output, value) =>
        {
            started |= output == OutputIo.NgConveyorRun && value;
            if (output == OutputIo.NgConveyorReverse && !value)
            {
                stop.Cancel();
            }
        };

        await system.Conveyor.RunAsync(stop.Token);
        Assert.True(stop.IsCancellationRequested);
        Assert.False(started);
        Assert.False(system.Conveyor.RunCommandOn);
        Assert.True(system.Io.GetOutput(OutputIo.NgConveyorNormalSpeed));
    }

    [Trait("Category", "MachineFlow")]
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StoresRearFirstEjectsAndCompacts(bool stopAfterEject)
    {
        var system = CreateSystem(alarmCarrierCount: 2);
        using var cancellation = new CancellationTokenSource();
        var runs = system.RunAsync(cancellation.Token);

        await LoadShuttleAsync(system);
        await system.Signals.WaitForInputAsync(InputIo.NgConveyorPosition1Occupied, true);

        await LoadShuttleAsync(system);
        await system.Signals.WaitForInputAsync(InputIo.NgConveyorPosition2Occupied, true);
        await WaitForOutputAsync(system.Io, OutputIo.NgCarrierEjectLamp, true);

        Assert.Equal(2, system.Conveyor.CarrierCount);
        Assert.True(system.Conveyor.AlarmRequired);
        Assert.False(system.Conveyor.Position3Occupied);

        await LoadShuttleAsync(system, lower: false);
        await system.Signals.WaitForInputAsync(InputIo.NgShuttleCarrierDetected, true);
        await system.Signals.WaitForInputAsync(InputIo.NgShuttleUp, true);

        Assert.True(system.Conveyor.Full);

        void StopAfterEject(InputIo input, bool value)
        {
            if (stopAfterEject && input == InputIo.NgConveyorPosition1Occupied && !value)
            {
                cancellation.Cancel();
            }
        }

        system.Io.InputChanged += StopAfterEject;
        system.Io.SetInput(InputIo.NgCarrierEjectButton, true);
        if (stopAfterEject)
        {
            await runs;
            Assert.False(system.Conveyor.RunCommandOn);
            Assert.True(system.Io.GetInput(InputIo.NgShuttleCarrierDetected));
            Assert.True(system.Io.GetInput(InputIo.NgShuttleUp));
            Assert.True(system.Io.GetInput(InputIo.NgShuttleCarrierDetected));
            system.Io.InputChanged -= StopAfterEject;
            return;
        }

        system.Io.InputChanged -= StopAfterEject;

        await WaitForOutputAsync(system.Io, OutputIo.NgCarrierEjectCompleteLamp, true);
        await system.Signals.WaitForInputAsync(InputIo.NgConveyorPosition2Occupied, false);
        await system.Signals.WaitForInputAsync(InputIo.NgConveyorPosition1Occupied, true);
        Assert.True(system.Io.GetInput(InputIo.NgShuttleCarrierDetected));
        Assert.True(system.Io.GetInput(InputIo.NgShuttleUp));

        Assert.Equal(2, system.Conveyor.CarrierCount);
        Assert.False(system.Io.GetOutput(OutputIo.NgCarrierEjectLamp));

        system.Io.SetInput(InputIo.NgCarrierEjectCompleteButton, true);
        await WaitForOutputAsync(system.Io, OutputIo.NgCarrierEjectCompleteLamp, false);
        system.Io.SetInput(InputIo.NgCarrierEjectButton, false);
        system.Io.SetInput(InputIo.NgCarrierEjectCompleteButton, false);
        await system.Signals.WaitForInputAsync(InputIo.NgShuttleCarrierDetected, false);
        await system.Signals.WaitForInputAsync(InputIo.NgConveyorPosition2Occupied, true);
        await WaitForOutputAsync(system.Io, OutputIo.NgCarrierEjectLamp, true);

        cancellation.Cancel();
        await runs;
    }

    [Theory]
    [InlineData(InputIo.NgConveyorPosition1Occupied, false)]
    [InlineData(InputIo.NgConveyorPosition1Occupied, true)]
    [InlineData(InputIo.NgConveyorPosition2Occupied, false)]
    public async Task InterruptedDestinationRestartsFromCurrentSensors(InputIo destination, bool stopAtDestination)
    {
        var system = CreateSystem();
        if (destination == InputIo.NgConveyorPosition2Occupied)
        {
            system.Io.SetInput(InputIo.NgConveyorPosition1Occupied, true);
        }

        await system.Signals.SetOutputAndWaitAsync(OutputIo.NgShuttleDown, true);
        system.Io.SetInput(InputIo.NgShuttleCarrierDetected, true);

        using var stop = new CancellationTokenSource();
        var motorStarts = 0;
        system.Io.OutputChanged += (output, value) =>
        {
            if (output == OutputIo.NgConveyorRun && value)
            {
                motorStarts++;
                if (!stopAtDestination)
                {
                    stop.Cancel();
                }
            }
        };
        system.Io.InputChanged += (input, value) =>
        {
            if (stopAtDestination && input == destination && value)
            {
                stop.Cancel();
            }
        };

        await system.Conveyor.RunAsync(stop.Token);

        Assert.False(system.Io.GetOutput(OutputIo.NgConveyorRun));
        Assert.Equal(1, motorStarts);
        using var nextStop = new CancellationTokenSource();
        var restarted = system.Conveyor.RunAsync(nextStop.Token);
        try
        {
            await system.Signals.WaitForInputAsync(destination, true, nextStop.Token);
        }
        finally
        {
            nextStop.Cancel();
            await restarted.WaitAsync(TimeSpan.FromSeconds(2));
        }
        Assert.False(system.Conveyor.RunCommandOn);
        Assert.Equal(stopAtDestination ? 1 : 2, motorStarts);
    }

    private static TestSystem CreateSystem(int alarmCarrierCount = 3, UnitSettings? units = null)
    {
        var io = new VirtualIoService(
            Outputs(
                new NgShuttleHardwareSettings(),
                new NgConveyorHardwareSettings(),
                new NgCarrierTransferHardwareSettings(),
                new MachineHardwareSettings()),
            new MachineOptions());
        _ = new VirtualMachine(io, []);
        units ??= new();
        var operations = new OperationCancellation();
        var motionSettings = new InspectionGantrySettings();
        var motion = new VirtualMotionService(motionSettings.Motion, operations, hasZ: false);
        var work = new InspectionWork(io, motion, new(), units);
        var conveyor = new NgCarrierConveyor(io,
            new NgConveyorSettings { AlarmCarrierCount = alarmCarrierCount }, work, units);
        var pickup = new InspectionStation(work, motion, conveyor, operations, motionSettings, new(), io, units,
            new VirtualCamera(motion.GetPosition, () => []), new VirtualLightController(), new(),
            new(OpenMachineStore(), new()));
        io.Initialize();
        return new TestSystem(io, conveyor, pickup);
    }

    private static async Task LoadShuttleAsync(TestSystem system, bool lower = true)
    {
        await system.Signals.WaitForInputAsync(InputIo.NgShuttleUp, true);
        await system.Signals.WaitForInputAsync(InputIo.NgShuttleCarrierDetected, false);
        system.Io.SetInput(InputIo.NgShuttleCarrierDetected, true);
        if (lower)
            await system.Signals.WaitForInputAsync(InputIo.NgShuttleDown, true);
    }

    private sealed record TestSystem(
            VirtualIoService Io,
            NgCarrierConveyor Conveyor,
            InspectionStation Pickup)
    {
        public IIoService Signals => Io;

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            await Conveyor.RunAsync(cancellationToken);
        }
    }
}
