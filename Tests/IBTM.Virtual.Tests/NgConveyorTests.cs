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
    public async Task ShuttleCycleResumesItsAscentAfterStopWithoutLoweringAgain()
    {
        var system = CreateSystem();
        system.Io.SetInput(InputIo.NgShuttleCarrierDetected, true);
        using var stop = new CancellationTokenSource();
        var downCommands = 0;
        var upCommands = 0;
        system.Io.OutputChanged += (output, on) =>
        {
            if (output != OutputIo.NgShuttleUp)
                return;
            if (!on)
                downCommands++;
            else if (++upCommands == 1)
                stop.Cancel();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => system.Shuttle.CycleAsync(stop.Token));
        Assert.Equal(1, downCommands);

        await system.Shuttle.CycleAsync(CancellationToken.None);

        Assert.Equal(1, downCommands);
        Assert.Equal(NgShuttleLiftState.Up, system.Shuttle.Feedback.Lift);
        Assert.True(system.Shuttle.Feedback.CarrierDetected);
        Assert.False(system.Io.GetOutput(OutputIo.NgConveyorRun));
    }

    [Fact]
    public async Task NgActuatorOutputsOnRaisePickupOpenGripperAndRaiseShuttle()
    {
        var system = CreateSystem();
        var pickup = new NgCarrierTransfer(system.Io);

        await pickup.SetLiftUpAsync(false);
        Assert.False(system.Io.GetOutput(OutputIo.NgCarrierPickupUp));
        Assert.Equal(NgTransferLiftState.Down, pickup.Lift);
        await pickup.SetLiftUpAsync(true);
        Assert.True(system.Io.GetOutput(OutputIo.NgCarrierPickupUp));
        Assert.Equal(NgTransferLiftState.Up, pickup.Lift);

        await pickup.SetGripperOpenAsync(false);
        Assert.False(system.Io.GetOutput(OutputIo.NgCarrierGripperOpen));
        Assert.Equal(NgTransferGripperState.Closed, pickup.Gripper);
        await pickup.SetGripperOpenAsync(true);
        Assert.True(system.Io.GetOutput(OutputIo.NgCarrierGripperOpen));
        Assert.Equal(NgTransferGripperState.Open, pickup.Gripper);

        await system.Shuttle.SetUpAsync(false);
        Assert.False(system.Io.GetOutput(OutputIo.NgShuttleUp));
        Assert.True(system.Io.GetInput(InputIo.NgShuttleDown));
        Assert.False(system.Io.GetInput(InputIo.NgShuttleUp));
        await system.Shuttle.SetUpAsync(true);
        Assert.True(system.Io.GetOutput(OutputIo.NgShuttleUp));
        Assert.True(system.Io.GetInput(InputIo.NgShuttleUp));
        Assert.False(system.Io.GetInput(InputIo.NgShuttleDown));
    }

    [Fact]
    public async Task NgConveyorEjectsAfterStopperInputFeedback()
    {
        var system = CreateSystem();
        var settings = new NgConveyorHardwareSettings();
        var hardware = settings.Outputs[OutputIo.NgConveyorStopperDown];
        Assert.Equal(70, hardware.Number);
        Assert.Equal(71, hardware.OffNumber);
        Assert.Equal(InputIo.NgConveyorStopperDown, hardware.Feedback!.OnInput);
        Assert.Equal(InputIo.NgConveyorStopperUp, hardware.Feedback.OffInput);
        Assert.Equal(87, settings.Inputs[hardware.Feedback.OnInput]);
        Assert.Equal(88, settings.Inputs[hardware.Feedback.OffInput]);
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
        Assert.False(system.Io.GetOutput(OutputIo.NgConveyorStopperDown));
        Assert.True(system.Io.GetInput(InputIo.NgConveyorStopperUp));
        Assert.False(system.Io.GetInput(InputIo.NgConveyorStopperDown));
        Assert.Equal(NgConveyorState.WaitingForEjectConfirmation, system.Conveyor.State);
        stop.Cancel();
        await run;
    }

    [Fact]
    public async Task StoppedCompactionNeedsPresenceFeedbackBeforeResuming()
    {
        var system = CreateSystem();
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
        Assert.Equal(NgConveyorState.CarrierPositionUnknown, system.Conveyor.State);

        using var resumed = new CancellationTokenSource();
        var run = system.Conveyor.RunAsync(resumed.Token);
        Assert.False(system.Conveyor.RunCommandOn);
        system.Io.SetInput(InputIo.NgConveyorPosition1Occupied, true);
        await WaitForOutputAsync(system.Io, OutputIo.NgConveyorRun, false);
        resumed.Cancel();
        await run;
        Assert.Equal(NgConveyorState.ReadyToEject, system.Conveyor.State);
    }

    [Fact]
    public async Task StopDuringMotorSetupCannotTurnRunBackOn()
    {
        var system = CreateSystem();
        await system.Signals.SetOutputAndWaitAsync(OutputIo.NgShuttleUp, false);
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
        using var resumed = new CancellationTokenSource();
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
            runs = system.RunAsync(resumed.Token);
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
        resumed.Cancel();
        await runs;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResumesSelectedDestinationAfterStop(bool stopAtDestination)
    {
        var system = CreateSystem();
        await system.Signals.SetOutputAndWaitAsync(OutputIo.NgShuttleUp, false);
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
            if (stopAtDestination && input == InputIo.NgConveyorPosition1Occupied && value)
            {
                stop.Cancel();
            }
        };

        await system.Conveyor.RunAsync(stop.Token);

        using var resumed = new CancellationTokenSource();
        var runs = system.RunAsync(resumed.Token);
        await system.Signals.WaitForInputAsync(InputIo.NgConveyorPosition1Occupied, true);
        await system.Signals.WaitForInputAsync(InputIo.NgShuttleUp, true);

        resumed.Cancel();
        await runs;

        Assert.False(system.Io.GetOutput(OutputIo.NgConveyorRun));
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
        var shuttle = new NgShuttle(io, conveyor, shuttleFeedback, new TestNgCarrierTransferFeedback(io));
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
