using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using IBTM.BoltFastening;
using IBTM.BoltFeeder;
using IBTM.Core;
using IBTM.Device;
using IBTM.NgConveyor;
using IBTM.PcbPlacement;
using IBTM.PcbSupply;
using IBTM.Virtual;
using IBTM.UI;
using Xunit;
using static IBTM.Virtual.Tests.VirtualTest;

namespace IBTM.Virtual.Tests;

public sealed class IoTests
{
    [Fact]
    public void ConfirmedIoMapHasOneSignalPerInputAndNoSeparateP3Sensor()
    {
        var settings = new MachineSettings();
        var hardware = typeof(MachineSettings).GetProperties()
            .Select(property => property.GetValue(settings))
            .OfType<InputHardwareSettings>()
            .ToArray();
        var inputs = hardware.SelectMany(section => section.Inputs)
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        Assert.Equal(Enum.GetValues<InputIo>().Order(), inputs.Keys.Order());
        Assert.Equal(inputs.Count, inputs.Values.Distinct().Count());
        Assert.DoesNotContain(86, inputs.Values); // DI-146 is not installed; P3 is DI-142.
        Assert.Equal(82, inputs[InputIo.NgShuttleCarrierDetected]);
        Assert.Equal(84, inputs[InputIo.NgConveyorPosition1Occupied]);
        Assert.Equal(85, inputs[InputIo.NgConveyorPosition2Occupied]);
        Assert.Equal(91, inputs[InputIo.MainConveyorEntryCarrierDetected]);
        Assert.Equal(92, inputs[InputIo.MainConveyorExitCarrierDetected]);
        Assert.Equal(20, inputs[InputIo.PcbSupplyRotated]);
        Assert.Equal(22, inputs[InputIo.PcbSupplyGripperClosed]);
        Assert.Equal(24, inputs[InputIo.PcbSupplyIpmFixerForward]);

        var outputs = hardware.OfType<IoHardwareSettings>().SelectMany(section => section.Outputs).ToArray();
        Assert.Equal(Enum.GetValues<OutputIo>().Order(), outputs.Select(pair => pair.Key).Order());
        var channels = outputs.SelectMany(
            pair =>
                pair.Value.OffNumber is { } off
                    ? new[] { pair.Value.Number, off }
                    : new[] { pair.Value.Number })
            .ToArray();
        Assert.Equal(channels.Length, channels.Distinct().Count());
    }

