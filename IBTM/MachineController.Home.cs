using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;
using IBTM.PcbPlacement;

namespace IBTM;

public sealed partial class MachineController
{
    public bool CanHome
    {
        get
        {
            return IsHomeAllowed(_state.MotionReadiness);
        }
    }

    public HomeBlockReason HomeBlock
    {
        get
        {
            return GetHomeBlock();
        }
    }

    public bool CanRaiseCylinders
    {
        get
        {
            return _state.ManualSetupEnabled
                && (BufferHandlersEnabled || _units.BoltFastening || InspectionGantryEnabled)
                && IsCylinderRaiseClear();
        }
    }

    private bool IsHomeAllowed(MotionReadiness motion, bool? running = null)
    {
        return !_operations.IsShuttingDown
            && _state.SafetyReady
            && motion.ServosOn
            && !motion.Faulted
            && _state.ServoMainContactorOn
            && !_state.IsError
            && !(running ?? _state.IsRunning)
            && HomeBlock == HomeBlockReason.None;
    }

    private bool IsCylinderRaiseClear()
    {
        // Keep the IPM down while Placement holds a PCB.
        return !BufferHandlersEnabled || !_io.GetInput(InputIo.PcbPlacementPcbDetected);
    }

    internal HomeBlockReason GetHomeBlock(MotionGroup? group = null)
    {
        if (!_io.IsReady)
            return HomeBlockReason.IoUnavailable;
        if (group is { } motionGroup && !_units.IsMotionEnabled(motionGroup))
            return HomeBlockReason.UnitDisabled;
        if (!_state.ManualMode && !_state.DoorInterlockReady)
            return HomeBlockReason.DoorOpen;
        if ((group is MotionGroup.PcbSupply or MotionGroup.PcbPlacementHandler
            || group is null
            && BufferHandlersEnabled)
            && (!_placementHandler.HandlerRaised
                || _placementHandler.IpmLift != PlacementCylinderState.Up))
            return HomeBlockReason.PlacementNotRaised;

        if ((group == MotionGroup.BoltFastening || group is null && _units.BoltFastening)
            && !_fasteningGantry.CanMoveHorizontal)
            return HomeBlockReason.FasteningNotRaised;

        if ((group == MotionGroup.InspectionGantry
            || group is null
            && InspectionGantryEnabled)
            && !_inspectionGantry.CanMove)
            return HomeBlockReason.NgPickupNotRaised;

        return HomeBlockReason.None;
    }

    internal bool CanHomeAxis(MotionGroup group, MotionAxis axis, bool live = true, bool? running = null)
    {
        return _units.IsMotionEnabled(group)
            && group != MotionGroup.PcbSupply
            && !(running ?? (live ? _state.IsRunning : _state.Display.IsRunning))
            && AreHomeAxisConditionsReady(group, axis, live);
    }

    private bool AreHomeAxisConditionsReady(MotionGroup group, MotionAxis axis, bool live = true)
    {
        if (!_units.IsMotionEnabled(group)
            || !_state.ManualMode
            || !_state.SafetyReady
            || !_state.ServoMainContactorOn
            || GetHomeBlock(group) != HomeBlockReason.None)
            return false;

        var motion = _state.GetMotionStatus(group);
        return motion.IsReady(live)
            && motion.Feedback.Axes.Where(candidate => candidate == axis || candidate == MotionAxis.Z)
                .All(
                    candidate =>
                        (live ? motion.Feedback.GetAxisState(candidate) : motion.Axes[candidate].State)
                            is { ServoOn: true, Alarm: false, Emergency: false });
    }

    internal async Task HomeAxisAsync(MotionGroup group, MotionAxis axis, CancellationToken cancellationToken)
    {
        // Admission and HOME startup read the synchronous SDK before the first asynchronous wait.
        await Task.Run(() => RunManualAsync(
            async token =>
            {
                _state.SetHoming(true);
                try
                {
                    var homed = await (group switch
                    {
                        MotionGroup.PcbPlacementHandler => _placementHandler.HomeAxisAsync(axis, token),
                        MotionGroup.BoltFastening => _fasteningGantry.HomeAxisAsync(axis, token),
                        MotionGroup.InspectionGantry => _inspectionGantry.HomeAxisAsync(axis, token),
                        _ => throw new ArgumentOutOfRangeException(nameof(group)),
                    });
                    if (!homed && !token.IsCancellationRequested)
                        _state.SetError(MachineAlarm.HomeFailed);
                }
                finally
                {
                    _state.SetHoming(false);
                    _state.Refresh();
                }
            },
            MachineAlarm.HomeFailed,
            () => CanHomeAxis(group, axis),
            cancellationToken,
            canContinue: () => AreHomeAxisConditionsReady(group, axis)));
    }

