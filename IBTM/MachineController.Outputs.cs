using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;
using Microsoft.Extensions.Logging;

namespace IBTM;

public sealed partial class MachineController
{
    // OUTPUTS writes just the selected logical output. Feedback is display-only;
    // it does not start a conveyor sequence, move an axis, or wait for a cylinder.
    internal OutputBlockReason ToggleDiagnosticOutput(OutputIo signal)
    {
        var block = ManualOutputSafetyBlock;
        if (block != OutputBlockReason.None)
        {
            _log?.LogInformation("{Message}", $"Direct output {signal} ignored: [{block}] {block.GetDescription()}");
            return block;
        }

        try
        {
            var value = signal switch
            {
                OutputIo.MainConveyorNormalSpeed or OutputIo.NgConveyorNormalSpeed => true,
                OutputIo.PcbPlacementHandlerRotate => false,
                _ => !_io.GetOutput(signal),
            };
            _io.SetOutput(signal, value);
            _log?.LogInformation("{Message}", $"Direct output {signal}: {(value ? "ON" : "OFF")}; alarm={_state.Alarm}.");
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
    private OutputBlockReason ManualOutputSafetyBlock
    {
        get
        {
            switch (true)
            {
                case true when _operations.IsShuttingDown:
                    return OutputBlockReason.ShuttingDown;
                case true when !_io.IsReady:
                    return OutputBlockReason.IoUnavailable;
                case true when !_state.ManualMode:
                    return OutputBlockReason.AutoMode;
                case true when !_state.EmergencyStopReleased:
                    return OutputBlockReason.EmergencyStop;
                default:
                    return OutputBlockReason.None;
            }
        }
    }

    internal async Task StopManualConveyorAsync(OutputIo signal)
    {
        await Task.Run(() =>
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
                _state.SetError(_state.IsError ? _state.Alarm : MachineAlarm.IoCommunication, exception);
                _operations.Cancel();
            }
        });
    }

    internal async Task<OutputBlockReason> RunManualConveyorAsync(
        OutputIo signal,
        CancellationToken cancellationToken)
    {
        OperationCancellation.Operation? operation;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var block = ManualOutputSafetyBlock;
            if (block != OutputBlockReason.None)
            {
                _log?.LogInformation("{Message}", $"Manual conveyor {signal} ignored: [{block}] {block.GetDescription()}");
                return block;
            }

            operation = _operations.TryBegin(cancellationToken);
            if (operation is null)
                return OutputBlockReason.Busy;
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
                    var reason = ManualOutputSafetyBlock;
                    if (reason != OutputBlockReason.None)
                    {
                        stopReason = reason;
                        operation.Cancel();
                        _log?.LogInformation("{Message}", $"Manual conveyor {signal} stopped: [{reason}] {reason.GetDescription()}");
                    }
                }
                catch (Exception exception)
                {
                    // This callback runs on the I/O worker. Cancel without stopping its scan.
                    _log?.LogError(exception, "{Message}", $"Manual conveyor {signal}: interlock feedback could not be read.");
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
                _log?.LogInformation("{Message}", $"Manual conveyor {signal}: ON, forward; alarm={_state.Alarm}.");
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
                _log?.LogInformation("{Message}", $"Manual conveyor {signal}: OFF.");
        }

        if (failure is not null)
        {
            if (failure is not OperationCanceledException)
            {
                _state.SetError(_state.IsError ? _state.Alarm : MachineAlarm.IoCommunication, failure);
                _operations.Cancel();
            }
            ExceptionDispatchInfo.Throw(failure);
        }