    [Fact]
    public void IoStatusSharesInputsAndRefreshesOutputDisplayOnlyWhenRequested()
    {
        var hardware = new NgShuttleHardwareSettings();
        hardware.Inputs.Add(InputIo.PcbSupplyPcbDetected, 999);
        var io = new VirtualIoService(hardware.Outputs, new MachineOptions())
        {
            AutoResponseEnabled = false,
        };
        var observedIo = DispatchProxy.Create<IIoService, OutputReadProbe>();
        var probe = (OutputReadProbe)observedIo;
        probe.Io = io;
        var signals = new IoSignals([hardware], observedIo);
        var status = hardware.CreateIoStatus(signals);
        Assert.Equal(hardware.Area, status.Area);
        Assert.Equal(hardware.Inputs.Keys.Order(), status.Inputs.Select(row => row.Signal));
        var output = Assert.Single(status.Outputs);
        Assert.Equal(hardware.Outputs[output.Signal].Number, output.Number);
        Assert.Equal(hardware.Outputs[output.Signal].OffNumber, output.OffNumber);
        Assert.Equal("068 / 069", output.Address);
        Assert.All(status.Inputs, row => Assert.Equal(hardware.Inputs[row.Signal], row.Number));
        Assert.Null(output.IsOn);
        Assert.Equal(0, probe.Reads);
        signals.RefreshOutputs();
        Assert.False(output.IsOn);
        Assert.Equal(1, probe.Reads);
        Assert.Equal(OutputIo.NgShuttleDown, output.Signal);
        Assert.Equal(
            new[] { InputIo.NgShuttleDown, InputIo.NgShuttleUp },
            output.Feedback.Select(row => row.Signal));
        Assert.All(
            output.Feedback,
            row =>
            {
                Assert.Same(status.Inputs.Single(input => input.Signal == row.Signal), row);
                Assert.DoesNotContain(row, status.Sensors);
            });

        var sensor = status.Sensors.Single(row => row.Signal == InputIo.PcbSupplyPcbDetected);
        Assert.Equal("999", sensor.Address);
        var filter = new IoList<IoOutputStatus, OutputIo>(signals.Outputs.Values.ToArray(), row => row);
        filter.SearchText = "069";
        Assert.Same(output, Assert.Single(filter.FilteredRows.Cast<IoOutputStatus>()));
        filter.SearchText = output.Feedback[0].Address;
        Assert.Same(output, Assert.Single(filter.FilteredRows.Cast<IoOutputStatus>()));
        filter.SearchText = "999";
        Assert.Empty(filter.FilteredRows.Cast<IoOutputStatus>());
        var changes = 0;
        sensor.PropertyChanged += (_, _) => changes++;
        io.SetInput(InputIo.AutoMode, false);
        Assert.Equal(0, changes);
        io.SetInput(sensor.Signal, true);
        Assert.True(sensor.IsOn);
        Assert.Equal(1, changes);
        io.SetInput(sensor.Signal, false);
        Assert.False(sensor.IsOn);
        Assert.Equal(2, changes);

        var outputChanges = 0;
        output.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(output.IsOn))
                outputChanges++;
        };
        io.SetOutput(output.Signal, true);
        Assert.False(output.IsOn);
        signals.RefreshOutputs();
        Assert.True(output.IsOn);
        Assert.Equal(1, outputChanges);
        signals.RefreshOutputs();
        Assert.Equal(1, outputChanges);
        Assert.All(output.Feedback, row => Assert.False(row.IsOn));
        io.SetInput(InputIo.NgShuttleUp, true);
        io.SetInput(InputIo.NgShuttleDown, true);
        Assert.All(output.Feedback, row => Assert.True(row.IsOn));
        Assert.True(output.HasConflict);
        Assert.False(output.IsMatched);
        Assert.Equal(3, probe.Reads);

        var error = new IOException("Output read failed.");
        probe.Error = error;
        Assert.Same(error, Assert.Throws<IOException>(signals.RefreshOutputs));
        Assert.Null(output.IsOn);
        Assert.False(output.IsMatched);

        probe.Error = null;
        signals.RefreshOutputs();
        Assert.True(output.IsOn);
        io.SetConnected(false);
        signals.RefreshOutputs();
        Assert.Null(output.IsOn);
        Assert.Null(sensor.IsOn);
        Assert.False(signals.InputsAvailable);
        var beforeReconnect = changes;
        io.SetConnected(true);
        signals.RefreshInputs();
        Assert.False(sensor.IsOn);
        Assert.True(signals.InputsAvailable);
        Assert.True(changes > beforeReconnect);
    }

    public class OutputReadProbe : DispatchProxy
    {
        public IIoService Io = null!;
        public int Reads;
        public Exception? Error;

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(IIoService.GetOutput))
            {
                Reads++;
                if (Error is { } error)
                    throw error;
            }

            return method.Invoke(Io, args);
        }
    }

    [Fact]
    public async Task CylinderFeedbackRequiresOneEndpointAndRejectsContradictoryInputs()
    {
        var io = new VirtualIoService(
            new NgShuttleHardwareSettings().Outputs,
            new MachineOptions { TimeoutMilliseconds = 100 });
        io.Initialize();
        io.AutoResponseEnabled = false;
        IIoService signals = io;
        io.SetInput(InputIo.NgShuttleDown, true);
        var waiting = signals.SetOutputAndWaitAsync(OutputIo.NgShuttleDown, true);
        Assert.False(waiting.IsCompleted);
        io.SetInput(InputIo.NgShuttleUp, false);
        await waiting;

        io.SetInput(InputIo.NgShuttleUp, true);
        await Assert.ThrowsAsync<IoTimeoutException>(
            () => signals.SetOutputAndWaitAsync(OutputIo.NgShuttleDown, true));
        Assert.True(io.GetOutput(OutputIo.NgShuttleDown));

        using var stop = new CancellationTokenSource();
        waiting = signals.SetOutputAndWaitAsync(OutputIo.NgShuttleDown, false, stop.Token);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.False(io.GetOutput(OutputIo.NgShuttleDown));
    }

    [Fact]
    public void ManualResponseRetainsDoorEmergencyStopAndResetInterlocks()
    {
        var io = new VirtualIoService(new MachineHardwareSettings().Outputs, new MachineOptions());
        using var motion = new VirtualMotionService(new MotionSettings(), new OperationCancellation());
        _ = new VirtualMachine(io, [motion]);
        io.Initialize();
        motion.Initialize();
        io.AutoResponseEnabled = false;
        io.SetInput(InputIo.Door1Open, false);
        Assert.True(io.GetInput(InputIo.ServoMainContactorOn));

        io.SetInput(InputIo.AutoMode, false);
        Assert.False(io.GetInput(InputIo.ServoMainContactorOn));
        Assert.All(
            motion.Axes,
            axis =>
            {
                Assert.False(motion.GetAxisState(axis).ServoOn);
                Assert.True(motion.GetAxisState(axis).Alarm);
            });
        io.SetInput(InputIo.ResetButton, true);
        Assert.False(io.GetInput(InputIo.ServoMainContactorOn));

        io.SetInput(InputIo.AutoMode, true);
        io.SetInput(InputIo.ResetButton, false);
        io.SetInput(InputIo.ResetButton, true);
        Assert.True(io.GetInput(InputIo.ServoMainContactorOn));
        Assert.All(motion.Axes, axis => Assert.True(motion.GetAxisState(axis).ServoOn));

        io.SetInput(InputIo.EmergencyStop1Pressed, true);
        io.SetInput(InputIo.ResetButton, false);
        io.SetInput(InputIo.ResetButton, true);
        Assert.False(io.GetInput(InputIo.ServoMainContactorOn));
        Assert.All(motion.Axes, axis => Assert.False(motion.GetAxisState(axis).ServoOn));

        io.SetInput(InputIo.EmergencyStop1Pressed, false);
        io.SetInput(InputIo.ResetButton, false);
        io.SetInput(InputIo.ResetButton, true);
        Assert.True(io.GetInput(InputIo.ServoMainContactorOn));
        Assert.All(motion.Axes, axis => Assert.True(motion.GetAxisState(axis).ServoOn));
    }
}
