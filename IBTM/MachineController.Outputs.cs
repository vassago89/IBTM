using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;

namespace IBTM;

public sealed partial class MachineController
{
    // OUTPUTS writes just the selected logical output. Feedback is display-only;
    // it does not start a conveyor sequence, move an axis, or wait for a cylinder.
    internal OutputBlockReason ToggleDiagnosticOutput(OutputIo signal)
    {
        var block = _operations.IsShuttingDown ? OutputBlockReason.ShuttingDown
            : !_io.IsReady ? OutputBlockReason.IoUnavailable
            : !_state.ManualMode ? OutputBlockReason.AutoMode
            : !_state.EmergencyStopReleased ? OutputBlockReason.EmergencyStop
            : OutputBlockReason.None;
        if (block != OutputBlockReason.None)
        {
            _log?.Write($"Direct output {signal} ignored: [{block}] {block.GetDescription()}");
            return block;
        }

        try
        {
            var value = !_io.GetOutput(signal);
            _io.SetOutput(signal, value);
            _log?.Write($"Direct output {signal}: {(value ? "ON" : "OFF")}; alarm={_state.Alarm}.");
            _state.RequestDisplayRefresh();
            return OutputBlockReason.None;
        }
        catch (Exception exception)
        {
            _state.SetError(MachineAlarm.IoCommunication, exception);
            _operations.Cancel();
            throw;
        }
    }

    // Coordinated manual controls retain their own admission and stop conditions.
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

    private OutputBlockReason GetManualOutputBlock(OutputIo signal)
    {
        var block = GetManualOutputSafetyBlock();
        if (block != OutputBlockReason.None) return block;

        if (signal == OutputIo.MainConveyorRun) return GetConveyorOutputBlock();
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

        if (IsInterfaceOutput(signal)) return GetInterfaceOutputBlock(signal);

        if (signal is OutputIo.PcbPlacementStopperUp or OutputIo.BoltFasteningStopperUp
            or OutputIo.InspectionStopperUp or OutputIo.NgConveyorStopperUp)
            return GetStopperOutputBlock(signal);

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
                var state = motion.Feedback.GetAxisState(axis);
                if (!state.Homed) return OutputBlockReason.HandlerNotHomed;
                if (!state.ServoOn) return OutputBlockReason.HandlerServoOff;
                if (state.Alarm || state.Emergency) return OutputBlockReason.HandlerMotionFault;
            }

