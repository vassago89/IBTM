using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;
using Microsoft.Extensions.Logging;

namespace IBTM;

public sealed partial class MachineController
{
    private readonly Lock _resetGate;
    private Task _resetTask = Task.CompletedTask;

    public bool IsResetAllowed => IsResetAllowedFor(_state.IsRunning);

    private bool IsResetAllowedFor(bool running)
    {
        switch (true)
        {
            case true when _operations.IsShuttingDown
                || _feedback.Failure is not null
                || running:
                return false;
            case true when _state.Alarm == MachineAlarm.IoCommunication:
                return true;
            case true when !_state.SafetyReady || !(_state.ManualMode || _state.DoorInterlockReady):
                return false;
            // Hardware recovery admission, not permission to acknowledge the buzzer.
            // A failed feedback scan must leave recovery usable without another native read.
            case true when _state.IsError
                || _feedback.ReadError is not null:
                return true;
        }
        var motion = _state.FeedbackReadiness;
        return motion.Faulted || !motion.ServosOn || !_state.ServoMainContactorOn;
    }

    public async Task ResetAsync()
    {
        await Task.Run(AcknowledgeAndResetAsync);
    }

    private Task AcknowledgeAndResetAsync()
    {
        SilenceBuzzer();
        lock (_resetGate)
        {
            // Repeated clicks acknowledge the buzzer, but share the current recovery.
            switch (true)
            {
                case true when !_resetTask.IsCompleted:
                    return _resetTask;
                case true when _state.IsError && _operations.HasActiveOperations
                    && !_operations.IsShuttingDown && _feedback.Failure is null:
                    return _resetTask = ResetHardwareAsync();
                case true when !IsResetAllowed:
                    _log?.LogInformation("Machine RESET: buzzer silenced; hardware recovery conditions are not satisfied.");
                    return Task.CompletedTask;
                default:
                    return _resetTask = ResetHardwareAsync();
            }
        }
    }

    private async Task ResetHardwareAsync()
    {
        if (_state.IsError && _operations.HasActiveOperations)
        {
            _log?.LogInformation("Machine RESET: waiting for stopped operation cleanup to finish.");
            await _operations.WaitForIdleAsync();
        }
        try
        {
            // Button availability uses acquired feedback; RESET must recheck the run outputs.
            // Disconnected I/O is initialized below without trying to read it first.
            if (!IsResetAllowedFor(_state.IsRunningFor()))
            {
                _log?.LogInformation("Machine RESET: hardware recovery conditions are not satisfied after the stop check.");
                return;
            }
        }
        catch (IOException exception)
        {
            StopAndReportFailure(MachineAlarm.IoCommunication, exception);
            return;
        }

        _log?.LogInformation("Machine RESET started.");
        using var operation = _operations.TryBegin();
        if (operation is null)
            return;
        var (alarm, error) = await InitializeIoAsync(operation.Token);
        operation.Token.ThrowIfCancellationRequested();
        switch (true)
        {
            case true when alarm != MachineAlarm.None:
                _state.SetError(alarm, error);
                return;
            case true when !_state.SafetyReady || !_state.ManualMode && !_state.DoorInterlockReady:
                return;
        }

        var failures = new List<Exception>();
        void RecordFailure(MachineAlarm deviceAlarm, string device, Exception exception)
        {
            _log?.LogError(exception, "{Message}", $"{device} reset failed.");
            if (alarm == MachineAlarm.None)
            {
                alarm = deviceAlarm;
            }

            failures.Add(exception);
        }

        foreach (var group in Enum.GetValues<MotionGroup>())
        {
            operation.Token.ThrowIfCancellationRequested();
            if (!_units.IsMotionEnabled(group))
            {
                continue;
            }

            try
            {
                switch (group)
                {
                    case MotionGroup.PcbSupply:
                        _pcbSupply.InitializeMotion();
                        operation.Token.ThrowIfCancellationRequested();
                        await _pcbSupply.ResetMotionAsync(operation.Token);
                        break;
                    case MotionGroup.PcbPlacementHandler:
                        _pcbPlacement.InitializeMotion();
                        operation.Token.ThrowIfCancellationRequested();
                        await _pcbPlacement.ResetMotionAsync(operation.Token);
                        break;
                    case MotionGroup.BoltFastening:
                        _fasteningStation.InitializeMotion();
                        operation.Token.ThrowIfCancellationRequested();
                        await _fasteningStation.ResetMotionAsync(operation.Token);
                        break;
                    case MotionGroup.InspectionGantry:
                        _inspectionStation.InitializeMotion();
                        operation.Token.ThrowIfCancellationRequested();
                        await _inspectionStation.ResetMotionAsync(operation.Token);
                        break;
                }
            }
            catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                RecordFailure(MachineAlarm.MotionUnavailable, group.ToString(), exception);
            }
        }

        operation.Token.ThrowIfCancellationRequested();
        if (_units.Inspection)
        {
            try
            {
                await _inspectionStation.InitializeVisionAsync(operation.Token);
            }
            catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                RecordFailure(MachineAlarm.Inspection, "Vision / lighting", exception);
            }
        }

        operation.Token.ThrowIfCancellationRequested();
        if (_units.BoltFastening)
        {
            try
            {
                await _fasteningStation.ResetHeadsAsync(operation.Token);
            }
            catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                RecordFailure(MachineAlarm.BoltFastening, "Bolt controllers", exception);
            }
        }

        operation.Token.ThrowIfCancellationRequested();
        try
        {
            var motion = _feedback.ReadLiveReadiness();
            if (motion.Faulted || !motion.ServosOn)
                throw new InvalidOperationException(
                    $"Motion reset is not confirmed by hardware feedback: faulted={motion.Faulted}, servosOn={motion.ServosOn}.");
        }
        catch (Exception exception)
        {
            RecordFailure(MachineAlarm.MotionUnavailable, "Motion feedback", exception);
        }
        if (failures.Count > 0)
        {
            _state.SetError(alarm, failures.Count == 1 ? failures[0] : new AggregateException(failures));
            return;
        }

        _state.ClearError();
        _state.Refresh();
        _log?.LogInformation("Machine RESET completed.");
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
                _pcbSupply.InitializeMotion();
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (_units.PcbPlacement)
            {
                stage = "PCB placement motion initialization";
                _log?.LogInformation("{Message}", stage + " started.");
                _pcbPlacement.InitializeMotion();
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (_units.BoltFastening)
            {
                stage = "Bolt fastening motion initialization";
                _log?.LogInformation("{Message}", stage + " started.");
                _fasteningStation.InitializeMotion();
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (InspectionGantryEnabled)
            {
                stage = "Inspection motion initialization";
                _log?.LogInformation("{Message}", stage + " started.");
                _inspectionStation.InitializeMotion();
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
