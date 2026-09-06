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
    public async Task StopDuringMotorSetupCannotTurnRunBackOn()
    {
        var system = CreateSystem();
        await system.Signals.SetOutputAndWaitAsync(OutputIo.NgShuttleDown, true);
        system.Io.SetInput(InputIo.NgConveyorPosition3Occupied, true);
        using var stop = new CancellationTokenSource();
        var started = false;
        system.Io.OutputChanged += (output, value) =>
        {
            started |= output == OutputIo.NgConveyorRun && value;
            if (output == OutputIo.NgConveyorNormalSpeed && value)
            {
                stop.Cancel();
            }
        };

        await system.Conveyor.RunAsync(stop.Token);
        Assert.True(stop.IsCancellationRequested);
        Assert.False(started);
        Assert.False(system.Conveyor.RunCommandOn);
    }

    [Fact]
    public async Task StoresRearFirstEjectsAndCompacts()
    {
        var system = CreateSystem(alarmCarrierCount: 2);
        using var cancellation = new CancellationTokenSource();
        var runs = system.RunAsync(cancellation.Token);

        await LoadShuttleAsync(system);
        await system.Signals.WaitForInputAsync(
            InputIo.NgConveyorPosition1Occupied,
            true);

        await LoadShuttleAsync(system);
        await system.Signals.WaitForInputAsync(
            InputIo.NgConveyorPosition2Occupied,
            true);
        await WaitForOutputAsync(system.Io, OutputIo.Buzzer, true);

        Assert.Equal(2, system.Conveyor.CarrierCount);
        Assert.True(system.Conveyor.AlarmRequired);
        Assert.False(system.Conveyor.Position3Occupied);

        await LoadShuttleAsync(system);
        await system.Signals.WaitForInputAsync(
            InputIo.NgConveyorPosition3Occupied,
            true);
        await system.Signals.WaitForInputAsync(InputIo.NgShuttleUp, true);

        Assert.True(system.Conveyor.Full);

        system.Io.SetInput(InputIo.NgCarrierEjectButton, true);
        await WaitForOutputAsync(
            system.Io,
            OutputIo.NgCarrierEjectCompleteLamp,
            true);
        await system.Signals.WaitForInputAsync(
            InputIo.NgConveyorPosition3Occupied,
            false);

        Assert.Equal(2, system.Conveyor.CarrierCount);
        Assert.False(system.Io.GetOutput(OutputIo.Buzzer));

        system.Io.SetInput(InputIo.NgCarrierEjectCompleteButton, true);
        await WaitForOutputAsync(
            system.Io,
            OutputIo.NgCarrierEjectCompleteLamp,
            false);
        system.Io.SetInput(InputIo.NgCarrierEjectButton, false);
        system.Io.SetInput(InputIo.NgCarrierEjectCompleteButton, false);
        await WaitForOutputAsync(system.Io, OutputIo.Buzzer, true);

        cancellation.Cancel();
        await runs;
    }

    [Fact]
    public async Task ResumesSelectedDestinationAfterStop()
    {
        var system = CreateSystem();
        await system.Signals.SetOutputAndWaitAsync(
            OutputIo.NgShuttleDown,
            true);
        system.Io.SetInput(InputIo.NgShuttleCarrierDetected, true);
        system.Io.SetInput(InputIo.NgConveyorPosition3Occupied, true);

        using var stop = new CancellationTokenSource();
        system.Io.OutputChanged += (output, value) =>
        {
            if (output == OutputIo.NgConveyorRun && value)
            {
                stop.Cancel();
            }
        };

        await system.Conveyor.RunAsync(stop.Token);

        using var resumed = new CancellationTokenSource();
        var runs = system.RunAsync(resumed.Token);
        await system.Signals.WaitForInputAsync(
            InputIo.NgConveyorPosition1Occupied,
            true);
        await system.Signals.WaitForInputAsync(InputIo.NgShuttleUp, true);

        resumed.Cancel();
        await runs;

        Assert.False(system.Io.GetOutput(OutputIo.NgConveyorRun));
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
            new NgConveyorSettings
            {
                AlarmCarrierCount = alarmCarrierCount,
            },
            shuttleFeedback);
        var shuttle = new NgShuttle(
            io,
            conveyor,
            shuttleFeedback,
            new TestNgCarrierTransferFeedback(io));
        io.Initialize();
        return new TestSystem(io, conveyor, shuttle);
    }

    private static async Task LoadShuttleAsync(TestSystem system)
    {
        await system.Signals.WaitForInputAsync(InputIo.NgShuttleUp, true);
        await system.Signals.WaitForInputAsync(
            InputIo.NgShuttleCarrierDetected,
            false);
        system.Io.SetInput(InputIo.NgShuttleCarrierDetected, true);
        await system.Signals.WaitForInputAsync(InputIo.NgShuttleDown, true);
    }

    private sealed record TestSystem(
        VirtualIoService Io,
        NgCarrierConveyor Conveyor,
        NgShuttle Shuttle)
    {
        public IIoService Signals => Io;

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            var conveyor = Conveyor.RunAsync(cancellationToken);
            var shuttle = Shuttle.RunAsync(cancellationToken);
            await Task.WhenAll(conveyor, shuttle);
        }
    }
}
