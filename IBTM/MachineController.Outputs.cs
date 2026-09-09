using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;

namespace IBTM;

public sealed partial class MachineController
{
    // OUTPUTS is manual I/O diagnostics, not automatic operation or teaching.
    private string? GetManualOutputSafetyBlock()
    {
        if (_operations.IsShuttingDown) return "Read only: the machine is shutting down.";
        if (!_io.IsReady) return "Read only: control I/O is unavailable.";
        if (!_state.ManualMode) return "Read only: switch the selector to MANUAL.";
        if (!_state.EmergencyStopReleased || !_state.AirPressureOk)
            return "Read only: check emergency stops and air pressure.";
        return _state.Alarm is MachineAlarm.None or MachineAlarm.MotionUnavailable
            or MachineAlarm.HomeFailed or MachineAlarm.Inspection
            ? null : "Read only: reset the safety, I/O or process alarm first.";
    }

    internal string? GetManualOutputBlock(OutputIo signal, bool live = false)
    {
        var block = live
            ? GetManualOutputSafetyBlock()
                ?? (_state.IsRunning ? "Read only: wait for the current operation to stop." : null)
            : _state.Display.ManualOutputBlock;
        if (block is not null) return block;

        if (signal == OutputIo.MainConveyorRun) return GetConveyorOutputBlock(live);

        if (signal is OutputIo.MachineLight or OutputIo.TowerLampGreen or OutputIo.TowerLampYellow
            or OutputIo.TowerLampRed or OutputIo.Buzzer
            or OutputIo.NgCarrierEjectLamp or OutputIo.NgCarrierEjectCompleteLamp)
            return null;

        if (IsInterfaceOutput(signal)) return GetInterfaceOutputBlock(signal, live);

        if (signal is OutputIo.PcbPlacementStopperUp or OutputIo.BoltFasteningStopperUp
            or OutputIo.InspectionStopperUp or OutputIo.NgConveyorStopperUp)
            return GetStopperOutputBlock(signal, live);

        // Motor, shuttle and shooting outputs need their dedicated
        // sequences/hold-to-run controls, not an unrestricted latched toggle.
        if (!_teachingOutputs.TryGetValue(signal, out var entry) || entry.Output.HoldToRun)
            return "Use the dedicated Manual Control / Station Teaching operation for this output.";

        if (entry.Output.RequiresHandler)
        {
            var motion = _state.GetMotionStatus(entry.Group);
            if (!_units.IsMotionEnabled(entry.Group) || !motion.Feedback.IsReady
                || !_state.ServoMainContactorOn
                || motion.Feedback.Axes.Any(axis =>
                    (live ? motion.Feedback.GetAxisState(axis) : motion.Axes[axis].State)
                    is not { Homed: true, ServoOn: true, Alarm: false, Emergency: false }))
                return "The associated handler must be enabled, homed and free of motion faults.";

            if (entry.Group == MotionGroup.PcbSupply
                    && (live ? _state.PlacementInBufferArea : _state.Display.PlacementInBufferArea)
                || entry.Group == MotionGroup.PcbPlacementHandler
                    && (live ? _state.SupplyInBufferArea : _state.Display.SupplyInBufferArea))
                return "The other handler is inside the PCB buffer.";
        }

        if (entry.Output.CanSet?.Invoke(live) == false
            || signal == OutputIo.PcbSupplyRotate && !_supplyHandler.CanRotateInPlace(live))
            return "Output interlock: check the handler position and cylinder clearance.";
        return null;
    }

    internal static bool IsInterfaceOutput(OutputIo signal) => signal is
        OutputIo.PcbSupplyReadyToFront1 or OutputIo.MainConveyorReadyToFront2 or OutputIo.MainConveyorAvailableToRear;

    private string? GetConveyorOutputBlock(bool live)
    {
        if (!_units.MainConveyor) return "Enable the main conveyor before its motor test.";
        if (!(live ? MainConveyorPathClear : _state.Display.MainConveyorPathClear))
            return "Raise and clear the enabled handlers before running the main conveyor.";
        if (_io.GetInput(InputIo.MainConveyorEntryCarrierDetected)
            || _io.GetInput(InputIo.PcbPlacementCarrierPresent)
            || _io.GetInput(InputIo.BoltFasteningCarrierPresent)
            || _io.GetInput(InputIo.InspectionCarrierPresent)
            || _io.GetInput(InputIo.MainConveyorExitCarrierDetected))
            return "Remove carriers from the main conveyor before this motor-only test.";
        return null;
    }