        return stopReason;
    }

    // Teaching may coordinate a handler as well as its cylinder output.
    internal bool IsSetTeachingOutputAllowed(TeachingOutput output, bool live = true)
    {
        return (live ? _state.ManualSetupEnabled : _state.Display.ManualSetupEnabled)
            && output.Signal != OutputIo.PcbPlacementHandlerRotate
            && (output.Signal != OutputIo.PcbSupplyRotate
                || IsManualMotionReady(MotionGroup.PcbSupply, live));
    }

    internal async Task ToggleTeachingOutputAsync(
        TeachingOutput output,
        CancellationToken cancellationToken,
        CancellationToken viewCancellation)
    {
        var viewToken = viewCancellation;
        var activeToken = cancellationToken;
        try
        {
            if (!IsSetTeachingOutputAllowed(output))
                return;
            using var operation = BeginManualOperation(
                () => _io.IsReady && _state.ManualMode && _state.SafetyReady,
                cancellationToken,
                viewToken);
            if (operation is null)
                return;
            activeToken = operation.Token;
            operation.Token.ThrowIfCancellationRequested();
            var value = !_io.GetOutput(output.Signal);
            if (output.Signal == OutputIo.ShootBolt)
            {
                _io.SetOutput(output.Signal, value);
                return;
            }
            switch (output.Signal)
            {
                case OutputIo.PcbSupplyGripperClosed:
                    await _supplyHandler.SetGripperClosedAsync(value, operation.Token);
                    break;
                case OutputIo.PcbSupplyIpmFixerForward:
                    await _supplyHandler.SetIpmFixerAsync(value, operation.Token);
                    break;
                case OutputIo.PcbSupplyRotate:
                    await _supplyHandler.SetRotatedAsync(value, operation.Token);
                    break;
                case OutputIo.PcbPlacementHandlerDown:
                    await _placementHandler.SetLiftDownAsync(value, operation.Token);
                    break;
                case OutputIo.PcbPlacementIpmDown:
                    await _placementHandler.SetIpmLiftDownAsync(value, operation.Token);
                    break;
                case OutputIo.PcbPlacementIpmGripperClose:
                    await _placementHandler.SetIpmGripperAsync(value, operation.Token);
                    break;
                case OutputIo.PcbPlacementVacuumEjector:
                    await _placementHandler.SetVacuumAsync(value, operation.Token);
                    break;
                case OutputIo.PickupHeadDown:
                    await _fasteningGantry.SetHeadDownAsync(FasteningHead.Pickup, value, operation.Token);
                    break;
                case OutputIo.ShootingHeadDown:
                    await _fasteningGantry.SetHeadDownAsync(FasteningHead.Shooting, value, operation.Token);
                    break;
                case OutputIo.PickupHeadVacuumPump:
                    await _fasteningGantry.SetVacuumAsync(FasteningHead.Pickup, value, operation.Token);
                    break;
                case OutputIo.ShootingHeadVacuumPump:
                    await _fasteningGantry.SetVacuumAsync(FasteningHead.Shooting, value, operation.Token);
                    break;
                case OutputIo.NgCarrierPickupDown:
                    await _ngTransfer.SetLiftUpAsync(!value, operation.Token);
                    break;
                case OutputIo.NgCarrierGripperClose:
                    await _ngTransfer.SetGripperOpenAsync(!value, operation.Token);
                    break;
                case OutputIo.NgShuttleDown:
                    await _ngShuttle.SetDownAsync(value, operation.Token);
                    break;
                case OutputIo.PcbPlacementBackupPlateUp:
                case OutputIo.BoltFasteningBackupPlateUp:
                case OutputIo.InspectionBackupPlateUp:
                case OutputIo.PcbPlacementStopperUp:
                case OutputIo.BoltFasteningStopperUp:
                case OutputIo.InspectionStopperUp:
                    await _io.SetOutputAndWaitAsync(output.Signal, value, operation.Token);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(output));
            }
        }
        catch (OperationCanceledException) when (activeToken.IsCancellationRequested
            || viewToken.IsCancellationRequested
            || _operations.IsShuttingDown)
        {
        }
        catch (Exception exception) when (MachineController.IsDeviceFailure(exception))
        {
            ReportManualFailure(output.Owner switch
            {
                HardwareArea.MainConveyor => MachineAlarm.MainConveyor,
                HardwareArea.PcbSupply => MachineAlarm.PcbSupply,
                HardwareArea.PcbPlacementHandler => MachineAlarm.PcbPlacement,
                HardwareArea.BoltFastening => MachineAlarm.BoltFastening,
                HardwareArea.NgCarrierTransfer => MachineAlarm.NgCarrierTransfer,
                HardwareArea.NgShuttle => MachineAlarm.NgShuttle,
                _ => throw new ArgumentOutOfRangeException(nameof(output)),
            }, exception);
        }
    }
}
