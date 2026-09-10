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
        var block = GetManualOutputSafetyBlock();
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

    // Shared minimum conditions for direct I/O and manual motor tests.
    private OutputBlockReason GetManualOutputSafetyBlock()
    {
        if (_operations.IsShuttingDown)
            return OutputBlockReason.ShuttingDown;
        if (!_io.IsReady)
            return OutputBlockReason.IoUnavailable;
        if (!_state.ManualMode)
            return OutputBlockReason.AutoMode;
        if (!_state.EmergencyStopReleased)
            return OutputBlockReason.EmergencyStop;
        return OutputBlockReason.None;
    }

    internal static bool IsInterfaceOutput(OutputIo signal)
    {
        return signal is OutputIo.PcbSupplyReadyToFront1
            or OutputIo.MainConveyorReadyToFront2
            or OutputIo.MainConveyorAvailableToRear;
    }

    internal static bool IsConveyorRunOutput(OutputIo signal)
    {
        return signal is OutputIo.MainConveyorRun or OutputIo.NgConveyorRun;
    }

    internal void StopManualOutput(OutputIo signal)
    {
        try
        {
            if (!_io.IsReady)
                return;
            if (IsConveyorRunOutput(signal))
                StopConveyorMotor(signal);
            else if (IsInterfaceOutput(signal))
                _io.SetOutput(signal, false);
            else
                throw new ArgumentOutOfRangeException(nameof(signal));
        }
        catch (Exception exception)
        {
            _state.SetError(MachineAlarm.IoCommunication, exception);
            _operations.Cancel();
        }
    }

    private void StopConveyorMotor(OutputIo signal)
    {
        if (!_io.IsReady)
            return;
        if (signal == OutputIo.MainConveyorRun)
            _conveyor.Stop();
        else if (signal == OutputIo.NgConveyorRun)
            _ngConveyor.Stop();
        else
            throw new ArgumentOutOfRangeException(nameof(signal));
    }

    internal async Task<OutputBlockReason> ToggleManualOutputAsync(
        OutputIo signal,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Button state is not an interlock. Both views use this live admission check.
            var block = GetManualOutputSafetyBlock();
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
                if (operation.IsCancellationRequested)
                    return;
                try
                {
                    var reason = GetManualOutputSafetyBlock();
                    if (reason != OutputBlockReason.None)
                    {
                        stopReason = reason;
                        operation.Cancel();
                        _log?.Write(
                            $"Manual output {signal} stopped: [{reason}] {reason.GetDescription()}");
                    }
                }
                catch (Exception exception)
                {
                    // State notifications can run on the I/O worker; stop this
                    // operation without propagating a callback failure to that worker.
                    _log?.Error(
                        $"Manual output {signal} stopped: interlock feedback could not be read.",
                        exception);
                    operation.Cancel();
                }
            }

            void OnDiagnosticOutputChanged(OutputIo output, bool on)
            {
                // STOP from another window must also release this operation's lifetime.
                if (outputStarted && output == signal && !on)
                    operation.Cancel();
            }

            _state.Changed += StopWhenUnavailable;
            if (maintainedOutput)
                _io.OutputChanged += OnDiagnosticOutputChanged;
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
                            _log?.Write(
                                $"Manual conveyor {signal}: ON, forward, normal speed; alarm={startingAlarm}.");
                            if (signal == OutputIo.MainConveyorRun)
                                _conveyor.RunMotor(operation.Token);
                            else
                                motorRun = _ngConveyor.RunMotorAsync(operation.Token);
                        }
                        else
                        {
                            _log?.Write(
                                $"Manual interface output {signal}: ON until OFF or cancellation. Connected equipment must be stopped.");
                            _io.SetOutput(signal, true);
                        }

                        outputStarted = true;
                        if (!_io.GetOutput(signal))
                            operation.Cancel();
                        // Own ON until cancellation; OFF must not wait for the UI dispatcher.
                        await (motorRun ?? Task.Delay(Timeout.Infinite, operation.Token)).ConfigureAwait(false);
                    }
                    finally
                    {
                        if (conveyorTest)
                            StopConveyorMotor(signal);
                        else
                            _io.SetOutput(signal, false);
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
                if (maintainedOutput)
                    _io.OutputChanged -= OnDiagnosticOutputChanged;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
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
    internal bool CanSetTeachingOutput(TeachingOutput output, bool live = true)
    {
        return (live ? _state.ManualSetupEnabled : _state.Display.ManualSetupEnabled)
            && TeachingOutputInterlockReady(output.Signal, live)
            && (output.Signal != OutputIo.PcbSupplyRotate
                || CanUseManualMotion(MotionGroup.PcbSupply, live));
    }

    private bool TeachingOutputInterlockReady(OutputIo signal, bool live)
    {
        return signal switch
        {
            OutputIo.PcbSupplyRotate => !_supplyHandler.IsInsideBuffer(live),
            OutputIo.PcbPlacementHandlerRotate

                => _placementHandler.IsAtHorizontalZ(live)
                    && _placementHandler.CanMoveHorizontal,
            _ => true,
        };
    }

    internal Task RunTeachingOutputAsync(
        TeachingOutput output,
        bool value,
        CancellationToken cancellationToken,
        CancellationToken viewCancellation)
    {
        Task SetOutput(CancellationToken token)
        {
            return output.Signal switch
            {
                OutputIo.PcbSupplyGripperClosed => _supplyHandler.SetGripperClosedAsync(value, token),
                OutputIo.PcbSupplyIpmFixerForward => _supplyHandler.SetIpmFixerAsync(value, token),
                OutputIo.PcbSupplyRotate => _supplyHandler.SetRotatedAsync(value, token),
                OutputIo.PcbPlacementHandlerDown => _placementHandler.SetLiftDownAsync(value, token),
                OutputIo.PcbPlacementIpmDown => _placementHandler.SetIpmLiftDownAsync(value, token),
                OutputIo.PcbPlacementIpmGripperClose

                    => _placementHandler.SetIpmGripperAsync(value, token),
                OutputIo.PcbPlacementVacuumEjector => _placementHandler.SetVacuumAsync(value, token),
                OutputIo.PcbPlacementHandlerRotate => _placementHandler.SetRotatedAsync(value, token),
                OutputIo.PickupHeadDown => _fasteningGantry.SetPickupHeadDownAsync(value, token),
                OutputIo.ShootingHeadDown

                    => _fasteningGantry.SetHeadDownAsync(FasteningHead.Shooting, value, token),
                OutputIo.PickupHeadVacuumPump

                    => _fasteningGantry.SetVacuumAsync(FasteningHead.Pickup, value, token),
                OutputIo.ShootingHeadVacuumPump

                    => _fasteningGantry.SetVacuumAsync(FasteningHead.Shooting, value, token),
                OutputIo.ShootBolt => _fasteningGantry.SetManualShootingAsync(value, token),
                OutputIo.NgCarrierPickupDown => _ngTransfer.SetLiftDownAsync(value, token),
                OutputIo.NgCarrierGripperClose => _ngTransfer.SetGripperClosedAsync(value, token),
                OutputIo.PcbPlacementBackupPlateUp
                    or OutputIo.BoltFasteningBackupPlateUp
                    or OutputIo.InspectionBackupPlateUp

                    => _io.SetOutputAndWaitAsync(output.Signal, value, token),
                _ => throw new ArgumentOutOfRangeException(nameof(output)),
            };
        }

        return RunManualAsync(
            SetOutput,
            output.Owner switch
            {
                HardwareArea.MainConveyor => MachineAlarm.MainConveyor,
                HardwareArea.PcbSupply => MachineAlarm.PcbSupply,
                HardwareArea.PcbPlacementHandler => MachineAlarm.PcbPlacement,
                HardwareArea.BoltFastening => MachineAlarm.BoltFastening,
                HardwareArea.NgCarrierTransfer => MachineAlarm.NgCarrierTransfer,
                _ => throw new ArgumentOutOfRangeException(nameof(output)),
            },
            () => CanSetTeachingOutput(output),
            cancellationToken,
            viewCancellation);
    }
}