    private string? GetStopperOutputBlock(OutputIo signal, bool live)
    {
        if (signal == OutputIo.NgConveyorStopperUp)
            return !_units.NgConveyor ? "Enable the NG conveyor before testing its stopper."
                : _io.GetInput(InputIo.NgConveyorPosition1Occupied)
                    || _io.GetInput(InputIo.NgConveyorPosition2Occupied)
                    || _io.GetInput(InputIo.NgShuttleCarrierDetected)
                    ? "Empty the NG conveyor and shuttle before testing the stopper." : null;

        if (!_units.MainConveyor) return "Enable the main conveyor before testing its stoppers.";
        if (!(live ? MainConveyorPathClear : _state.Display.MainConveyorPathClear))
            return "Raise and clear the enabled handlers before testing conveyor stoppers.";
        var (carrier, plateUp, plateDown) = signal switch
        {
            OutputIo.PcbPlacementStopperUp => (InputIo.PcbPlacementCarrierPresent,
                InputIo.PcbPlacementBackupPlateUp, InputIo.PcbPlacementBackupPlateDown),
            OutputIo.BoltFasteningStopperUp => (InputIo.BoltFasteningCarrierPresent,
                InputIo.BoltFasteningBackupPlateUp, InputIo.BoltFasteningBackupPlateDown),
            _ => (InputIo.InspectionCarrierPresent, InputIo.InspectionBackupPlateUp, InputIo.InspectionBackupPlateDown),
        };
        return _io.GetInput(carrier) || _io.GetInput(plateUp) || !_io.GetInput(plateDown)
            ? "Empty this station and lower its backup plate before testing the stopper." : null;
    }

    private string? GetInterfaceOutputBlock(OutputIo signal, bool live)
    {
        if (!(signal == OutputIo.PcbSupplyReadyToFront1 ? _units.PcbSupply : _units.MainConveyor))
            return "Enable the owning unit before testing its interface signal.";
        // A manual interface signal does not command a servo axis. Keep actual
        // transfer/collision interlocks, not whole-machine homing/servo readiness.
        if (live ? _state.BufferConflict : _state.Display.BufferConflict)
            return "Clear the PCB buffer conflict before testing interface signals.";
        if (Array.Exists(CarrierInputs, _io.GetInput) || _io.GetInput(InputIo.PcbSupplyPcbDetected)
            || _io.GetInput(InputIo.PcbPlacementPcbDetected) || _io.GetInput(InputIo.PcbBufferPcbPresent))
            return "Empty the machine before testing interface signals.";
        if (_io.GetInput(InputIo.PcbSupplyAvailableFromFront1)
            || _io.GetInput(InputIo.MainConveyorAvailableFromFront2)
            || _io.GetInput(InputIo.MainConveyorReadyFromRear))
            return "Stop the connected equipment: its available/ready inputs must be OFF for this test.";
        return !(live ? MainConveyorPathClear : _state.Display.MainConveyorPathClear)
            ? "Clear the conveyor path before testing interface signals." : null;
    }

