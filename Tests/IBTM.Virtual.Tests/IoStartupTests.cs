using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using IBTM.Core;
using IBTM.BoltFastening;
using IBTM.Device;
using IBTM.PcbSupply;
using IBTM.Inspection;
using IBTM.UI;
using IBTM.Virtual;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IBTM.Virtual.Tests;

public sealed class IoStartupTests
{
    [Fact]
    public void MachineStopClearsBothIoBoltStartsEvenWhenOneWriteFails()
    {
        var settings = new MachineSettings();
        settings.Drivers.Bolt = BoltDriver.Io;
        using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<StartupIo>();
        io.Initialize();
        io.SetOutput(OutputIo.PickupBoltStart, true);
        io.SetOutput(OutputIo.ShootingBoltStart, true);
        var error = new IOException("Pickup START OFF failed.");
        io.BeforeOutputWrite = (output, on) =>
        {
            if (output == OutputIo.PickupBoltStart && !on)
                throw error;
        };

        var failure = Assert.Throws<AggregateException>(machine.Stop);
        Assert.Contains(error, failure.Flatten().InnerExceptions);
        Assert.True(io.GetOutput(OutputIo.PickupBoltStart));
        Assert.False(io.GetOutput(OutputIo.ShootingBoltStart));
        io.BeforeOutputWrite = null;
        machine.Stop();
        Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
        Assert.False(io.GetOutput(OutputIo.ShootingBoltStart));
    }

    [Fact]
    public async Task IoFasteningRetainsCompletedResultWhenStopWriteFails()
    {
        var settings = new MachineSettings();
        settings.Drivers.Bolt = BoltDriver.Io;
        using var services = CreateServices(settings);
        var io = services.GetRequiredService<StartupIo>();
        var raw = services.GetRequiredService<VirtualIoService>();
        var head = Assert.IsType<IoBoltHead>(services.GetRequiredKeyedService<IBoltHead>(FasteningHead.Pickup));
        io.Initialize();
        await head.SelectPresetAsync(1);
        var stopError = new IOException("START OFF failed after completion.");
        var starts = 0;
        io.BeforeOutputWrite = (output, on) =>
        {
            if (output != OutputIo.PickupBoltStart)
                return;
            if (!on)
                throw stopError;
            starts++;
        };
        var cycle = head.TightenAsync();
        raw.SetInput(InputIo.PickupBoltFasten, true);
        raw.SetInput(InputIo.PickupBoltFasten, false);
        Assert.Same(stopError, await Assert.ThrowsAsync<IOException>(() => cycle));
        Assert.True(head.HasPendingResult);
        await Assert.ThrowsAsync<InvalidOperationException>(() => head.ReadPendingResultAsync());
        io.BeforeOutputWrite = null;
        head.Stop();
        var result = await head.TightenAsync();
        Assert.True(result.Success);
        Assert.Null(result.Torque);
        Assert.Equal(BoltResultSource.IoAssumedOk, result.Source);
        Assert.Equal(1, starts);
        Assert.False(head.HasPendingResult);
    }

    [Fact]
    public async Task IoFasteningPreservesFeedbackFailureWhenStopAlsoFails()
    {
        var settings = new MachineSettings();
        settings.Drivers.Bolt = BoltDriver.Io;
        using var services = CreateServices(settings);
        var io = services.GetRequiredService<StartupIo>();
        var head = services.GetRequiredKeyedService<IBoltHead>(FasteningHead.Pickup);
        io.Initialize();
        await head.SelectPresetAsync(1);
        var readError = new IOException("FASTEN feedback unavailable.");
        var stopError = new IOException("START OFF failed.");
        io.BeforeOutputWrite = (output, on) =>
        {
            if (output != OutputIo.PickupBoltStart)
                return;
            if (!on)
                throw stopError;
            io.BeforeInputRead = input =>
            {
                if (input == InputIo.PickupBoltFasten)
                    throw readError;
            };
        };
        var failure = await Assert.ThrowsAsync<AggregateException>(() => head.TightenAsync());
        Assert.Contains(readError, failure.InnerExceptions);
        Assert.Contains(stopError, failure.InnerExceptions);
        Assert.True(head.HasPendingResult);
        Assert.Null(await head.ReadPendingResultAsync());
        io.BeforeInputRead = null;
        io.BeforeOutputWrite = null;
        ((IoBoltHead)head).Stop();
    }

