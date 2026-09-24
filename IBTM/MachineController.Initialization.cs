using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;
using Microsoft.Extensions.Logging;

namespace IBTM;

public sealed partial class MachineController
{
    public async Task InitializeAsync()
    {
        _log?.LogInformation("Machine initialization started.");
        using (var operation = _operations.TryBegin())
        {
            if (operation is null)
                return;
            var (alarm, error) = await InitializeHardwareAsync(operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            if (alarm == MachineAlarm.None)
                alarm = SafetyAlarm;

            if (alarm == MachineAlarm.None)
                _state.Refresh();
            else
                _state.SetError(alarm, error);
        }

        UpdateMachineIndicators();
        _log?.LogInformation("{Message}", $"Machine initialization finished. Alarm={_state.Alarm}.");
    }

    private async Task<(MachineAlarm Alarm, Exception? Error)> InitializeIoAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stage = "Control I/O initialization";
        _log?.LogInformation("{Message}", stage + " started.");
        try
        {
            // Keep SDK initialization off the input notification thread.
            await Task.Run(
                () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _io.Initialize();
                    cancellationToken.ThrowIfCancellationRequested();
                    stage = "Control I/O readiness check";
                    _io.CheckReady();
                },
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            stage = "Stopping run outputs after I/O initialization";
            StopRunOutputs();
            stage = "Setting conveyor normal speed outputs";
            _io.SetOutput(OutputIo.MainConveyorNormalSpeed, true);
            _io.SetOutput(OutputIo.NgConveyorNormalSpeed, true);
            stage = "Setting placement handler rotation OFF";
            _io.SetOutput(OutputIo.PcbPlacementHandlerRotate, false);
            stage = "Setting main conveyor forward direction";
            _io.SetOutput(OutputIo.MainConveyorForward, true);
            _log?.LogInformation("Control I/O initialization and readiness check completed.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _log?.LogError("{Message}", $"{stage} failed. {exception.Message}");
            return (MachineAlarm.IoCommunication, exception);
        }

        return (MachineAlarm.None, null);
    }

    private async Task<(MachineAlarm Alarm, Exception? Error)> InitializeHardwareAsync(
        CancellationToken cancellationToken)
    {
        var ioResult = await InitializeIoAsync(cancellationToken);
        // Input feedback must keep updating during the remaining device initialization.
        await _feedback.StartAsync();
        if (ioResult.Alarm != MachineAlarm.None)
        {
            return ioResult;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var stage = "Motion initialization";
        try
        {
            if (_units.PcbSupply)
            {
                stage = "PCB supply motion initialization";
                _log?.LogInformation("{Message}", stage + " started.");
                _motions[MotionGroup.PcbSupply].Initialize();
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (_units.PcbPlacement)
            {
                stage = "PCB placement motion initialization";
                _log?.LogInformation("{Message}", stage + " started.");
                _motions[MotionGroup.PcbPlacementHandler].Initialize();
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (_units.BoltFastening)
            {
                stage = "Bolt fastening motion initialization";
                _log?.LogInformation("{Message}", stage + " started.");
                _motions[MotionGroup.BoltFastening].Initialize();
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (InspectionGantryEnabled)
            {
                stage = "Inspection motion initialization";
                _log?.LogInformation("{Message}", stage + " started.");
                _motions[MotionGroup.InspectionGantry].Initialize();
                cancellationToken.ThrowIfCancellationRequested();
            }

            _log?.LogInformation("Motion initialization completed.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _log?.LogError("{Message}", $"{stage} failed. {exception.Message}");
            return (MachineAlarm.MotionUnavailable, exception);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (_units.Inspection)
        {
            try
            {
                _log?.LogInformation("Vision / lighting initialization started.");
                await _inspectionStation.InitializeVisionAsync(cancellationToken);
                _log?.LogInformation("Vision / lighting initialization completed.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _log?.LogError("{Message}", $"Vision / lighting initialization failed. {exception.Message}");
                return (MachineAlarm.Inspection, exception);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (_units.BoltFastening)
        {
            try
            {
                _log?.LogInformation("Bolt controller readiness check started.");
                await _fasteningStation.CheckReadyAsync(cancellationToken);
                _log?.LogInformation("Bolt controller readiness check completed.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _log?.LogError("{Message}", $"Bolt controller readiness check failed. {exception.Message}");
                return (MachineAlarm.BoltFastening, exception);
            }
        }

        return (MachineAlarm.None, null);
    }
}
