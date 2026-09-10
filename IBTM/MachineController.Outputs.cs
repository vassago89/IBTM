using System;
using System.Runtime.ExceptionServices;
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

    // Shared minimum conditions for direct I/O and manual conveyor runs.
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

    internal void StopManualConveyor(OutputIo signal)
    {
        try
        {
            if (signal == OutputIo.MainConveyorRun)
                _conveyor.Stop();
            else if (signal == OutputIo.NgConveyorRun)
                _ngConveyor.Stop();
            else
                throw new ArgumentOutOfRangeException(nameof(signal));
        }
        catch (Exception exception)
        {
            _state.SetError(MachineAlarm.IoCommunication, exception);
            _operations.Cancel();
        }
    }

    internal async Task<OutputBlockReason> RunManualConveyorAsync(
        OutputIo signal,
        CancellationToken cancellationToken)
    {
        OperationCancellation.Operation operation;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var block = GetManualOutputSafetyBlock();
            if (block != OutputBlockReason.None)
            {
                _log?.Write($"Manual conveyor {signal} ignored: [{block}] {block.GetDescription()}");
                return block;
            }

            operation = _operations.Link(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _state.SetError(MachineAlarm.IoCommunication, exception);
            _operations.Cancel();
            throw;
        }

        var stopReason = OutputBlockReason.None;
        Exception? failure = null;
        using (operation)
        {
            var outputStarted = false;

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
                        _log?.Write($"Manual conveyor {signal} stopped: [{reason}] {reason.GetDescription()}");
                    }
                }
                catch (Exception exception)
                {
                    // This callback runs on the I/O worker. Cancel without stopping its scan.
                    _log?.Error($"Manual conveyor {signal}: interlock feedback could not be read.", exception);
                    operation.Cancel();
                }
            }

            void OnOutputChanged(OutputIo output, bool on)
            {
                // OFF from OUTPUTS must release the Manual operation too.
                if (outputStarted && output == signal && !on)
                    operation.Cancel();
            }

            _state.Changed += StopWhenUnavailable;
            _io.OutputChanged += OnOutputChanged;
            Task motorRun = Task.CompletedTask;
            try
            {
                StopWhenUnavailable();
                operation.Token.ThrowIfCancellationRequested();
                _log?.Write($"Manual conveyor {signal}: ON, forward; alarm={_state.Alarm}.");
                if (signal == OutputIo.MainConveyorRun)
                {
                    motorRun = _conveyor.RunMotorAsync(operation.Token);
                }
                else if (signal == OutputIo.NgConveyorRun)
                {
                    motorRun = _ngConveyor.RunMotorAsync(operation.Token);
                }
                else
                {
                    throw new ArgumentOutOfRangeException(nameof(signal));
                }

                outputStarted = true;
                if (!_io.GetOutput(signal))
                    operation.Cancel();
            }
            catch (OperationCanceledException) when (operation.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            // A read failure after starting must still cancel and await the device's cleanup.
            try
            {
                if (failure is not null && !operation.IsCancellationRequested)
                    operation.Cancel();
            }
            catch (Exception exception)
            {
                failure = new AggregateException(failure!, exception);
            }

            try
            {
                await motorRun.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (operation.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                failure = failure is null ? exception : new AggregateException(failure, exception);
            }
            finally
            {
                _state.Changed -= StopWhenUnavailable;
                _io.OutputChanged -= OnOutputChanged;
            }

            if (failure is null && outputStarted)
                _log?.Write($"Manual conveyor {signal}: OFF.");
        }

        if (failure is not null)
        {
            if (failure is not OperationCanceledException)
            {
                _state.SetError(MachineAlarm.IoCommunication, failure);
                _operations.Cancel();
            }
            ExceptionDispatchInfo.Throw(failure);
        }

        return stopReason;
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
            OutputIo.PcbSupplyRotate => _supplyHandler.IsInsideBuffer(live) == false,
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
            if (output.Signal == OutputIo.ShootBolt)
            {
                token.ThrowIfCancellationRequested();
                _io.SetOutput(output.Signal, value);
                return Task.CompletedTask;
            }

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
                OutputIo.NgCarrierPickupUp => _ngTransfer.SetLiftUpAsync(value, token),
                OutputIo.NgCarrierGripperOpen => _ngTransfer.SetGripperOpenAsync(value, token),
                OutputIo.NgShuttleUp => _ngShuttle.SetUpAsync(value, token),
                OutputIo.PcbPlacementBackupPlateDown
                    or OutputIo.BoltFasteningBackupPlateDown
                    or OutputIo.InspectionBackupPlateDown
                    or OutputIo.PcbPlacementStopperDown
                    or OutputIo.BoltFasteningStopperDown
                    or OutputIo.InspectionStopperDown
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
                HardwareArea.NgShuttle => MachineAlarm.NgShuttle,
                _ => throw new ArgumentOutOfRangeException(nameof(output)),
            },
            () => CanSetTeachingOutput(output),
            cancellationToken,
            viewCancellation);
    }
}
