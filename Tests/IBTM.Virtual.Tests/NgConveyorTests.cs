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
    [Fact]
    public void ShuttleOnlyReceiveIgnoresConveyorStateButStillRequiresRaisedEmptyShuttle()
    {
        var system = CreateSystem();
        system.Io.SetInput(InputIo.NgConveyorPosition2Occupied, true);
        Assert.False(system.Shuttle.CanReceive(useConveyor: true));
        Assert.True(system.Shuttle.CanReceive(useConveyor: false));

        system.Io.SetInput(InputIo.NgShuttleCarrierDetected, true);
        Assert.False(system.Shuttle.CanReceive(useConveyor: false));
        system.Io.SetInput(InputIo.NgShuttleCarrierDetected, false);
        system.Io.SetInput(InputIo.NgShuttleUp, false);
        system.Io.SetInput(InputIo.NgShuttleDown, true);
        Assert.False(system.Shuttle.CanReceive(useConveyor: false));
    }

    [Fact]
    public async Task InterruptedShuttleCycleStopsWithoutAdditionalCommands()
    {
        var system = CreateSystem();
        system.Io.SetInput(InputIo.NgShuttleCarrierDetected, true);
        using var stop = new CancellationTokenSource();
        var downCommands = 0;
        var upCommands = 0;
        system.Io.OutputChanged += (output, on) =>
        {
            if (output != OutputIo.NgShuttleDown)
                return;
            if (on)
                downCommands++;
            else if (++upCommands == 1)
                stop.Cancel();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => system.Shuttle.CycleAsync(stop.Token));
        Assert.Equal(1, downCommands);

        await Task.Delay(80);
        Assert.Equal(1, downCommands);
        Assert.Equal(1, upCommands);
        Assert.True(system.Shuttle.Feedback.CarrierDetected);
        Assert.False(system.Io.GetOutput(OutputIo.NgConveyorRun));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShuttleCycleRechecksCarrierAndPickupBeforeAscent(bool loseCarrier)
    {
        var system = CreateSystem();
        system.Io.SetInput(InputIo.NgShuttleCarrierDetected, true);
        var downCommands = 0;
        var upCommands = 0;
        void ChangeFeedbackWhileLowering(OutputIo output, bool on)
        {
            if (output != OutputIo.NgShuttleDown)
                return;
            if (!on)
            {
                upCommands++;
                return;
            }

            downCommands++;
            system.Io.SetInput(
                loseCarrier ? InputIo.NgShuttleCarrierDetected : InputIo.NgCarrierPickupUp,
                false);
        }

        system.Io.OutputChanged += ChangeFeedbackWhileLowering;
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => system.Shuttle.CycleAsync(CancellationToken.None));
        Assert.Equal(1, downCommands);
        Assert.Equal(0, upCommands);
        Assert.Equal(NgShuttleLiftState.Down, system.Shuttle.Feedback.Lift);

        system.Io.SetInput(
            loseCarrier ? InputIo.NgShuttleCarrierDetected : InputIo.NgCarrierPickupUp,
            true);
        await system.Shuttle.CycleAsync(CancellationToken.None);
        Assert.Equal(1, downCommands);
        Assert.Equal(1, upCommands);
        Assert.Equal(NgShuttleLiftState.Up, system.Shuttle.Feedback.Lift);
    }

    [Fact]
    public async Task NgActuatorOutputsOnLowerPickupCloseGripperAndLowerShuttle()
    {
        var system = CreateSystem();
        var pickup = new NgCarrierTransfer(system.Io);

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

        await system.Shuttle.SetDownAsync(true);
        Assert.True(system.Io.GetOutput(OutputIo.NgShuttleDown));
        Assert.True(system.Io.GetInput(InputIo.NgShuttleDown));
        Assert.False(system.Io.GetInput(InputIo.NgShuttleUp));
        await system.Shuttle.SetDownAsync(false);
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

    private static TestSystem CreateSystem(int alarmCarrierCount = 3)
    {
        var io = new VirtualIoService(
            Outputs(
                new NgShuttleHardwareSettings(),
                new NgConveyorHardwareSettings(),
                new NgCarrierTransferHardwareSettings(),
                new MachineHardwareSettings()),
            new MachineOptions());
        _ = new VirtualMachine(io, []);
        var shuttleFeedback = new NgShuttleFeedback(io);
        var conveyor = new NgCarrierConveyor(
            io,
            new NgConveyorSettings { AlarmCarrierCount = alarmCarrierCount, },
            shuttleFeedback);
        var shuttle = new NgShuttle(io, conveyor, shuttleFeedback, new NgCarrierTransfer(io));
        io.Initialize();
        return new TestSystem(io, conveyor, shuttle);
    }

    private static async Task LoadShuttleAsync(TestSystem system, bool lower = true)
    {
        await system.Signals.WaitForInputAsync(InputIo.NgShuttleUp, true);
        await system.Signals.WaitForInputAsync(InputIo.NgShuttleCarrierDetected, false);
        system.Io.SetInput(InputIo.NgShuttleCarrierDetected, true);
        if (lower)
            await system.Signals.WaitForInputAsync(InputIo.NgShuttleDown, true);
    }

    private sealed record TestSystem(VirtualIoService Io, NgCarrierConveyor Conveyor, NgShuttle Shuttle)
    {
        public IIoService Signals
        {
            get
            {
                return Io;
            }
        }

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            var conveyor = Conveyor.RunAsync(cancellationToken);
            var shuttle = Shuttle.RunAsync(cancellationToken);
            await Task.WhenAll(conveyor, shuttle);
        }
    }
}