    internal bool CanHomeUnit(MotionGroup group, bool live = true)
    {
        return _state.GetMotionStatus(group).Feedback.Axes
            .All(axis => CanHomeAxis(group, axis, live));
    }

    internal async Task HomeUnitAsync(MotionGroup group, CancellationToken cancellationToken)
    {
        await Task.Run(() => RunManualAsync(
            async token =>
            {
                _state.SetHoming(true);
                try
                {
                    bool homed;
                    switch (group)
                    {
                        case MotionGroup.PcbPlacementHandler:
                            homed = await _placementHandler.HomeAxisAsync(MotionAxis.Z, token);
                            if (homed)
                                homed = await _placementHandler.HomeHorizontalAsync(token);
                            break;

                        case MotionGroup.BoltFastening:
                            homed = await _fasteningGantry.HomeAxisAsync(MotionAxis.Z, token);
                            if (homed)
                                homed = await _fasteningGantry.HomeHorizontalAsync(token);
                            break;

                        case MotionGroup.InspectionGantry:
                            homed = await _inspectionGantry.HomeHorizontalAsync(token);
                            break;

                        default:
                            throw new ArgumentOutOfRangeException(nameof(group));
                    }

                    if (!homed && !token.IsCancellationRequested)
                        _state.SetError(MachineAlarm.HomeFailed);
                }
                finally
                {
                    _state.SetHoming(false);
                    _state.Refresh();
                }
            },
            MachineAlarm.HomeFailed,
            () => CanHomeUnit(group),
            cancellationToken,
            canContinue: () => _state.GetMotionStatus(group).Feedback.Axes
                .All(axis => AreHomeAxisConditionsReady(group, axis))));
    }

    public async Task RaiseCylindersAsync(CancellationToken cancellationToken)
    {
        await Task.Run(() => RaiseHomeCylindersAsync(cancellationToken), cancellationToken);
    }