    [Fact]
    public async Task TeachingStopsSequenceSmemaButLeavesDirectOutputsAvailable()
    {
        using var services = CreateServices();
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<VirtualIoService>();
        var sequenceIo = services.GetRequiredService<IIoService>();
        OutputIo[] smema = [OutputIo.PcbSupplyReadyToFront1,
            OutputIo.MainConveyorReadyToFront2, OutputIo.MainConveyorAvailableToRear];
        io.AutoResponseEnabled = false;
        foreach (var output in smema)
            io.SetOutput(output, true);
        await machine.InitializeAsync();
        try
        {
            Assert.All(smema, output => Assert.False(io.GetOutput(output)));
            io.SetInput(InputIo.AutoMode, false);
            foreach (var output in smema)
            {
                sequenceIo.SetAutomaticSmemaOutput(output, true);
                Assert.True(io.GetOutput(output));
            }
            io.SetInputs((InputIo.AutoMode, true));
            foreach (var output in smema)
            {
                Assert.False(io.GetOutput(output));
                sequenceIo.SetAutomaticSmemaOutput(output, true);
                Assert.False(io.GetOutput(output));
                io.SetOutput(output, true);
                sequenceIo.SetAutomaticSmemaOutput(output, false);
                Assert.True(io.GetOutput(output));
                io.SetOutput(output, false);
            }
            io.SetInput(InputIo.AutoMode, false);
            foreach (var output in smema)
            {
                Assert.False(io.GetOutput(output));
                sequenceIo.SetAutomaticSmemaOutput(output, true);
                Assert.True(io.GetOutput(output));
            }
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task TeachingSmemaIgnoresPeerInputsButRequiresSelectorFeedback()
    {
        using var services = CreateServices();
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<StartupIo>();
        var supply = services.GetRequiredService<PcbSupplyHandler>();
        var conveyor = services.GetRequiredService<IBTM.Conveyor.MainConveyor>();
        await machine.InitializeAsync();
        await services.GetRequiredService<MachineState>().StopDisplayUpdatesAsync();
        await services.GetRequiredService<MachineFeedbackMonitor>().StopAsync();
        services.GetRequiredService<VirtualIoService>().AutoResponseEnabled = false;
        try
        {
            io.BeforeInputRead = input =>
            {
                if (input is InputIo.PcbSupplyAvailableFromFront1
                    or InputIo.MainConveyorAvailableFromFront2
                    or InputIo.MainConveyorReadyFromRear)
                    throw new IOException("Peer SMEMA must not be read in teaching.");
            };
            supply.TestUpstreamCarrierAvailable = true;
            conveyor.TestUpstreamCarrierAvailable = true;
            conveyor.TestDownstreamReady = true;
            Assert.True(supply.UpstreamCarrierAvailable);
            Assert.True(conveyor.UpstreamCarrierAvailable);
            Assert.True(conveyor.DownstreamReady);
            supply.TestUpstreamCarrierAvailable = false;
            conveyor.TestUpstreamCarrierAvailable = false;
            conveyor.TestDownstreamReady = false;
            Assert.False(supply.UpstreamCarrierAvailable);
            Assert.False(conveyor.UpstreamCarrierAvailable);
            Assert.False(conveyor.DownstreamReady);

            io.BeforeInputRead = input =>
            {
                if (input == InputIo.AutoMode)
                    throw new IOException("Selector feedback is unavailable.");
            };
            Assert.Throws<IOException>(() => supply.UpstreamCarrierAvailable);
            Assert.Throws<IOException>(() => conveyor.UpstreamCarrierAvailable);
            Assert.Throws<IOException>(() => conveyor.DownstreamReady);
        }
        finally
        {
            io.BeforeInputRead = null;
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task PhysicalInputWithoutAValidScanIsNotOffOrCompleted()
    {
        // Construction and unavailable reads must not invoke either native SDK.
        using var physical = new PhysicalIoService(
            new IBTM.AlphaMotion.AlphaMotionController(new()),
            new IBTM.Ajin.AjinController(new()),
            new Dictionary<InputIo, int> { [InputIo.PcbPlacementHeatSink1Present] = 54 },
            new Dictionary<OutputIo, OutputHardware>(),
            new());
        IIoService io = physical;

        Assert.Throws<IOException>(() => io.GetInput(InputIo.PcbPlacementHeatSink1Present));
        Assert.Contains("no configured input address", Assert.Throws<IOException>(
            () => io.GetInput(InputIo.PcbPlacementCarrierPresent)).Message);
        await Assert.ThrowsAsync<IOException>(
            () => io.WaitForInputAsync(InputIo.PcbPlacementHeatSink1Present, false));
    }

    [Fact]
    public async Task InputFaultStopsReachableOutputsAndManualStopCanRetry()
    {
        using var services = CreateServices();
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<StartupIo>();
        var physicalOutputs = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        try
        {
            io.AllowWritesWhileUnavailable = true;
            machine.ToggleDiagnosticOutput(OutputIo.MainConveyorRun);
            Assert.True(physicalOutputs.GetOutput(OutputIo.MainConveyorRun));

            io.Disconnect(new IOException("Input controller disconnected; motor controller is still connected."));
            Assert.False(io.IsReady);
            Assert.False(physicalOutputs.GetOutput(OutputIo.MainConveyorRun));

            // Drain reads already in flight before checking the manual commands.
            await services.GetRequiredService<MachineState>().StopDisplayUpdatesAsync();
            await services.GetRequiredService<MachineFeedbackMonitor>().StopAsync();
            var readsBeforeManualCommands = io.ReadsWhileUnavailable;

            physicalOutputs.SetOutput(OutputIo.MainConveyorRun, true);
            machine.StopManualConveyor(OutputIo.MainConveyorRun);
            Assert.False(physicalOutputs.GetOutput(OutputIo.MainConveyorRun));
            Assert.Equal(OutputBlockReason.IoUnavailable, machine.ToggleDiagnosticOutput(OutputIo.MainConveyorRun));
            Assert.Equal(readsBeforeManualCommands, io.ReadsWhileUnavailable);
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScreenStopReportsOutputFailureAndStillStopsOtherDevices(bool motionWindow)
    {
        using var services = CreateServices();
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<StartupIo>();
        await machine.InitializeAsync();
        IAsyncRelayCommand stop = motionWindow
            ? services.GetRequiredService<MotionWindowViewModel>().StopCommand
            : services.GetRequiredService<OperationViewModel>().StopCommand;
        io.SetOutput(OutputIo.MainConveyorRun, true);
        io.SetOutput(OutputIo.NgConveyorRun, true);
        var outputError = new IOException("Main conveyor STOP write failed.");
        io.BeforeOutputWrite = (output, on) =>
        {
            if (output == OutputIo.MainConveyorRun && !on)
                throw outputError;
        };
        try
        {
            await stop.ExecuteAsync(null);
            Assert.False(io.GetOutput(OutputIo.NgConveyorRun));
            Assert.True(io.GetOutput(OutputIo.MainConveyorRun));
            Assert.Equal(MachineAlarm.StopFailed, state.Alarm);
            Assert.Contains(outputError.Message, state.AlarmDetail);
        }
        finally
        {
            io.BeforeOutputWrite = null;
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(InputIo.EmergencyStop1Pressed, true, MachineAlarm.EmergencyStop, false)]
    [InlineData(InputIo.AutoMode, false, MachineAlarm.StopFailed, false)]
    [InlineData(InputIo.EmergencyStop1Pressed, true, MachineAlarm.EmergencyStop, true)]
    public async Task InputTriggeredStopFailureKeepsFeedbackAndRecoveryAlive(
        InputIo input,
        bool value,
        MachineAlarm alarm,
        bool manualRun)
    {
        using var services = CreateServices();
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<StartupIo>();
        var feedback = services.GetRequiredService<MachineFeedbackMonitor>();
        await machine.InitializeAsync();
        var row = services.GetRequiredService<ManualHardwareViewModel>().Conveyors
            .Single(item => item.Io.Signal == OutputIo.MainConveyorRun);
        var running = manualRun ? row.RunCommand.ExecuteAsync(null) : Task.CompletedTask;
        if (manualRun)
            await WaitUntilAsync(() => io.GetOutput(OutputIo.MainConveyorRun));
        else
            io.SetOutput(OutputIo.MainConveyorRun, true);
        io.SetOutput(OutputIo.NgConveyorRun, true);
        var outputError = new IOException("Main conveyor STOP write failed during input notification.");
        io.BeforeOutputWrite = (output, on) =>
        {
            if (output == OutputIo.MainConveyorRun && !on)
                throw outputError;
        };
        try
        {
            io.PendingInput = (input, value);
            await WaitUntilAsync(() => feedback.Failure is not null
                || state.AlarmDetail?.Contains(outputError.Message) == true);
            await running.WaitAsync(TimeSpan.FromSeconds(2));
            if (manualRun)
                await row.StopCommand.ExecuteAsync(null);
            var scans = Volatile.Read(ref io.InputScans);
            await WaitUntilAsync(() => feedback.Failure is not null || Volatile.Read(ref io.InputScans) > scans);
            Assert.Null(feedback.Failure);
            Assert.Equal(alarm, state.Alarm);
            Assert.Contains(outputError.Message, state.AlarmDetail);
            Assert.False(io.GetOutput(OutputIo.NgConveyorRun));
            Assert.True(io.GetOutput(OutputIo.MainConveyorRun));

            // The next input scan and explicit recovery must remain usable.
            io.BeforeOutputWrite = null;
            io.PendingInput = (input, !value);
            await WaitUntilAsync(() => io.GetInput(input) == !value);
            machine.Stop();
            Assert.True(machine.CanReset);
            await machine.ResetAsync();
            Assert.False(state.IsError);
        }
        finally
        {
            io.BeforeOutputWrite = null;
            row.RunCommand.Cancel();
            await running;
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task AutomaticStopFailureKeepsFeedbackAliveAndStaysAtCommandBoundary()
    {
        using var services = CreateServices(new MachineSettings
        {
            Units = new UnitSettings
            {
                PcbSupply = false,
                PcbPlacement = false,
                PickupBoltFeeder = false,
                ShootingBoltFeeder = false,
                BoltFastening = false,
                Inspection = false,
                NgCarrierTransfer = false,
                NgShuttle = false,
                NgConveyor = false,
            },
        });
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<StartupIo>();
        var feedback = services.GetRequiredService<MachineFeedbackMonitor>();
        await machine.InitializeAsync();
        var running = Record.ExceptionAsync(() => machine.StartAsync());
        try
        {
            await WaitUntilAsync(() => state.AutomaticRunning && io.GetOutput(OutputIo.MainConveyorReadyToFront2));
            io.SetOutput(OutputIo.MainConveyorRun, true);
            io.SetOutput(OutputIo.NgConveyorRun, true);
            io.BeforeOutputWrite = (output, on) =>
            {
                if (output == OutputIo.MainConveyorRun && !on)
                    throw new IOException("Automatic conveyor STOP failed.");
            };
            io.PendingInput = (InputIo.EmergencyStop1Pressed, true);
            Assert.Null(await running.WaitAsync(TimeSpan.FromSeconds(2)));
            var scans = Volatile.Read(ref io.InputScans);
            await WaitUntilAsync(() => feedback.Failure is not null || Volatile.Read(ref io.InputScans) > scans);
            Assert.Null(feedback.Failure);
            Assert.Equal(MachineAlarm.EmergencyStop, state.Alarm);
            Assert.Contains("Automatic conveyor STOP failed.", state.AlarmDetail);
            Assert.False(io.GetOutput(OutputIo.NgConveyorRun));
            Assert.True(io.GetOutput(OutputIo.MainConveyorRun));
            io.BeforeOutputWrite = null;
            io.PendingInput = (InputIo.EmergencyStop1Pressed, false);
            await WaitUntilAsync(() => !io.GetInput(InputIo.EmergencyStop1Pressed));
        }
        finally
        {
            io.BeforeOutputWrite = null;
            machine.Stop();
            await running;
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepeatReturnKeepsOperationAndStopFailuresWithoutReplacingSafetyAlarm(bool safetyStop)
    {
        using var services = CreateServices(new MachineSettings
        {
            Units = new UnitSettings
            {
                PcbSupply = false,
                PcbPlacement = false,
                PickupBoltFeeder = false,
                ShootingBoltFeeder = false,
                BoltFastening = false,
                Inspection = false,
            },
        });
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<StartupIo>();
        var inputs = services.GetRequiredService<VirtualIoService>();
        var log = services.GetRequiredService<ApplicationLog>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        inputs.SetInput(InputIo.NgConveyorPosition1Occupied, true);
        io.SetOutput(OutputIo.NgShuttleDown, false);
        state.RepeatEnabled = true;
        // Resume the selected return phase directly; this test does not run a full route.
        typeof(MachineController).GetField("_repeatPhase", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(machine, RepeatPhase.ReturnToShuttle);
        Assert.True(machine.CanStart, machine.StartBlock.ToString());
        var returning = false;
        var runFailure = new IOException("Shuttle lowering failed during repeat return.");
        var stopFailure = new IOException("NG conveyor STOP failed during return cleanup.");
        io.BeforeOutputWrite = (output, on) =>
        {
            if (output == OutputIo.NgShuttleDown && on)
            {
                returning = true;
                if (safetyStop)
                    inputs.SetInput(InputIo.EmergencyStop1Pressed, true);
                throw runFailure;
            }
            if (returning && output == OutputIo.NgConveyorRun && !on)
                throw stopFailure;
        };
        try
        {
            await machine.StartAsync().WaitAsync(TimeSpan.FromSeconds(2));

            Assert.True(returning);
            Assert.Equal(safetyStop ? MachineAlarm.EmergencyStop : MachineAlarm.NgConveyor, state.Alarm);
            Assert.Contains(log.Snapshot(), entry => entry.Detail?.Contains(runFailure.Message) == true);
            Assert.Contains(log.Snapshot(), entry => entry.Detail?.Contains(stopFailure.Message) == true);
            Assert.False(state.AutomaticRunning);
            Assert.False(services.GetRequiredService<OperationCancellation>().HasActiveOperations);
        }
        finally
        {
            io.BeforeOutputWrite = null;
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task ManualFailureStopsRemainingOutputsAndReportsStopFailure()
    {
        using var services = CreateServices();
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<StartupIo>();
        var log = services.GetRequiredService<ApplicationLog>();
        await machine.InitializeAsync();
        var operationFailure = new IOException("Manual device command failed.");
        var stopFailure = new IOException("Main conveyor STOP failed.");
        Assert.True(state.SetupEditingEnabled);
        try
        {
            var commandFailure = await Record.ExceptionAsync(() => machine.RunTeachingEditAsync(
                _ =>
                {
                    io.SetOutput(OutputIo.MainConveyorRun, true);
                    io.SetOutput(OutputIo.NgConveyorRun, true);
                    io.BeforeOutputWrite = (output, on) =>
                    {
                        if (output == OutputIo.MainConveyorRun && !on)
                            throw stopFailure;
                    };
                    return Task.FromException(operationFailure);
                },
                CancellationToken.None,
                CancellationToken.None));

            Assert.Null(commandFailure);
            Assert.Equal(MachineAlarm.IoCommunication, state.Alarm);
            Assert.Contains(operationFailure.ToString(), state.AlarmDetail);
            Assert.Contains(log.Snapshot(), entry => entry.Detail?.Contains(stopFailure.ToString()) == true);
            Assert.True(io.GetOutput(OutputIo.MainConveyorRun));
            Assert.False(io.GetOutput(OutputIo.NgConveyorRun));
            Assert.False(services.GetRequiredService<OperationCancellation>().HasActiveOperations);
        }
        finally
        {
            io.BeforeOutputWrite = null;
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(OutputIo.MainConveyorRun, OutputIo.MainConveyorReadyToFront2)]
    [InlineData(OutputIo.NgConveyorRun, OutputIo.NgCarrierEjectCompleteLamp)]
    public async Task ConveyorCancellationRetainsFailedStopWhenCleanupSucceeds(
        OutputIo motor,
        OutputIo handshake)
    {
        using var services = CreateServices();
        var io = services.GetRequiredService<StartupIo>();
        io.Initialize();
        using var cancellation = new CancellationTokenSource();
        var run = motor == OutputIo.MainConveyorRun
            ? services.GetRequiredService<IBTM.Conveyor.MainConveyor>().RunMotorAsync(cancellation.Token)
            : services.GetRequiredService<IBTM.NgConveyor.NgCarrierConveyor>().RunMotorAsync(cancellation.Token);
        var stopFailure = new IOException("First motor STOP failed.");
        var stopAttempts = 0;
        Assert.True(io.GetOutput(motor));
        io.SetOutput(handshake, true);
        io.BeforeOutputWrite = (output, on) =>
        {
            if (output == motor && !on && Interlocked.Increment(ref stopAttempts) == 1)
                throw stopFailure;
        };
        try
        {
            var cancellationFailure = Record.Exception(cancellation.Cancel);
            var runFailure = await Record.ExceptionAsync(() => run.WaitAsync(TimeSpan.FromSeconds(2)));

            Assert.Null(cancellationFailure);
            Assert.Contains(stopFailure, Assert.IsType<AggregateException>(runFailure).Flatten().InnerExceptions);
            Assert.False(io.GetOutput(motor));
            Assert.False(io.GetOutput(handshake));
            Assert.False(services.GetRequiredService<OperationCancellation>().HasActiveOperations);
        }
        finally
        {
            io.BeforeOutputWrite = null;
            cancellation.Cancel();
            await Record.ExceptionAsync(() => run);
        }
    }

    [Theory]
    [InlineData(OutputIo.MainConveyorRun, OutputIo.MainConveyorReadyToFront2)]
    [InlineData(OutputIo.NgConveyorRun, OutputIo.NgCarrierEjectCompleteLamp)]
    public async Task ManualStopStillClearsHandshakeWhenMotorCancellationFails(
        OutputIo motor,
        OutputIo handshake)
    {
        using var services = CreateServices();
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<StartupIo>();
        await machine.InitializeAsync();
        var row = services.GetRequiredService<ManualHardwareViewModel>().Conveyors
            .Single(row => row.Io.Signal == motor);
        var run = row.RunCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => io.GetOutput(motor));
        io.SetOutput(handshake, true);
        io.BeforeOutputWrite = (output, on) =>
        {
            if (output == motor && !on)
                throw new IOException("Motor OFF failed.");
        };
        try
        {
            await row.StopCommand.ExecuteAsync(null);
            await run.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(io.GetOutput(handshake));
            Assert.Contains("Motor OFF failed.", row.ActionMessage);
            Assert.True(services.GetRequiredService<MachineState>().IsError);
            Assert.False(services.GetRequiredService<OperationCancellation>().HasActiveOperations);
        }
        finally
        {
            io.BeforeOutputWrite = null;
            row.RunCommand.Cancel();
            await run;
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SupplyReadyDoesNotSignalCompletionOnStopOrEmergency(bool emergency)
    {
        using var services = CreateServices();
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        io.AutoResponseEnabled = false;
        io.SetInput(InputIo.AutoMode, false);
        io.SetInput(InputIo.PcbSupplyAvailableFromFront1, true);
        io.SetOutput(OutputIo.PcbSupplyReadyToFront1, true);
        try
        {
            if (emergency)
                io.SetInput(InputIo.EmergencyStop1Pressed, true);
            else
                machine.Stop();

            Assert.True(io.GetOutput(OutputIo.PcbSupplyReadyToFront1));
            io.SetInput(InputIo.PcbSupplyAvailableFromFront1, false);
            machine.Stop();
            Assert.False(io.GetOutput(OutputIo.PcbSupplyReadyToFront1));
        }
        finally
        {
            io.SetInput(InputIo.EmergencyStop1Pressed, false);
            io.SetInput(InputIo.PcbSupplyAvailableFromFront1, false);
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task SupplyStopPreservesReadyWhenCarrierInputCannotBeRead()
    {
        using var services = CreateServices();
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<StartupIo>();
        var signals = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        await services.GetRequiredService<MachineState>().StopDisplayUpdatesAsync();
        await services.GetRequiredService<MachineFeedbackMonitor>().StopAsync();
        signals.AutoResponseEnabled = false;
        signals.SetInput(InputIo.AutoMode, false);
        signals.SetInput(InputIo.PcbSupplyAvailableFromFront1, true);
        signals.SetOutput(OutputIo.PcbSupplyReadyToFront1, true);
        var failure = new IOException("Supply carrier feedback is unavailable.");
        io.BeforeInputRead = input =>
        {
            if (input == InputIo.PcbSupplyAvailableFromFront1)
                throw failure;
        };
        try
        {
            var stopped = Assert.Throws<AggregateException>(machine.Stop);
            Assert.Contains(failure, stopped.Flatten().InnerExceptions);
            Assert.True(signals.GetOutput(OutputIo.PcbSupplyReadyToFront1));
        }
        finally
        {
            io.BeforeInputRead = null;
            signals.SetInput(InputIo.PcbSupplyAvailableFromFront1, false);
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(OutputIo.MainConveyorRun)]
    [InlineData(OutputIo.MainConveyorReadyToFront2)]
    [InlineData(OutputIo.NgConveyorRun)]
    [InlineData(OutputIo.NgCarrierEjectLamp)]
    public async Task StopAttemptsEveryDeviceAndPreservesWriteFailures(OutputIo failedOutput)
    {
        using var services = CreateServices();
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<StartupIo>();
        await machine.InitializeAsync();
        var writes = new List<OutputIo>();
        io.SetOutput(OutputIo.MainConveyorReadyToFront2, true);
        io.SetOutput(OutputIo.MainConveyorAvailableToRear, true);
        io.SetOutput(OutputIo.NgCarrierEjectLamp, true);
        io.SetOutput(OutputIo.NgCarrierEjectCompleteLamp, true);
        var conveyorFailure = new IOException("Main conveyor STOP failed.");
        var shootingFailure = new IOException("Shoot output OFF failed.");
        io.BeforeOutputWrite = (output, value) =>
        {
            Assert.False(value);
            writes.Add(output);
            if (output == failedOutput)
                throw conveyorFailure;
            if (output == OutputIo.ShootBolt)
                throw shootingFailure;
        };
        try
        {
            var failure = Assert.Throws<AggregateException>(machine.Stop);
            var failures = failure.Flatten().InnerExceptions;
            Assert.Equal(2, failures.Count);
            Assert.Contains(conveyorFailure, failures);
            Assert.Contains(shootingFailure, failures);
            Assert.Contains(OutputIo.MainConveyorReadyToFront2, writes);
            Assert.Contains(OutputIo.MainConveyorAvailableToRear, writes);
            Assert.False(io.GetOutput(OutputIo.MainConveyorAvailableToRear));
            Assert.Contains(OutputIo.ShootingFeederRunSignal, writes);
            Assert.Contains(OutputIo.NgConveyorRun, writes);
            Assert.Contains(OutputIo.NgCarrierEjectLamp, writes);
            Assert.Contains(OutputIo.NgCarrierEjectCompleteLamp, writes);
            Assert.False(io.GetOutput(OutputIo.NgCarrierEjectCompleteLamp));
            Assert.Contains(OutputIo.PcbSupplyReadyToFront1, writes);
        }
        finally
        {
            io.BeforeOutputWrite = null;
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(OutputIo.MainConveyorRun, true)]
    [InlineData(OutputIo.NgConveyorRun, true)]
    [InlineData(OutputIo.NgConveyorRun, false)]
    public async Task ConveyorRunFailureSurvivesOutputCleanupFailure(OutputIo motor, bool manual)
    {
        using var services = CreateServices();
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<StartupIo>();
        await machine.InitializeAsync();
        var runError = new IOException("Conveyor operation failed.");
        var stopError = new IOException("Conveyor cleanup failed.");
        var trigger = !manual
            ? OutputIo.NgCarrierEjectCompleteLamp
            : motor == OutputIo.MainConveyorRun ? OutputIo.MainConveyorForward : OutputIo.NgConveyorReverse;
        var attempted = false;
        io.BeforeOutputWrite = (output, on) =>
        {
            if (output == trigger && !attempted)
            {
                attempted = true;
                throw runError;
            }
            if (attempted && output == motor && !on)
                throw stopError;
        };
        try
        {
            var run = motor == OutputIo.MainConveyorRun
                ? services.GetRequiredService<IBTM.Conveyor.MainConveyor>().RunMotorAsync()
                : manual
                    ? services.GetRequiredService<IBTM.NgConveyor.NgCarrierConveyor>().RunMotorAsync(CancellationToken.None)
                    : services.GetRequiredService<IBTM.NgConveyor.NgCarrierConveyor>().RunAsync();
            var failure = await Assert.ThrowsAsync<AggregateException>(() => run);
            Assert.Equal(new[] { runError, stopError }, failure.Flatten().InnerExceptions);
        }
        finally
        {
            io.BeforeOutputWrite = null;
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData("receive")]
    [InlineData("transfer")]
    [InlineData("discharge")]
    [InlineData("return")]
    [InlineData("ng")]
    [InlineData("shoot")]
    [InlineData("supply")]
    [InlineData("feeder")]
    public async Task TransferStepPreservesOperationAndCleanupFailures(string step)
    {
        using var services = CreateServices();
        var io = services.GetRequiredService<StartupIo>();
        var physicalIo = services.GetRequiredService<VirtualIoService>();
        var conveyor = services.GetRequiredService<IBTM.Conveyor.MainConveyor>();
        io.Initialize();
        physicalIo.SetInput(InputIo.AutoMode, false);
        if (step == "receive")
            physicalIo.SetInput(InputIo.MainConveyorEntryCarrierDetected, true);
        if (step == "transfer")
        {
            var work = services.GetRequiredService<IBTM.PcbPlacement.PcbPlacementWork>();
            VirtualTest.SetCarrier(physicalIo, InputIo.PcbPlacementHeatSink1Present, true);
            await work.Station.SeatAsync(CancellationToken.None);
            work.Complete(work.CurrentJob);
        }
        if (step == "discharge")
        {
            physicalIo.SetInput(InputIo.MainConveyorExitCarrierDetected, true);
            physicalIo.SetInput(InputIo.MainConveyorReadyFromRear, true);
        }
        if (step == "return")
            VirtualTest.SetCarrier(physicalIo, InputIo.BoltFasteningHeatSink1Present, true);

        var output = step switch
        {
            "ng" => OutputIo.NgConveyorRun,
            "shoot" => OutputIo.ShootBolt,
            "supply" => OutputIo.PcbSupplyReadyToFront1,
            "feeder" => OutputIo.ShootingFeederRunSignal,
            _ => OutputIo.MainConveyorRun,
        };
        var runError = new IOException("Transfer step failed.");
        var stopError = new IOException("Transfer output OFF failed.");
        var handshakeError = new IOException("Transfer handshake OFF failed.");
        var handshake = step switch
        {
            "receive" => OutputIo.MainConveyorReadyToFront2,
            "discharge" => OutputIo.MainConveyorAvailableToRear,
            _ => (OutputIo?)null,
        };
        var started = false;
        var stopFailed = false;
        var handshakeFailed = false;
        io.BeforeOutputWrite = (signal, on) =>
        {
            if (signal == output && on)
            {
                started = true;
                throw runError;
            }
            if (started && signal == output && !on && !stopFailed)
            {
                stopFailed = true;
                throw stopError;
            }
            if (started && signal == handshake && !on && !handshakeFailed)
            {
                handshakeFailed = true;
                throw handshakeError;
            }
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            var run = step switch
            {
                "return" => conveyor.ReturnToStartAsync(timeout.Token),
                "ng" => services.GetRequiredService<IBTM.NgConveyor.NgCarrierConveyor>()
                    .RunUntilAsync(InputIo.NgConveyorPosition1Occupied, true, false, timeout.Token),
                "shoot" => services.GetRequiredService<IBTM.BoltFastening.BoltFasteningGantry>()
                    .ShootBoltAsync(timeout.Token),
                "supply" => services.GetRequiredService<IBTM.PcbSupply.PcbSupplier>()
                    .RunAsync(new(), timeout.Token),
                "feeder" => services.GetRequiredService<IBTM.BoltFeeder.ShootingBoltFeeder>()
                    .RunAsync(timeout.Token),
                _ => conveyor.RunAsync(timeout.Token),
            };
            var failure = await Assert.ThrowsAsync<AggregateException>(() => run);
            Assert.Equal(
                handshake is null ? new[] { runError, stopError } : new[] { runError, stopError, handshakeError },
                failure.Flatten().InnerExceptions);
        }
        finally
        {
            io.BeforeOutputWrite = null;
        }
    }

    [Fact]
    public async Task MainConveyorStopPreservesCancellationAndOutputFailures()
    {
        using var services = CreateServices();
        var io = services.GetRequiredService<StartupIo>();
        var conveyor = services.GetRequiredService<IBTM.Conveyor.MainConveyor>();
        io.Initialize();
        var cancelError = new IOException("Conveyor cancellation callback failed.");
        var stopError = new IOException("Conveyor output OFF failed.");
        var run = conveyor.RunControlledAsync(
            async token =>
            {
                using var registration = token.Register(() => throw cancelError);
                await Task.Delay(Timeout.Infinite, token);
            },
            CancellationToken.None);
        io.BeforeOutputWrite = (output, _) =>
        {
            if (output == OutputIo.MainConveyorRun)
                throw stopError;
        };
        try
        {
            var failure = Assert.Throws<AggregateException>(conveyor.Stop);
            Assert.Contains(cancelError, failure.Flatten().InnerExceptions);
            Assert.Contains(stopError, failure.Flatten().InnerExceptions);
        }
        finally
        {
            io.BeforeOutputWrite = null;
            await Record.ExceptionAsync(() => run.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.True(run.IsCompleted);
        }
    }

    [Fact]
    public async Task FasteningRunPreservesFailureWhenShootingCleanupFails()
    {
        using var services = CreateServices();
        var io = services.GetRequiredService<StartupIo>();
        var physicalIo = services.GetRequiredService<VirtualIoService>();
        var work = services.GetRequiredService<IBTM.BoltFastening.BoltFasteningWork>();
        io.Initialize();
        VirtualTest.SetCarrier(physicalIo, InputIo.BoltFasteningHeatSink1Present, true);
        await work.Station.SeatAsync(CancellationToken.None);
        var runError = new InvalidOperationException("Fastening target lookup failed.");
        var stopError = new IOException("Shooting output OFF failed.");
        var station = new IBTM.BoltFastening.BoltFasteningStation(
            services.GetRequiredService<IBTM.BoltFastening.BoltFasteningGantry>(),
            work,
            services.GetRequiredService<IBTM.BoltFeeder.PickupBoltFeeder>(),
            services.GetRequiredService<IBTM.BoltFeeder.ShootingBoltFeeder>(),
            () => throw runError);
        io.BeforeOutputWrite = (output, _) =>
        {
            if (output == OutputIo.ShootBolt)
                throw stopError;
        };
        try
        {
            var failure = await Assert.ThrowsAsync<AggregateException>(() => station.RunAsync(new()));
            Assert.Equal(new Exception[] { runError, stopError }, failure.InnerExceptions);
        }
        finally
        {
            io.BeforeOutputWrite = null;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopAndShutdownPreserveCancellationAndOutputFailures(bool shuttingDown)
    {
        using var services = CreateServices();
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<StartupIo>();
        await machine.InitializeAsync();
        using var operation = services.GetRequiredService<OperationCancellation>().Link();
        var cancelError = new IOException("Active command STOP failed.");
        var outputError = new IOException("Conveyor output STOP failed.");
        using var registration = operation.Token.Register(() => throw cancelError);
        io.BeforeOutputWrite = (output, _) =>
        {
            if (output == OutputIo.MainConveyorRun)
                throw outputError;
        };
        try
        {
            AggregateException failure;
            if (shuttingDown)
            {
                var shutdown = machine.ShutdownAsync();
                Assert.False(shutdown.IsCompleted);
                Assert.False(services.GetRequiredService<MachineFeedbackMonitor>().Completion.IsCompleted);
                operation.Dispose();
                failure = await Assert.ThrowsAsync<AggregateException>(
                    () => shutdown.WaitAsync(TimeSpan.FromSeconds(2)));
                Assert.True(services.GetRequiredService<MachineFeedbackMonitor>().Completion.IsCompleted);
            }
            else
            {
                failure = Assert.Throws<AggregateException>(machine.Stop);
            }

            var failures = failure.Flatten().InnerExceptions;
            Assert.Contains(cancelError, failures);
            Assert.Contains(outputError, failures);
        }
        finally
        {
            io.BeforeOutputWrite = null;
            operation.Dispose();
            var cleanupError = await Record.ExceptionAsync(machine.ShutdownAsync);
            if (shuttingDown)
            {
                var error = Assert.IsType<AggregateException>(cleanupError);
                Assert.Same(cancelError, Assert.Single(error.Flatten().InnerExceptions));
            }
            else
            {
                Assert.Null(cleanupError);
            }
        }
    }

    [Theory]
    [InlineData(OutputIo.MainConveyorRun)]
    [InlineData(OutputIo.NgConveyorRun)]
    public async Task ManualConveyorReadFailureWaitsForDeviceCleanup(OutputIo output)
    {
        using var services = CreateServices();
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var operations = services.GetRequiredService<OperationCancellation>();
        var io = services.GetRequiredService<StartupIo>();
        var outputs = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        await state.StopDisplayUpdatesAsync(); // Isolate the command's output read.
        await services.GetRequiredService<MachineFeedbackMonitor>().StopAsync();
        outputs.AutoResponseEnabled = false;
        var readFailure = new IOException("Manual RUN output read failed.");
        var cleanupOutput = output == OutputIo.MainConveyorRun
            ? OutputIo.MainConveyorAvailableToRear
            : OutputIo.NgCarrierEjectCompleteLamp;
        var cleanupReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseCleanup = new ManualResetEventSlim();
        var started = false;
        var cleanupWrites = 0;
        io.BeforeOutputWrite = (signal, value) =>
        {
            if (signal == output && value)
                started = true;
            if (started && signal == cleanupOutput && !value)
            {
                Interlocked.Increment(ref cleanupWrites);
                cleanupReached.TrySetResult();
                Assert.True(releaseCleanup.Wait(TimeSpan.FromSeconds(2)));
            }
        };
        io.OutputReadError = readFailure;
        var run = Task.Run(() => machine.RunManualConveyorAsync(output, CancellationToken.None));
        try
        {
            await cleanupReached.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(outputs.GetOutput(output));
            Assert.False(run.IsCompleted);
            Assert.True(operations.HasActiveOperations);
            releaseCleanup.Set();
            Assert.Same(
                readFailure,
                await Assert.ThrowsAsync<IOException>(() => run.WaitAsync(TimeSpan.FromSeconds(2))));
            Assert.Equal(1, cleanupWrites);
            Assert.False(operations.HasActiveOperations);
            Assert.Equal(MachineAlarm.IoCommunication, state.Alarm);
        }
        finally
        {
            releaseCleanup.Set();
            io.BeforeOutputWrite = null;
            io.OutputReadError = null;
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CylinderRaiseReportsInputReadFailureBeforeAnyActuation(bool operationStarted)
    {
        using var services = CreateServices();
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var operations = services.GetRequiredService<OperationCancellation>();
        var io = services.GetRequiredService<StartupIo>();
        await machine.InitializeAsync();
        await state.StopDisplayUpdatesAsync();
        await services.GetRequiredService<MachineFeedbackMonitor>().StopAsync();
        Assert.True(machine.CanRaiseCylinders);
        var failure = new IOException("Carrier input became unavailable before cylinder raise.");
        var readsFailed = 0;
        var cylinderWrites = 0;
        io.BeforeInputRead = input =>
        {
            if (input != InputIo.PcbPlacementHeatSink1Present
                || operations.HasActiveOperations != operationStarted)
                return;
            io.BeforeInputRead = null;
            readsFailed++;
            throw failure;
        };
        io.BeforeOutputWrite = (output, _) =>
        {
            if (output is OutputIo.PcbPlacementHandlerDown or OutputIo.PcbPlacementIpmDown
                or OutputIo.PickupHeadUp or OutputIo.ShootingHeadUp or OutputIo.NgCarrierPickupUp)
                cylinderWrites++;
        };
        try
        {
            var escaped = await Record.ExceptionAsync(() => machine.RaiseCylindersAsync(CancellationToken.None));

            Assert.Null(escaped);
            Assert.Equal(1, readsFailed);
            Assert.Equal(0, cylinderWrites);
            Assert.Equal(MachineAlarm.IoCommunication, state.Alarm);
            Assert.Equal(failure.ToString(), state.AlarmDetail);
            Assert.False(operations.HasActiveOperations);
            Assert.False(state.IsRunning);
            Assert.True(machine.CanReset);
        }
        finally
        {
            io.BeforeInputRead = null;
            io.BeforeOutputWrite = null;
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task HomeStopsAndReportsInputReadFailureFromStateNotification()
    {
        var settings = new MachineSettings();
        settings.BoltFastening.SafeZ = 10;
        settings.BoltFastening.Motion.ZSpeed = 1;
        using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<StartupIo>();
        await machine.InitializeAsync();
        await state.StopDisplayUpdatesAsync();
        await services.GetRequiredService<MachineFeedbackMonitor>().StopAsync();
        var homing = machine.HomeAsync(CancellationToken.None);
        var failure = new IOException("Carrier input read failed during HOME notification.");
        var notificationThread = Environment.CurrentManagedThreadId;
        io.BeforeInputRead = input =>
        {
            if (input != InputIo.PcbPlacementHeatSink1Present
                || Environment.CurrentManagedThreadId != notificationThread)
                return;
            io.BeforeInputRead = null;
            throw failure;
        };
        try
        {
            Assert.True(state.IsHoming);
            Assert.False(homing.IsCompleted);
            var escaped = Record.Exception(state.Refresh);

            Assert.Null(escaped);
            await homing.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(MachineAlarm.MotionUnavailable, state.Alarm);
            Assert.Equal(failure.ToString(), state.AlarmDetail);
            Assert.False(state.IsHoming);
            Assert.False(state.IsRunning);
            Assert.False(services.GetRequiredService<OperationCancellation>().HasActiveOperations);
            Assert.False(services.GetRequiredKeyedService<IXyMotion>(MotionGroup.BoltFastening)
                .GetAxisState(MotionAxis.Z).InMotion);
        }
        finally
        {
            io.BeforeInputRead = null;
            machine.Stop();
            await homing;
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public void StateQueriesBeforeInitializationDoNotReadOutputs()
    {
        using var services = CreateServices();
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<StartupIo>();

        Assert.False(state.IsRunning);
        Assert.False(state.ConveyorRunning);
        Assert.False(machine.CanStart);
        Assert.False(machine.CanHome);
        _ = machine.CanReset;
        using (services.GetRequiredService<OperationCancellation>().Link())
        {
            Assert.True(state.IsRunning);
            Assert.False(machine.CanReset);
        }

        Assert.Equal(0, io.ReadsWhileUnavailable);
    }

    [Fact]
    public void TeachingHintsAndCaptureAvailabilityBeforeInitializationDoNotReadInputs()
    {
        using var services = CreateServices();
        var io = services.GetRequiredService<StartupIo>();
        var station = services.GetRequiredService<TeachingViewModel>();

        foreach (var group in station.TeachingUnits)
        {
            station.SelectedTeachingUnit = group;
            Assert.Equal(
                group == HardwareArea.InspectionGantry ? TeachingMotionHint.None : TeachingMotionHint.MotionUnavailable,
                station.MotionHint);
            Assert.False(station.CaptureCarrierImageCommand.CanExecute(null));
            foreach (var point in station.FilteredPoints.Where(point => point.Position.Storage == TeachingStorage.Buffer))
            {
                station.SelectedPoint = point;
                Assert.Equal(TeachingMotionHint.MotionUnavailable, station.MotionHint);
                Assert.False(station.CaptureCarrierImageCommand.CanExecute(null));
            }
        }
        Assert.Equal(0, io.ReadsWhileUnavailable);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InitializationFailurePreservesOriginalAlarmAndAllowsReset(bool failCheckReady)
    {
        using var services = CreateServices();
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<StartupIo>();
        var error = new IOException("Original control initialization failure.");
        io.InitializationError = error;
        io.FailCheckReady = failCheckReady;

        await machine.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(2));

        AssertUnavailable(state, error);
        var log = services.GetRequiredService<ApplicationLog>();
        var detail = Assert.Single(log.Snapshot(), entry => entry.Detail?.Contains(error.Message) == true);
        Assert.Equal("Machine alarm: IoCommunication.", detail.Message);
        Assert.Equal(error.ToString(), detail.Detail);
        var stage = failCheckReady ? "Control I/O readiness check" : "Control I/O initialization";
        Assert.Contains(log.Snapshot(), entry => entry.Message == $"{stage} failed. {error.Message}"
            && entry.Detail is null);
        Assert.True(machine.CanReset);
        Assert.False(machine.CanStart);
        Assert.False(machine.CanHome);
        Assert.False(state.ManualSetupEnabled);
        Assert.All(
            services.GetRequiredService<IoSignals>().Outputs.Values,
            output => Assert.Null(output.IsOn));
        Assert.Equal(0, io.ReadsWhileUnavailable);
        Assert.Equal(0, io.WritesWhileUnavailable);

        var resetError = new IOException("Control initialization failed again during RESET.");
        io.InitializationError = resetError;
        await machine.ResetAsync().WaitAsync(TimeSpan.FromSeconds(2));
        AssertUnavailable(state, error);
        var resetDetail = Assert.Single(log.Snapshot(), entry => entry.Detail?.Contains(resetError.Message) == true);
        Assert.Equal("Machine alarm remains: IoCommunication.", resetDetail.Message);
        Assert.Equal(resetError.ToString(), resetDetail.Detail);

        io.InitializationError = null;
        await machine.ResetAsync().WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => state.Display.Available && state.Display.Alarm == MachineAlarm.None);
        Assert.Equal(MachineAlarm.None, state.Alarm);
        Assert.Null(state.Display.ReadError);
        Assert.Equal(0, io.ReadsWhileUnavailable);
        Assert.Equal(0, io.WritesWhileUnavailable);
        await machine.ShutdownAsync();
    }

    [Fact]
    public async Task LostConnectionDoesNotReplaceTheOriginalFaultWithAnOutputRead()
    {
        using var services = CreateServices();
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<StartupIo>();
        await machine.InitializeAsync();
        var error = new IOException("Original control connection failure.");

        io.Disconnect(error);
        await WaitUntilAsync(() => !state.Display.Available);

        AssertUnavailable(state, error);
        Assert.Contains(
            services.GetRequiredService<ApplicationLog>().Snapshot(),
            entry => entry.Level == "ERROR" && entry.Detail?.Contains(error.Message) == true);
        Assert.True(machine.CanReset);
        Assert.All(
            services.GetRequiredService<InspectionGantry>().Motion.Axes.Values,
            axis => Assert.Equal(AxisCondition.Unavailable, axis.Condition));
        Assert.Equal(0, io.ReadsWhileUnavailable);
        await Assert.ThrowsAsync<AggregateException>(machine.ShutdownAsync);
        Assert.True(io.WritesWhileUnavailable > 0);
    }

    [Fact]
    public async Task ConnectionFailureDuringOutputScanPreservesTheOriginalFault()
    {
        using var services = CreateServices();
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<StartupIo>();
        await machine.InitializeAsync();
        var error = new IOException("Original connection fault during output scan.");
        io.BeforeOutputRead = () =>
        {
            io.Disconnect(error);
            throw new IOException("Secondary AXT_RT_NOT_OPEN during the in-flight read.");
        };

        state.RequestDisplayRefresh();
        await WaitUntilAsync(() => !state.Display.Available);

        AssertUnavailable(state, error);
        Assert.All(
            services.GetRequiredService<InspectionGantry>().Motion.Axes.Values,
            axis => Assert.Equal(AxisCondition.Unavailable, axis.Condition));
        Assert.Equal(0, io.ReadsWhileUnavailable);
        await Assert.ThrowsAsync<AggregateException>(machine.ShutdownAsync);
    }

    [Fact]
    public async Task ReadyOutputReadFailureStillFailsClosed()
    {
        using var services = CreateServices();
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<StartupIo>();
        await machine.InitializeAsync();
        await state.StopDisplayUpdatesAsync();
        var display = state.Display;
        var error = new IOException("Actual output read failure.");
        try
        {
            machine.ToggleDiagnosticOutput(OutputIo.MainConveyorRun);
            io.OutputReadError = error;
            await WaitUntilAsync(() => state.Alarm == MachineAlarm.IoCommunication);
            Assert.Contains(error.Message, state.AlarmDetail);
            Assert.False(services.GetRequiredService<VirtualIoService>().GetOutput(OutputIo.MainConveyorRun));
            Assert.All(services.GetRequiredService<IoSignals>().Outputs.Values, output => Assert.Null(output.IsOn));
            Assert.Same(error, services.GetRequiredService<MachineFeedbackMonitor>().ReadError);
            Assert.Same(display, state.Display);

            io.OutputReadError = null;
            await WaitUntilAsync(() => services.GetRequiredService<MachineFeedbackMonitor>().ReadError is null);
            Assert.Equal(MachineAlarm.IoCommunication, state.Alarm); // Recovery does not acknowledge the alarm.
        }
        finally
        {
            io.OutputReadError = null;
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task InputAndOutputMonitorsOutliveDisplayAndWaitForOperationCleanup()
    {
        using var services = CreateServices();
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<StartupIo>();
        var signals = services.GetRequiredService<IoSignals>();
        var feedback = services.GetRequiredService<MachineFeedbackMonitor>();
        await machine.InitializeAsync();
        await state.StopDisplayUpdatesAsync();
        var display = state.Display;
        var input = signals.Inputs[InputIo.PcbSupplyPcbDetected];
        var light = signals.Outputs[OutputIo.MachineLight];
        using var operation = services.GetRequiredService<OperationCancellation>().Link();
        try
        {
            // No driver event: the next input/output scan must discover these changes.
            io.PendingInput = (input.Signal, true);
            io.ObservedLight = true;
            await WaitUntilAsync(() => input.IsOn == true && light.IsOn == true);
            Assert.Same(display, state.Display);

            var shutdown = machine.ShutdownAsync();
            Assert.True(operation.Token.IsCancellationRequested);
            Assert.False(shutdown.IsCompleted);
            io.PendingInput = (input.Signal, false);
            io.ObservedLight = false;
            await WaitUntilAsync(() => input.IsOn == false && light.IsOn == false);
            Assert.False(feedback.Completion.IsCompleted);
            operation.Dispose();
            await shutdown.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.True(feedback.Completion.IsCompletedSuccessfully);
            var scans = Volatile.Read(ref io.InputScans);
            io.PendingInput = (input.Signal, true);
            await Task.Delay(30);
            Assert.Equal(scans, Volatile.Read(ref io.InputScans));
            Assert.False(input.IsOn);
        }
        finally
        {
            operation.Dispose();
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnexpectedIoMonitorFailureCannotBeResetIntoAnUnmonitoredMachine(bool inputScan)
    {
        var services = CreateServices();
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<StartupIo>();
        var feedback = services.GetRequiredService<MachineFeedbackMonitor>();
        var error = new InvalidOperationException("Unexpected I/O scan programming error.");
        try
        {
            await machine.InitializeAsync();
            if (inputScan)
                io.InputScanError = error;
            else
                io.OutputReadError = error;
            await WaitUntilAsync(() => ReferenceEquals(error, feedback.Failure));
            await WaitUntilAsync(() => services.GetRequiredService<MachineState>().Alarm == MachineAlarm.IoCommunication);
            io.InputScanError = null;
            io.OutputReadError = null;

            Assert.False(machine.CanReset);
            await machine.ResetAsync();
            Assert.Same(error, feedback.ReadError);
            Assert.Equal(MachineAlarm.IoCommunication, services.GetRequiredService<MachineState>().Alarm);
            if (inputScan)
            {
                var shutdownError = await Assert.ThrowsAsync<AggregateException>(machine.ShutdownAsync);
                var failures = shutdownError.Flatten().InnerExceptions;
                Assert.Contains(error, failures);
                Assert.Contains(failures, failure => failure is IOException);
            }
            else
            {
                Assert.Same(error, await Assert.ThrowsAsync<InvalidOperationException>(machine.ShutdownAsync));
            }
        }
        finally
        {
            Assert.Same(error, Record.Exception(services.Dispose));
        }
    }

    private static void AssertUnavailable(MachineState state, Exception error)
    {
        Assert.Equal(MachineAlarm.IoCommunication, state.Alarm);
        Assert.False(state.Display.Available);
        Assert.Equal(MachineAlarm.IoCommunication, state.Display.Alarm);
        Assert.Equal(error.Message, state.Display.AlarmMessage);
        Assert.Contains(error.Message, state.Display.AlarmDetail);
        Assert.Null(state.Display.ReadError);
        Assert.False(state.Display.CanStart);
        Assert.False(state.Display.CanHome);
        Assert.False(state.Display.ManualControlsEnabled);
        Assert.False(state.Display.ManualSetupEnabled);
    }

    private static ServiceProvider CreateServices(MachineSettings? settings = null)
    {
        return new ServiceCollection().AddIbtmApplication(
            settings ?? new MachineSettings())
            .AddSingleton<StartupIo>()
            .AddSingleton<IIoService>(provider => provider.GetRequiredService<StartupIo>())
            .BuildServiceProvider();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }

    // Physical I/O starts closed; the regular VirtualIoService starts ready and
    // permits output reads while disconnected, which cannot expose this bug.
    private sealed class StartupIo(VirtualIoService inner) : IIoService
    {
        public bool IsReady { get; private set; }

        public int TimeoutMilliseconds
        {
            get
            {
                return inner.TimeoutMilliseconds;
            }
        }

        public Exception? InitializationError { get; set; }
        public Exception? OutputReadError { get; set; }
        public Exception? InputScanError { get; set; }
        public (InputIo Input, bool Value)? PendingInput;
        public bool? ObservedLight;
        public int InputScans;
        public Action? BeforeOutputRead { get; set; }
        public Action<InputIo>? BeforeInputRead { get; set; }
        public Action<OutputIo, bool>? BeforeOutputWrite { get; set; }
        public bool FailCheckReady { get; set; }
        public bool AllowWritesWhileUnavailable { get; set; }
        public int ReadsWhileUnavailable { get; private set; }
        public int WritesWhileUnavailable { get; private set; }

        public event Action<Exception>? Faulted;
        public event Action<InputIo, bool>? InputChanged
        {
            add
            {
                inner.InputChanged += value;
            }

            remove
            {
                inner.InputChanged -= value;
            }
        }

        public event Action<OutputIo, bool>? OutputChanged
        {
            add
            {
                inner.OutputChanged += value;
            }

            remove
            {
                inner.OutputChanged -= value;
            }
        }

        public void Initialize()
        {
            if (!FailCheckReady && InitializationError is { } error)
                throw error;
            IsReady = true;
            inner.Initialize();
        }

        public void CheckReady()
        {
            if (FailCheckReady && InitializationError is { } error)
            {
                IsReady = false;
                throw error;
            }

            inner.CheckReady();
        }

        public void RefreshInputs()
        {
            Interlocked.Increment(ref InputScans);
            if (InputScanError is { } error)
            {
                Disconnect(error);
                throw error;
            }
            if (PendingInput is { } pending)
            {
                PendingInput = null;
                inner.SetInput(pending.Input, pending.Value);
            }
        }

        public void Disconnect(Exception error)
        {
            IsReady = false;
            Faulted?.Invoke(error);
        }

        public bool GetInput(InputIo input)
        {
            BeforeInputRead?.Invoke(input);
            if (!IsReady)
            {
                ReadsWhileUnavailable++;
                throw new IOException("Input scan is unavailable; cached inputs are not current feedback.");
            }

            return inner.GetInput(input);
        }

        public OutputFeedback? GetOutputFeedback(OutputIo output)
        {
            return inner.GetOutputFeedback(output);
        }

        public bool GetOutput(OutputIo output)
        {
            if (!IsReady)
            {
                ReadsWhileUnavailable++;
                throw new IOException("Simulated AXT_RT_NOT_OPEN during output read.");
            }

            var beforeRead = BeforeOutputRead;
            BeforeOutputRead = null;
            beforeRead?.Invoke();
            if (OutputReadError is { } error)
                throw error;
            if (output == OutputIo.MachineLight && ObservedLight is { } light)
                return light;
            return inner.GetOutput(output);
        }

        public void SetOutput(OutputIo output, bool value)
        {
            if (!IsReady)
            {
                WritesWhileUnavailable++;
                if (!AllowWritesWhileUnavailable)
                    throw new IOException("Output controller is unavailable.");
            }

            BeforeOutputWrite?.Invoke(output, value);
            inner.SetOutput(output, value);
        }
    }
}