            if (entry.Group == MotionGroup.PcbSupply
                    && _state.PlacementInBufferArea
                || entry.Group == MotionGroup.PcbPlacementHandler
                    && _state.SupplyInBufferArea)
                return OutputBlockReason.OtherHandlerInBuffer;
        }

        if (!TeachingOutputInterlockReady(signal, live: true)
            || signal == OutputIo.PcbSupplyRotate && !_supplyHandler.CanRotateInPlace())
            return OutputBlockReason.OutputInterlock;
        return OutputBlockReason.None;
    }

    internal static bool IsInterfaceOutput(OutputIo signal) => signal is
        OutputIo.PcbSupplyReadyToFront1 or OutputIo.MainConveyorReadyToFront2 or OutputIo.MainConveyorAvailableToRear;

    internal static bool IsConveyorRunOutput(OutputIo signal) => signal is
        OutputIo.MainConveyorRun or OutputIo.NgConveyorRun;

    internal void StopManualOutput(OutputIo signal)
    {
        try
        {
            if (!_io.IsReady) return;
            if (IsConveyorRunOutput(signal)) StopConveyorMotor(signal);
            else if (IsInterfaceOutput(signal)) _io.SetOutput(signal, false);
            else throw new ArgumentOutOfRangeException(nameof(signal));
        }
        catch (Exception exception)
        {
            _state.SetError(MachineAlarm.IoCommunication, exception);
            _operations.Cancel();
        }
    }

    private void StopConveyorMotor(OutputIo signal)
    {
        if (!_io.IsReady) return;
        if (signal == OutputIo.MainConveyorRun) _conveyor.Stop();
        else if (signal == OutputIo.NgConveyorRun) _ngConveyor.Stop();
        else throw new ArgumentOutOfRangeException(nameof(signal));
    }

    private OutputBlockReason GetNgConveyorOutputBlock() =>
        _units.NgConveyor ? OutputBlockReason.None : OutputBlockReason.NgConveyorDisabled;

    private OutputBlockReason GetConveyorOutputBlock()
    {
        if (!_units.MainConveyor) return OutputBlockReason.MainConveyorDisabled;
        var path = GetMainConveyorPathBlock();
        if (path != OutputBlockReason.None) return path;
        if (_io.GetInput(InputIo.MainConveyorEntryCarrierDetected)
            || _io.GetInput(InputIo.PcbPlacementCarrierPresent)
            || _io.GetInput(InputIo.BoltFasteningCarrierPresent)
            || _io.GetInput(InputIo.InspectionCarrierPresent)
            || _io.GetInput(InputIo.MainConveyorExitCarrierDetected))
            return OutputBlockReason.MainConveyorCarrierDetected;
        return OutputBlockReason.None;
    }

    private OutputBlockReason GetStopperOutputBlock(OutputIo signal)
    {
        if (signal == OutputIo.NgConveyorStopperUp)
            return !_units.NgConveyor ? OutputBlockReason.NgConveyorDisabled
                : _io.GetInput(InputIo.NgConveyorPosition1Occupied)
                    || _io.GetInput(InputIo.NgConveyorPosition2Occupied)
                    || _io.GetInput(InputIo.NgShuttleCarrierDetected)
                    ? OutputBlockReason.NgConveyorOccupied : OutputBlockReason.None;

        if (!_units.MainConveyor) return OutputBlockReason.MainConveyorDisabled;
        var path = GetMainConveyorPathBlock();
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

    private OutputBlockReason GetInterfaceOutputBlock(OutputIo signal)
    {
        if (signal == OutputIo.PcbSupplyReadyToFront1)
        {
            if (!_units.PcbSupply) return OutputBlockReason.PcbSupplyDisabled;
        }
        else if (!_units.MainConveyor) return OutputBlockReason.MainConveyorDisabled;
        // A manual interface signal does not command a servo axis. Keep actual
        // transfer/collision interlocks, not whole-machine homing/servo readiness.
        if (_state.BufferConflict)
            return OutputBlockReason.BufferConflict;
        if (Array.Exists(CarrierInputs, _io.GetInput) || _io.GetInput(InputIo.PcbSupplyPcbDetected)
            || _io.GetInput(InputIo.PcbPlacementPcbDetected) || _io.GetInput(InputIo.PcbBufferPcbPresent))
            return OutputBlockReason.MaterialDetected;
        if (_io.GetInput(InputIo.PcbSupplyAvailableFromFront1)
            || _io.GetInput(InputIo.MainConveyorAvailableFromFront2)
            || _io.GetInput(InputIo.MainConveyorReadyFromRear))
            return OutputBlockReason.PeerHandshakeActive;
        return GetMainConveyorPathBlock();
    }

    internal async Task<OutputBlockReason> ToggleManualOutputAsync(
        OutputIo signal,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Button state is not an interlock. Both views use this live admission check.
            var block = GetManualOutputBlock(signal);
            if (block == OutputBlockReason.None && _state.IsRunning) block = OutputBlockReason.Busy;
            if (block != OutputBlockReason.None)
            {
                _log?.Write($"Manual output {signal} ignored: [{block}] {block.GetDescription()}");
                return block;
            }

            using var operation = _operations.Link(cancellationToken);
            var startingAlarm = _state.Alarm;
            var conveyorTest = IsConveyorRunOutput(signal);
            var interfaceTest = IsInterfaceOutput(signal);
            var maintainedOutput = conveyorTest || interfaceTest;
            var outputStarted = false;
            var stopReason = OutputBlockReason.None;
            void StopWhenUnavailable()
            {
                if (operation.IsCancellationRequested) return;
                try
                {
                    var reason = maintainedOutput ? GetManualOutputBlock(signal) : GetManualOutputSafetyBlock();
                    if (reason == OutputBlockReason.None && _state.Alarm != startingAlarm)
                        reason = OutputBlockReason.AlarmChanged;
                    if (reason != OutputBlockReason.None)
                    {
                        stopReason = reason;
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
            void OnMotionPositionChanged(double _, double __, double ___) => StopWhenUnavailable();
            void OnDiagnosticOutputChanged(OutputIo output, bool on)
            {
                // STOP from another window must also release this operation's lifetime.
                if (outputStarted && output == signal && !on) operation.Cancel();
            }
            _state.Changed += StopWhenUnavailable;
            if (maintainedOutput) _io.InputChanged += OnDiagnosticInputChanged;
            if (maintainedOutput) _io.OutputChanged += OnDiagnosticOutputChanged;
            if (maintainedOutput)
            {
                _placementHandler.Feedback.PositionChanged += OnMotionPositionChanged;
                _fasteningGantry.Feedback.PositionChanged += OnMotionPositionChanged;
                _inspectionGantry.Feedback.PositionChanged += OnMotionPositionChanged;
            }
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
                    return OutputBlockReason.None;
                }
                var value = !_io.GetOutput(signal);
                // This window never moves an axis as a side effect of a toggle.
                _log?.Write($"Manual output {signal}: {(value ? "ON" : "OFF")}; alarm={startingAlarm}.");
                operation.Token.ThrowIfCancellationRequested();
                _io.SetOutput(signal, value);
                if (_io.GetOutputFeedback(signal) is not null)
                    await _io.WaitForOutputFeedbackAsync(signal, value, operation.Token);
            }
            catch (OperationCanceledException) when (operation.IsCancellationRequested)
            {
                return stopReason;
            }
            finally
            {
                _state.Changed -= StopWhenUnavailable;
                if (maintainedOutput) _io.InputChanged -= OnDiagnosticInputChanged;
                if (maintainedOutput) _io.OutputChanged -= OnDiagnosticOutputChanged;
                if (maintainedOutput)
                {
                    _placementHandler.Feedback.PositionChanged -= OnMotionPositionChanged;
                    _fasteningGantry.Feedback.PositionChanged -= OnMotionPositionChanged;
                    _inspectionGantry.Feedback.PositionChanged -= OnMotionPositionChanged;
                }
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
            throw;
        }
        return OutputBlockReason.None;
    }

    // Teaching may coordinate a handler as well as its cylinder output.
    internal bool CanSetTeachingOutput(TeachingOutput output, bool live = true) =>
        (live ? _state.ManualSetupEnabled : _state.Display.ManualSetupEnabled)
        && TeachingOutputInterlockReady(output.Signal, live)
        && (!output.RequiresHandler || CanUseManualMotion(output.Owner switch
        {
            HardwareArea.PcbSupply => MotionGroup.PcbSupply,
            HardwareArea.PcbPlacementHandler => MotionGroup.PcbPlacementHandler,
            HardwareArea.BoltFastening => MotionGroup.BoltFastening,
            HardwareArea.NgCarrierTransfer => MotionGroup.InspectionGantry,
            _ => throw new ArgumentOutOfRangeException(nameof(output)),
        }, live));

    private bool TeachingOutputInterlockReady(OutputIo signal, bool live) => signal switch
    {
        OutputIo.PcbSupplyRotate => !_supplyHandler.IsInsideBuffer(live),
        OutputIo.PcbPlacementHandlerRotate => _placementHandler.IsAtHorizontalZ(live)
            && _placementHandler.CanMoveHorizontal,
        OutputIo.NgCarrierPickupDown or OutputIo.NgCarrierGripperClose => _units.NgCarrierTransfer,
        _ => true,
    };

    internal Task RunTeachingOutputAsync(
        TeachingOutput output,
        bool value,
        CancellationToken cancellationToken,
        CancellationToken viewCancellation)
    {
        Task SetOutput(CancellationToken token) => output.Signal switch
        {
            OutputIo.PcbSupplyGripperClosed => _supplyHandler.SetGripperClosedAsync(value, token),
            OutputIo.PcbSupplyIpmFixerForward => _supplyHandler.SetIpmFixerAsync(value, token),
            OutputIo.PcbSupplyRotate => _supplyHandler.SetRotatedAsync(value, token),
            OutputIo.PcbPlacementHandlerDown => _placementHandler.SetLiftDownAsync(value, token),
            OutputIo.PcbPlacementIpmDown => _placementHandler.SetIpmLiftDownAsync(value, token),
            OutputIo.PcbPlacementIpmGripperClose => _placementHandler.SetIpmGripperAsync(value, token),
            OutputIo.PcbPlacementVacuumEjector => _placementHandler.SetVacuumAsync(value, token),
            OutputIo.PcbPlacementHandlerRotate => _placementHandler.SetRotatedAsync(value, token),
            OutputIo.PickupHeadDown => _fasteningGantry.SetPickupHeadDownAsync(value, token),
            OutputIo.ShootingHeadDown => _fasteningGantry.SetHeadDownAsync(FasteningHead.Shooting, value, token),
            OutputIo.PickupHeadVacuumPump => _fasteningGantry.SetVacuumAsync(FasteningHead.Pickup, value, token),
            OutputIo.ShootingHeadVacuumPump => _fasteningGantry.SetVacuumAsync(FasteningHead.Shooting, value, token),
            OutputIo.ShootBolt => _fasteningGantry.SetManualShootingAsync(value, token),
            OutputIo.NgCarrierPickupDown => _ngTransfer.SetLiftDownAsync(value, token),
            OutputIo.NgCarrierGripperClose => _ngTransfer.SetGripperClosedAsync(value, token),
            OutputIo.PcbPlacementBackupPlateUp or OutputIo.BoltFasteningBackupPlateUp or OutputIo.InspectionBackupPlateUp
                => _io.SetOutputAndWaitAsync(output.Signal, value, token),
            _ => throw new ArgumentOutOfRangeException(nameof(output)),
        };
        return RunManualAsync(SetOutput, output.Owner switch
        {
            HardwareArea.MainConveyor => MachineAlarm.MainConveyor,
            HardwareArea.PcbSupply => MachineAlarm.PcbSupply,
            HardwareArea.PcbPlacementHandler => MachineAlarm.PcbPlacement,
            HardwareArea.BoltFastening => MachineAlarm.BoltFastening,
            HardwareArea.NgCarrierTransfer => MachineAlarm.NgCarrierTransfer,
            _ => throw new ArgumentOutOfRangeException(nameof(output)),
        }, () => CanSetTeachingOutput(output), cancellationToken, viewCancellation);
    }
}