    private async Task RaiseHomeCylindersAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!CanRaiseCylinders)
                return;
        }
        catch (IOException exception)
        {
            StopAndReportFailure(_state.IsError ? _state.Alarm : MachineAlarm.IoCommunication, exception);
            return;
        }

        using var operation = _operations.TryBegin(cancellationToken);
        if (operation is null)
            return;
        void StopWhenUnavailable()
        {
            if (operation.IsCancellationRequested)
                return;
            try
            {
                if (!_state.ManualMode
                    || !_state.SafetyReady
                    || !_io.IsReady
                    || !IsCylinderRaiseClear())
                    operation.Cancel();
            }
            catch (IOException exception)
            {
                operation.Cancel();
                _state.SetError(_state.IsError ? _state.Alarm : MachineAlarm.IoCommunication, exception);
            }
        }

        async Task RaiseAsync(Func<CancellationToken, Task> raise, MachineAlarm alarm)
        {
            try
            {
                await raise(operation.Token);
            }
            catch (OperationCanceledException) when (operation.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                if (!_state.IsError)
                    _state.SetError(alarm, exception);
                else
                    _log?.Error(
                        $"Cylinder raise {alarm} failed while stopping; existing alarm={_state.Alarm}.",
                        exception);
                operation.Cancel();
            }
        }

        _state.Changed += StopWhenUnavailable;
        try
        {
            StopWhenUnavailable();
            var tasks = new List<Task>(3);
            if (BufferHandlersEnabled)
                tasks.Add(RaiseAsync(_placementHandler.RaiseAsync, MachineAlarm.PcbPlacement));
            if (_units.BoltFastening)
                tasks.Add(RaiseAsync(_fasteningGantry.RaiseCylindersAsync, MachineAlarm.BoltFastening));
            if (InspectionGantryEnabled)
                tasks.Add(RaiseAsync(
                    token => _ngTransfer.SetLiftUpAsync(true, token),
                    MachineAlarm.NgCarrierTransfer));
            await Task.WhenAll(tasks);
        }
        finally
        {
            _state.Changed -= StopWhenUnavailable;
        }
    }

    public async Task HomeAsync(CancellationToken cancellationToken)
    {
        await Task.Run(() => HomeAllAxesAsync(cancellationToken), cancellationToken);
    }

    private async Task HomeAllAxesAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!CanHome)
                return;
        }
        catch (Exception exception) when (exception is IOException or MotionException)
        {
            StopAndReportFailure(_state.IsError ? _state.Alarm : MachineAlarm.MotionUnavailable, exception);
            return;
        }

        using var operation = _operations.TryBegin(cancellationToken);
        if (operation is null)
            return;
        cancellationToken = operation.Token;
        void StopWhenHomeBecomesUnavailable()
        {
            if (operation.IsCancellationRequested)
            {
                return;
            }

            try
            {
                if (!_io.IsReady
                    || _state.IsError
                    || !_state.SafetyReady
                    || HomeBlock != HomeBlockReason.None)
                {
                    operation.Cancel();
                    return;
                }

                var motion = _state.MotionReadiness;
                if (motion.Faulted || !motion.ServosOn || !_state.ServoMainContactorOn)
                {
                    operation.Cancel();
                    _state.SetError(_state.IsError ? _state.Alarm : MachineAlarm.MotionUnavailable);
                }
                else if (_state.IsError || HomeBlock != HomeBlockReason.None)
                {
                    operation.Cancel();
                }
            }
            catch (Exception exception)
            {
                operation.Cancel();
                _state.SetError(_state.IsError ? _state.Alarm : MachineAlarm.MotionUnavailable, exception);
            }
        }

        _state.Changed += StopWhenHomeBecomesUnavailable;
        try
        {
            _state.SetHoming(true);
            await HomeVerticalAxesAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await HomeHorizontalAxesAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _state.SetError(_state.IsError ? _state.Alarm : MachineAlarm.HomeFailed, exception);
        }
        finally
        {
            _state.Changed -= StopWhenHomeBecomesUnavailable;
            _state.SetHoming(false);
            _state.Refresh();
        }
    }

    private async Task HomeVerticalAxesAsync(CancellationToken cancellationToken)
    {
        await Task.WhenAll(
            _units.PcbPlacement
                ? CheckHomeAsync(
                    _placementHandler.HomeAxisAsync(MotionAxis.Z, cancellationToken), cancellationToken)
                : Task.CompletedTask,
            _units.PcbSupply
                ? ObserveHomeStepAsync(_supplyHandler.PrepareHomeAsync(cancellationToken))
                : Task.CompletedTask,
            _units.BoltFastening
                ? CheckHomeAsync(
                    _fasteningGantry.HomeAxisAsync(MotionAxis.Z, cancellationToken), cancellationToken)
                : Task.CompletedTask);
    }

    private async Task HomeHorizontalAxesAsync(CancellationToken cancellationToken)
    {
        await Task.WhenAll(
            _units.PcbPlacement
                ? CheckHomeAsync(_placementHandler.HomeHorizontalAsync(cancellationToken), cancellationToken)
                : Task.CompletedTask,
            _units.PcbSupply
                ? CheckHomeAsync(_supplyHandler.CompleteHomeAsync(cancellationToken), cancellationToken)
                : Task.CompletedTask,
            _units.BoltFastening
                ? CheckHomeAsync(_fasteningGantry.HomeHorizontalAsync(cancellationToken), cancellationToken)
                : Task.CompletedTask,
            InspectionGantryEnabled
                ? CheckHomeAsync(_inspectionGantry.HomeHorizontalAsync(cancellationToken), cancellationToken)
                : Task.CompletedTask);
    }

    private async Task CheckHomeAsync(Task<bool> homing, CancellationToken cancellationToken)
    {
        try
        {
            if (!await homing && !cancellationToken.IsCancellationRequested)
                _state.SetError(MachineAlarm.HomeFailed);
        }
        catch (Exception exception)
        {
            if (exception is not OperationCanceledException)
                _state.SetError(_state.IsError ? _state.Alarm : MachineAlarm.HomeFailed, exception);
            throw;
        }
    }

    private async Task ObserveHomeStepAsync(Task step)
    {
        try
        {
            await step;
        }
        catch (Exception exception)
        {
            if (exception is not OperationCanceledException)
                _state.SetError(_state.IsError ? _state.Alarm : MachineAlarm.HomeFailed, exception);
            throw;
        }
    }
}