    internal async Task ToggleManualOutputAsync(OutputIo signal, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            // The display controls button availability only. Recheck live state
            // before any write, including direct invocation of a disabled command.
            if (GetManualOutputBlock(signal, live: true) is { } block)
            {
                _log?.Write($"Manual output {signal} ignored: {block}");
                return;
            }

            using var operation = _operations.Link(cancellationToken);
            var startingAlarm = _state.Alarm;
            var conveyorTest = signal == OutputIo.MainConveyorRun;
            var interfaceTest = IsInterfaceOutput(signal);
            var maintainedOutput = conveyorTest || interfaceTest;
            void StopWhenUnavailable()
            {
                if (operation.IsCancellationRequested) return;
                try
                {
                    if (GetManualOutputSafetyBlock() is not null || _state.Alarm != startingAlarm
                        || interfaceTest && GetInterfaceOutputBlock(signal, live: true) is not null
                        || conveyorTest && GetConveyorOutputBlock(live: true) is not null)
                        operation.Cancel();
                }
                catch (Exception exception)
                {
                    // This callback also runs on the DI monitor: a motion read failure
                    // must stop this output test, not terminate input scanning.
                    _log?.Error($"Manual output {signal} stopped: interlock feedback could not be read.", exception);
                    operation.Cancel();
                }
            }

            void OnDiagnosticInputChanged(InputIo _, bool __) => StopWhenUnavailable();
            _state.Changed += StopWhenUnavailable;
            if (maintainedOutput) _io.InputChanged += OnDiagnosticInputChanged;
            if (maintainedOutput) _state.DisplayChanged += StopWhenUnavailable;
            try
            {
                StopWhenUnavailable();
                operation.Token.ThrowIfCancellationRequested();
                if (maintainedOutput)
                {
                    try
                    {
                        if (conveyorTest)
                        {
                            _log?.Write($"Main conveyor motor test RUN: forward, normal speed; alarm={startingAlarm}.");
                            _conveyor.RunMotor(operation.Token);
                        }
                        else
                        {
                            _log?.Write($"Manual interface output {signal}: ON until OFF or cancellation. Connected equipment must be stopped.");
                            _io.SetOutput(signal, true);
                        }
                        // Own ON until cancellation; OFF must not wait for the UI dispatcher.
                        await Task.Delay(Timeout.Infinite, operation.Token).ConfigureAwait(false);
                    }
                    finally
                    {
                        if (conveyorTest) _conveyor.Stop();
                        else _io.SetOutput(signal, false);
                        _log?.Write(conveyorTest ? "Main conveyor motor test STOP." : $"Manual interface output {signal}: OFF.");
                    }
                    return;
                }
                var value = !_io.GetOutput(signal);
                // This window never moves an axis as a side effect of a toggle.
                _log?.Write($"Manual output {signal}: {(value ? "ON" : "OFF")}; alarm={startingAlarm}.");
                operation.Token.ThrowIfCancellationRequested();
                _io.SetOutput(signal, value);
                if (_io.GetOutputFeedback(signal) is not null)
                    await _io.WaitForOutputFeedbackAsync(signal, value, operation.Token);
            }
            finally
            {
                _state.Changed -= StopWhenUnavailable;
                if (maintainedOutput) _io.InputChanged -= OnDiagnosticInputChanged;
                if (maintainedOutput) _state.DisplayChanged -= StopWhenUnavailable;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (IoTimeoutException exception)
        {
            _log?.Error($"Manual output {signal}: feedback timed out. {exception.Message}", exception);
            throw;
        }
        catch (Exception exception)
        {
            _state.SetError(MachineAlarm.IoCommunication, exception);
            _operations.Cancel();
        }
    }

    // Teaching may coordinate a handler as well as its cylinder output.
    internal Task RunTeachingOutputAsync(
        TeachingOutput output,
        bool value,
        CancellationToken cancellationToken,
        CancellationToken viewCancellation) =>
        RunManualAsync(token => output.SetAsync(value, token), output.Owner switch
        {
            HardwareArea.MainConveyor => MachineAlarm.MainConveyor,
            HardwareArea.PcbSupply => MachineAlarm.PcbSupply,
            HardwareArea.PcbPlacementHandler => MachineAlarm.PcbPlacement,
            HardwareArea.BoltFastening => MachineAlarm.BoltFastening,
            HardwareArea.NgCarrierTransfer => MachineAlarm.NgCarrierTransfer,
            _ => throw new ArgumentOutOfRangeException(nameof(output)),
        }, () => _state.ManualSetupEnabled
            && (output.CanSet?.Invoke(true) ?? true)
            && (!output.RequiresHandler || CanUseManualMotion(output.Owner switch
            {
                HardwareArea.PcbSupply => MotionGroup.PcbSupply,
                HardwareArea.PcbPlacementHandler => MotionGroup.PcbPlacementHandler,
                HardwareArea.BoltFastening => MotionGroup.BoltFastening,
                HardwareArea.NgCarrierTransfer => MotionGroup.InspectionGantry,
                _ => throw new ArgumentOutOfRangeException(nameof(output)),
            })), cancellationToken, viewCancellation);
}
