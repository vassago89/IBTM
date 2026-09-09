using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;

namespace IBTM;

public sealed partial class MachineController
{
    // OUTPUTS is manual I/O diagnostics, not automatic operation or teaching.
    private OutputBlockReason GetManualOutputSafetyBlock()
    {
        if (_operations.IsShuttingDown) return OutputBlockReason.ShuttingDown;
        if (!_io.IsReady) return OutputBlockReason.IoUnavailable;
        if (!_state.ManualMode) return OutputBlockReason.AutoMode;
        if (!_state.EmergencyStopReleased) return OutputBlockReason.EmergencyStop;
        if (!_state.AirPressureOk) return OutputBlockReason.AirPressureLow;
        return _state.Alarm is MachineAlarm.None or MachineAlarm.MotionUnavailable
            or MachineAlarm.HomeFailed or MachineAlarm.Inspection
            ? OutputBlockReason.None : OutputBlockReason.MachineAlarm;
    }

    internal OutputBlockReason GetManualOutputBlock(OutputIo signal, bool live = false)
    {
        var block = live ? GetManualOutputSafetyBlock() : _state.Display.ManualOutputBlock;
        if (block != OutputBlockReason.None) return block;
        if (live && _state.IsRunning) return OutputBlockReason.Busy;

        if (signal == OutputIo.MainConveyorRun) return GetConveyorOutputBlock(live);
        if (signal == OutputIo.NgConveyorRun) return GetNgConveyorOutputBlock();

        // Direction/speed selection does not start the motor. The common idle
        // gate above is checked again live before either an ON or OFF write.
        if (signal is OutputIo.MainConveyorReverse or OutputIo.MainConveyorNormalSpeed
            or OutputIo.NgConveyorReverse or OutputIo.NgConveyorNormalSpeed)
            return OutputBlockReason.None;

        if (signal is OutputIo.MachineLight or OutputIo.TowerLampGreen or OutputIo.TowerLampYellow
            or OutputIo.TowerLampRed or OutputIo.Buzzer
            or OutputIo.NgCarrierEjectLamp or OutputIo.NgCarrierEjectCompleteLamp)
            return OutputBlockReason.None;

        if (IsInterfaceOutput(signal)) return GetInterfaceOutputBlock(signal, live);

        if (signal is OutputIo.PcbPlacementStopperUp or OutputIo.BoltFasteningStopperUp
            or OutputIo.InspectionStopperUp or OutputIo.NgConveyorStopperUp)
            return GetStopperOutputBlock(signal, live);

        // Motor, shuttle and shooting outputs need their dedicated
        // sequences/hold-to-run controls, not an unrestricted latched toggle.
        if (!_teachingOutputs.TryGetValue(signal, out var entry) || entry.Output.HoldToRun)
            return OutputBlockReason.DedicatedControlRequired;

        if (entry.Output.RequiresHandler)
        {
            var motion = _state.GetMotionStatus(entry.Group);
            if (!_units.IsMotionEnabled(entry.Group)) return OutputBlockReason.HandlerDisabled;
            if (!motion.Feedback.IsReady) return OutputBlockReason.HandlerUnavailable;
            if (!_state.ServoMainContactorOn) return OutputBlockReason.ServoPowerOff;
            foreach (var axis in motion.Feedback.Axes)
            {
                var feedback = live ? motion.Feedback.GetAxisState(axis) : motion.Axes[axis].State;
                if (feedback is not { } state) return OutputBlockReason.HandlerUnavailable;
                if (!state.Homed) return OutputBlockReason.HandlerNotHomed;
                if (!state.ServoOn) return OutputBlockReason.HandlerServoOff;
                if (state.Alarm || state.Emergency) return OutputBlockReason.HandlerMotionFault;
            }

            if (entry.Group == MotionGroup.PcbSupply
                    && (live ? _state.PlacementInBufferArea : _state.Display.PlacementInBufferArea)
                || entry.Group == MotionGroup.PcbPlacementHandler
                    && (live ? _state.SupplyInBufferArea : _state.Display.SupplyInBufferArea))
                return OutputBlockReason.OtherHandlerInBuffer;
        }

        if (entry.Output.CanSet?.Invoke(live) == false
            || signal == OutputIo.PcbSupplyRotate && !_supplyHandler.CanRotateInPlace(live))
            return OutputBlockReason.OutputInterlock;
        return OutputBlockReason.None;
    }

