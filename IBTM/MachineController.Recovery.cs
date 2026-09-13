using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;

namespace IBTM;

public sealed partial class MachineController
{
    private readonly Lock _resetGate = new();
    private Task _resetTask = Task.CompletedTask;

    public bool CanReset
    {
        get
        {
            if (_operations.IsShuttingDown
                || _feedback.Failure is not null
                || _state.IsRunning)
            {
                return false;
            }

            if (_state.Alarm == MachineAlarm.IoCommunication)
            {
                return true;
            }

            if (!_state.SafetyReady || !(_state.ManualMode || _state.DoorInterlockReady))
            {
                return false;
            }

            // Hardware recovery admission, not permission to acknowledge the buzzer.
            // A failed feedback scan must leave recovery usable without another native read.
            if (_state.IsError || _feedback.ReadError is not null)
                return true;
            var motion = _state.FeedbackReadiness;
            return motion.Faulted || !motion.ServosOn || !_state.ServoMainContactorOn;
        }
    }

    public Task ResetAsync()
    {
        _state.SilenceBuzzer();
        lock (_resetGate)
        {
            // Repeated clicks acknowledge the buzzer, but share the current recovery.
            if (!_resetTask.IsCompleted)
                return _resetTask;
            if (!CanReset)
            {
                _log?.Write("Machine RESET: buzzer silenced; hardware recovery conditions are not satisfied.");
                return Task.CompletedTask;
            }

            return _resetTask = ResetHardwareAsync();
        }
    }

    private async Task ResetHardwareAsync()
    {
        _log?.Write("Machine RESET started.");
        using var operation = _operations.TryBegin();
        if (operation is null)
            return;
        var (alarm, error) = await InitializeIoAsync(operation.Token);
        operation.Token.ThrowIfCancellationRequested();
        if (alarm != MachineAlarm.None)
        {
            _state.SetError(alarm, error);
            return;
        }

        if (!_state.SafetyReady || !_state.ManualMode && !_state.DoorInterlockReady)
        {
            return;
        }

        var failures = new List<Exception>();
        void RecordFailure(MachineAlarm deviceAlarm, string device, Exception exception)
        {
            _log?.Error($"{device} reset failed.", exception);
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
                        _supplyHandler.InitializeMotion();
                        operation.Token.ThrowIfCancellationRequested();
                        _supplyHandler.ResetMotion();
                        break;
                    case MotionGroup.PcbPlacementHandler:
                        _placementHandler.InitializeMotion();
                        operation.Token.ThrowIfCancellationRequested();
                        _placementHandler.ResetMotion();
                        break;
                    case MotionGroup.BoltFastening:
                        _fasteningGantry.InitializeMotion();
                        operation.Token.ThrowIfCancellationRequested();
                        _fasteningGantry.ResetMotion();
                        break;
                    case MotionGroup.InspectionGantry:
                        _inspectionGantry.InitializeMotion();
                        operation.Token.ThrowIfCancellationRequested();
                        _inspectionGantry.ResetMotion();
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
                await _boltInspector.InitializeVisionAsync(operation.Token);
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
                await _fasteningGantry.ResetHeadsAsync(operation.Token);
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
        if (failures.Count > 0)
        {
            _state.SetError(alarm, failures.Count == 1 ? failures[0] : new AggregateException(failures));
            return;
        }

        _state.ClearError();
        _state.Refresh();
    }

    private async Task<(MachineAlarm Alarm, Exception? Error)> InitializeIoAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stage = "Control I/O initialization";
        _log?.Write(stage + " started.");
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
            stage = "Setting main conveyor forward direction";
            _io.SetOutput(OutputIo.MainConveyorForward, true);
            _log?.Write("Control I/O initialization and readiness check completed.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _log?.Error($"{stage} failed. {exception.Message}");
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
                _log?.Write(stage + " started.");
                _supplyHandler.InitializeMotion();
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (_units.PcbPlacement)
            {
                stage = "PCB placement motion initialization";
                _log?.Write(stage + " started.");
                _placementHandler.InitializeMotion();
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (_units.BoltFastening)
            {
                stage = "Bolt fastening motion initialization";
                _log?.Write(stage + " started.");
                _fasteningGantry.InitializeMotion();
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (InspectionGantryEnabled)
            {
                stage = "Inspection motion initialization";
                _log?.Write(stage + " started.");
                _inspectionGantry.InitializeMotion();
                cancellationToken.ThrowIfCancellationRequested();
            }

            _log?.Write("Motion initialization completed.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _log?.Error($"{stage} failed. {exception.Message}");
            return (MachineAlarm.MotionUnavailable, exception);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (_units.Inspection)
        {
            try
            {
                _log?.Write("Vision / lighting initialization started.");
                await _boltInspector.InitializeVisionAsync(cancellationToken);
                _log?.Write("Vision / lighting initialization completed.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _log?.Error($"Vision / lighting initialization failed. {exception.Message}");
                return (MachineAlarm.Inspection, exception);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (_units.BoltFastening)
        {
            try
            {
                _log?.Write("Bolt controller readiness check started.");
                await _fasteningGantry.CheckReadyAsync(cancellationToken);
                _log?.Write("Bolt controller readiness check completed.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _log?.Error($"Bolt controller readiness check failed. {exception.Message}");
                return (MachineAlarm.BoltFastening, exception);
            }
        }

        return (MachineAlarm.None, null);
    }
}
