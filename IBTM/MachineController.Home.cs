using System;
using System.Collections.Generic;
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

    private bool IsHomeAllowed(MotionReadiness motion, bool? running = null)
    {
        return !_operations.IsShuttingDown
            && _state.SafetyReady
            && motion.ServosOn
            && !motion.Faulted
            && _state.ServoMainContactorOn
            && !motion.Homed
            && !_state.IsError
            && !(running ?? _state.IsRunning)
            && HomeBlock == HomeBlockReason.None
            && (!_units.PcbSupply || _pcbSupply.CanHome);
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

    private bool IsCylinderRaiseClear()
    {
        // Keep the IPM down while Placement holds a PCB.
        return !Array.Exists(CarrierInputs, _io.GetInput)
            && (!BufferHandlersEnabled || !_io.GetInput(InputIo.PcbPlacementPcbDetected));
    }

    internal HomeBlockReason GetHomeBlock(MotionGroup? group = null)
    {
        if (!_io.IsReady)
            return HomeBlockReason.IoUnavailable;
        if (group is { } motionGroup && !_units.IsMotionEnabled(motionGroup))
            return HomeBlockReason.UnitDisabled;
        if (!_state.ManualMode && !_state.DoorInterlockReady)
            return HomeBlockReason.DoorOpen;
        if (Array.Exists(CarrierInputs, _io.GetInput))
            return HomeBlockReason.CarrierDetected;

        if ((group is MotionGroup.PcbSupply or MotionGroup.PcbPlacementHandler
            || group is null
            && BufferHandlersEnabled)
            && (!_placementHandler.CanMoveHorizontal
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
            && (group != MotionGroup.PcbPlacementHandler || !_state.Buffer.IsSupplyInside(live))
            && HomeAxisConditionsReady(group, axis, live);
    }

    private bool HomeAxisConditionsReady(MotionGroup group, MotionAxis axis, bool live = true)
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

    internal Task HomeAxisAsync(MotionGroup group, MotionAxis axis, CancellationToken cancellationToken)
    {
        return RunManualAsync(
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
            canContinue: () => HomeAxisConditionsReady(group, axis));
    }

    internal bool CanHomeUnit(MotionGroup group, bool live = true)
    {
        return _state.GetMotionStatus(group).Feedback.Axes
            .All(axis => CanHomeAxis(group, axis, live));
    }

    internal Task HomeUnitAsync(MotionGroup group, CancellationToken cancellationToken)
    {
        return RunManualAsync(
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
                            {
                                await _placementHandler.MoveToHorizontalZAsync(token);
                                homed = await _placementHandler.HomeHorizontalAsync(token);
                            }
                            break;

                        case MotionGroup.BoltFastening:
                            homed = await _fasteningGantry.HomeAxisAsync(MotionAxis.Z, token);
                            if (homed)
                            {
                                await _fasteningGantry.MoveToSafeZAsync(token);
                                homed = await _fasteningGantry.HomeHorizontalAsync(token);
                            }
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
                .All(axis => HomeAxisConditionsReady(group, axis)));
    }

    public async Task RaiseCylindersAsync(CancellationToken cancellationToken)
    {
        if (!CanRaiseCylinders)
            return;

        using var operation = _operations.TryBegin(cancellationToken);
        if (operation is null)
            return;
        void StopWhenUnavailable()
        {
            if (!_state.ManualMode
                || !_state.SafetyReady
                || !_io.IsReady
                || !IsCylinderRaiseClear())
                operation.Cancel();
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
        if (!CanHome)
        {
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

            if (!_io.IsReady
                || _state.IsError
                || !_state.SafetyReady
                || HomeBlock != HomeBlockReason.None)
            {
                operation.Cancel();
                return;
            }

            bool hardwareAvailable;
            try
            {
                var motion = _state.MotionReadiness;
                hardwareAvailable = !motion.Faulted && motion.ServosOn && _state.ServoMainContactorOn;
            }
            catch (Exception exception)
            {
                operation.Cancel();
                _state.SetError(MachineAlarm.MotionUnavailable, exception);
                return;
            }

            if (!hardwareAvailable)
            {
                operation.Cancel();
                _state.SetError(MachineAlarm.MotionUnavailable);
            }
            else if (_state.IsError || HomeBlock != HomeBlockReason.None)
            {
                operation.Cancel();
            }
        }

        async Task RunHomeStepAsync(Task step)
        {
            try
            {
                await step;
            }
            catch (Exception exception)
            {
                if (exception is not OperationCanceledException
                    && !cancellationToken.IsCancellationRequested)
                {
                    _state.SetError(MachineAlarm.HomeFailed, exception);
                }

                throw;
            }
        }

        async Task CheckHomeAsync(Task<bool> homing)
        {
            await RunHomeStepAsync(homing);
            if (!await homing)
            {
                _state.SetError(MachineAlarm.HomeFailed);
            }
        }

        _state.Changed += StopWhenHomeBecomesUnavailable;
        try
        {
            _state.SetHoming(true);
            var zHomeTasks = new List<Task>(3);
            if (_units.PcbPlacement)
            {
                zHomeTasks.Add(
                    CheckHomeAsync(_placementHandler.HomeAxisAsync(MotionAxis.Z, cancellationToken)));
            }

            if (_units.PcbSupply)
            {
                zHomeTasks.Add(CheckHomeAsync(_supplyHandler.PrepareHomeAsync(cancellationToken)));
            }

            if (_units.BoltFastening)
            {
                zHomeTasks.Add(
                    CheckHomeAsync(_fasteningGantry.HomeAxisAsync(MotionAxis.Z, cancellationToken)));
            }

            await Task.WhenAll(zHomeTasks);
            cancellationToken.ThrowIfCancellationRequested();

            var safeZTasks = new List<Task>(2);
            if (_units.PcbPlacement)
            {
                safeZTasks.Add(
                    RunHomeStepAsync(_placementHandler.MoveToHorizontalZAsync(cancellationToken)));
            }

            if (_units.BoltFastening)
            {
                safeZTasks.Add(RunHomeStepAsync(_fasteningGantry.MoveToSafeZAsync(cancellationToken)));
            }

            await Task.WhenAll(safeZTasks);

            var horizontalHomeTasks = new List<Task>(4);
            if (_units.PcbPlacement)
            {
                horizontalHomeTasks.Add(
                    CheckHomeAsync(_placementHandler.HomeHorizontalAsync(cancellationToken)));
            }

            if (_units.PcbSupply)
            {
                horizontalHomeTasks.Add(
                    CheckHomeAsync(_supplyHandler.CompleteHomeAsync(cancellationToken)));
            }

            if (_units.BoltFastening)
            {
                horizontalHomeTasks.Add(
                    CheckHomeAsync(_fasteningGantry.HomeHorizontalAsync(cancellationToken)));
            }

            if (InspectionGantryEnabled)
            {
                horizontalHomeTasks.Add(
                    CheckHomeAsync(_inspectionGantry.HomeHorizontalAsync(cancellationToken)));
            }

            await Task.WhenAll(horizontalHomeTasks);
            cancellationToken.ThrowIfCancellationRequested();

            if (_units.PcbSupply)
            {
                await _supplyHandler.MoveToRotationZAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _state.SetError(MachineAlarm.HomeFailed, exception);
        }
        finally
        {
            _state.Changed -= StopWhenHomeBecomesUnavailable;
            _state.SetHoming(false);
            _state.Refresh();
        }
    }
}