    internal static bool IsInterfaceOutput(OutputIo signal) => signal is
        OutputIo.PcbSupplyReadyToFront1 or OutputIo.MainConveyorReadyToFront2 or OutputIo.MainConveyorAvailableToRear;

    internal static bool IsConveyorRunOutput(OutputIo signal) => signal is
        OutputIo.MainConveyorRun or OutputIo.NgConveyorRun;

    internal void StopManualConveyor(OutputIo signal) =>
        TryRunManual(() => StopConveyorMotor(signal), () => _io.IsReady, MachineAlarm.IoCommunication);

    private void StopConveyorMotor(OutputIo signal)
    {
        if (!_io.IsReady) return;
        if (signal == OutputIo.MainConveyorRun) _conveyor.Stop();
        else if (signal == OutputIo.NgConveyorRun) _ngConveyor.Stop();
        else throw new ArgumentOutOfRangeException(nameof(signal));
    }

    private OutputBlockReason GetNgConveyorOutputBlock() =>
        _units.NgConveyor ? OutputBlockReason.None : OutputBlockReason.NgConveyorDisabled;

    private OutputBlockReason GetConveyorOutputBlock(bool live)
    {
        if (!_units.MainConveyor) return OutputBlockReason.MainConveyorDisabled;
        var path = live ? GetMainConveyorPathBlock() : _state.Display.MainConveyorPathBlock;
        if (path != OutputBlockReason.None) return path;
        if (_io.GetInput(InputIo.MainConveyorEntryCarrierDetected)
            || _io.GetInput(InputIo.PcbPlacementCarrierPresent)
            || _io.GetInput(InputIo.BoltFasteningCarrierPresent)
            || _io.GetInput(InputIo.InspectionCarrierPresent)
            || _io.GetInput(InputIo.MainConveyorExitCarrierDetected))
            return OutputBlockReason.MainConveyorCarrierDetected;
        return OutputBlockReason.None;
    }

    private OutputBlockReason GetStopperOutputBlock(OutputIo signal, bool live)
    {
        if (signal == OutputIo.NgConveyorStopperUp)
            return !_units.NgConveyor ? OutputBlockReason.NgConveyorDisabled
                : _io.GetInput(InputIo.NgConveyorPosition1Occupied)
                    || _io.GetInput(InputIo.NgConveyorPosition2Occupied)
                    || _io.GetInput(InputIo.NgShuttleCarrierDetected)
                    ? OutputBlockReason.NgConveyorOccupied : OutputBlockReason.None;

        if (!_units.MainConveyor) return OutputBlockReason.MainConveyorDisabled;
        var path = live ? GetMainConveyorPathBlock() : _state.Display.MainConveyorPathBlock;
        if (path != OutputBlockReason.None) return path;
        var (carrier, plateUp, plateDown) = signal switch
        {
            OutputIo.PcbPlacementStopperUp => (InputIo.PcbPlacementCarrierPresent,
                InputIo.PcbPlacementBackupPlateUp, InputIo.PcbPlacementBackupPlateDown),
            OutputIo.BoltFasteningStopperUp => (InputIo.BoltFasteningCarrierPresent,
                InputIo.BoltFasteningBackupPlateUp, InputIo.BoltFasteningBackupPlateDown),
            _ => (InputIo.InspectionCarrierPresent, InputIo.InspectionBackupPlateUp, InputIo.InspectionBackupPlateDown),
        };
        if (_io.GetInput(carrier)) return OutputBlockReason.StationCarrierDetected;
        return _io.GetInput(plateUp) || !_io.GetInput(plateDown)
            ? OutputBlockReason.BackupPlateNotDown : OutputBlockReason.None;
    }

    private OutputBlockReason GetInterfaceOutputBlock(OutputIo signal, bool live)
    {
        if (signal == OutputIo.PcbSupplyReadyToFront1)
        {
            if (!_units.PcbSupply) return OutputBlockReason.PcbSupplyDisabled;
        }
        else if (!_units.MainConveyor) return OutputBlockReason.MainConveyorDisabled;
        // A manual interface signal does not command a servo axis. Keep actual
        // transfer/collision interlocks, not whole-machine homing/servo readiness.
        if (live ? _state.BufferConflict : _state.Display.BufferConflict)
            return OutputBlockReason.BufferConflict;
        if (Array.Exists(CarrierInputs, _io.GetInput) || _io.GetInput(InputIo.PcbSupplyPcbDetected)
            || _io.GetInput(InputIo.PcbPlacementPcbDetected) || _io.GetInput(InputIo.PcbBufferPcbPresent))
            return OutputBlockReason.MaterialDetected;
        if (_io.GetInput(InputIo.PcbSupplyAvailableFromFront1)
            || _io.GetInput(InputIo.MainConveyorAvailableFromFront2)
            || _io.GetInput(InputIo.MainConveyorReadyFromRear))
            return OutputBlockReason.PeerHandshakeActive;
        return live ? GetMainConveyorPathBlock() : _state.Display.MainConveyorPathBlock;
    }

    internal async Task ToggleManualOutputAsync(OutputIo signal, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            // The display controls button availability only. Recheck live state
            // before any write, including direct invocation of a disabled command.
            var block = GetManualOutputBlock(signal, live: true);
            if (block != OutputBlockReason.None)
            {
                _log?.Write($"Manual output {signal} ignored: [{block}] {block.GetDescription()}");
                return;
            }

            using var operation = _operations.Link(cancellationToken);
            var startingAlarm = _state.Alarm;
            var conveyorTest = IsConveyorRunOutput(signal);
            var interfaceTest = IsInterfaceOutput(signal);
            var maintainedOutput = conveyorTest || interfaceTest;
            var outputStarted = false;
            void StopWhenUnavailable()
            {
                if (operation.IsCancellationRequested) return;
                try
                {
                    var reason = GetManualOutputSafetyBlock();
                    if (reason == OutputBlockReason.None && _state.Alarm != startingAlarm)
                        reason = OutputBlockReason.AlarmChanged;
                    if (reason == OutputBlockReason.None && interfaceTest)
                        reason = GetInterfaceOutputBlock(signal, live: true);
                    if (reason == OutputBlockReason.None && conveyorTest)
                        reason = signal == OutputIo.MainConveyorRun
                            ? GetConveyorOutputBlock(live: true) : GetNgConveyorOutputBlock();
                    if (reason != OutputBlockReason.None)
                    {
                        operation.Cancel();
                        _log?.Write($"Manual output {signal} stopped: [{reason}] {reason.GetDescription()}");
                    }
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
            void OnDiagnosticOutputChanged(OutputIo output, bool on)
            {
                // STOP from another window must also release this operation's lifetime.
                if (outputStarted && output == signal && !on) operation.Cancel();
            }
            _state.Changed += StopWhenUnavailable;
            if (maintainedOutput) _io.InputChanged += OnDiagnosticInputChanged;
            if (maintainedOutput) _io.OutputChanged += OnDiagnosticOutputChanged;
            if (maintainedOutput) _state.DisplayChanged += StopWhenUnavailable;
            try
            {
                StopWhenUnavailable();
                operation.Token.ThrowIfCancellationRequested();
                if (maintainedOutput)
                {
                    try
                    {
                        Task? motorRun = null;
                        if (conveyorTest)
                        {
                            _log?.Write($"Manual conveyor {signal}: ON, forward, normal speed; alarm={startingAlarm}.");
                            if (signal == OutputIo.MainConveyorRun) _conveyor.RunMotor(operation.Token);
                            else motorRun = _ngConveyor.RunMotorAsync(operation.Token);
                        }
                        else
                        {
                            _log?.Write($"Manual interface output {signal}: ON until OFF or cancellation. Connected equipment must be stopped.");
                            _io.SetOutput(signal, true);
                        }
                        outputStarted = true;
                        if (!_io.GetOutput(signal)) operation.Cancel();
                        // Own ON until cancellation; OFF must not wait for the UI dispatcher.
                        await (motorRun ?? Task.Delay(Timeout.Infinite, operation.Token)).ConfigureAwait(false);
                    }
                    finally
                    {
                        if (conveyorTest) StopConveyorMotor(signal);
                        else _io.SetOutput(signal, false);
                        _log?.Write($"Manual output {signal}: OFF.");
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
                if (maintainedOutput) _io.OutputChanged -= OnDiagnosticOutputChanged;
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
