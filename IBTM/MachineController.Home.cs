using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;
using IBTM.PcbPlacement;
using Microsoft.Extensions.Logging;

namespace IBTM;

public sealed partial class MachineController
{
    public bool IsHomeAllowed => _state.Available && IsHomeAllowedFor(_state.FeedbackReadiness, _state.IsRunning);

    public HomeBlockReason HomeBlock => GetHomeBlock();

    private bool IsHomeAllowedFor(MotionReadiness motion, bool? running = null)
    {
        return !_operations.IsShuttingDown
            && _state.SafetyReady
            && motion.ServosOn
            && !motion.Faulted
            && _state.ServoMainContactorOn
            && !_state.IsError
            && !(running ?? _state.IsRunningFor())
            && HomeBlock == HomeBlockReason.None;
    }

    internal HomeBlockReason GetHomeBlock(MotionGroup? group = null, bool requireRaised = false)
    {
        switch (true)
        {
            case true when !_io.IsReady:
                return HomeBlockReason.IoUnavailable;
            case true when group is { } motionGroup && !_units.IsMotionEnabled(motionGroup):
                return HomeBlockReason.UnitDisabled;
            case true when !_state.ManualMode && !_state.DoorInterlockReady:
                return HomeBlockReason.DoorOpen;
            case true when (group is MotionGroup.PcbSupply or MotionGroup.PcbPlacementHandler
                || group is null
                && PcbHandlersEnabled)
                && _io.GetInput(InputIo.PcbPlacementPcbDetected)
                && _placementHandler.IpmLift != PlacementCylinderState.Up:
                return HomeBlockReason.PlacementHoldingPcb;
            case true when requireRaised
                && (group is MotionGroup.PcbSupply or MotionGroup.PcbPlacementHandler
                    || group is null && PcbHandlersEnabled)
                && (!_placementHandler.HandlerRaised
                    || _placementHandler.IpmLift != PlacementCylinderState.Up):
                return HomeBlockReason.PlacementNotRaised;
            case true when requireRaised
                && (group == MotionGroup.BoltFastening || group is null && _units.BoltFastening)
                && !_fasteningGantry.IsHorizontalMoveAllowed:
                return HomeBlockReason.FasteningNotRaised;
            case true when requireRaised && (group == MotionGroup.InspectionGantry
                || group is null
                && InspectionGantryEnabled)
                && !_ngTransfer.IsRaised:
                return HomeBlockReason.NgPickupNotRaised;
            default:
                return HomeBlockReason.None;
        }
    }

    private bool IsHomeAxisReady(
        MotionGroup group, MotionAxis? axis = null, bool live = true, bool requireRaised = false)
    {
        if (!_state.ManualMode
            || !_state.SafetyReady
            || !_state.ServoMainContactorOn
            || GetHomeBlock(group, requireRaised) != HomeBlockReason.None)
            return false;

        var motion = _state.GetMotionStatus(group);
        return motion.IsReady(live)
            && motion.Feedback.Axes.Where(candidate => axis is null || candidate == axis)
                .All(
                    candidate =>
                        (live ? motion.Feedback.GetAxisState(candidate) : motion.Axes[candidate].State)
                            is { ServoOn: true, Alarm: false, Emergency: false });
    }

    internal async Task HomeAsync(
        MotionGroup group,
        CancellationToken cancellationToken,
        MotionAxis? axis = null)
    {
        // Admission and HOME startup read the synchronous SDK before the first asynchronous wait.
        await Task.Run(async () =>
        {
            var activeToken = cancellationToken;
            try
            {
                if (_state.IsRunningFor())
                    return;
                var homingAxes = false;
                using var operation = BeginManualOperation(
                    () => IsHomeAxisReady(group, axis, requireRaised: homingAxes),
                    cancellationToken);
                if (operation is null)
                    return;
                activeToken = operation.Token;
                operation.Token.ThrowIfCancellationRequested();
                _state.IsHoming = true;
                try
                {
                    await RaiseCylindersAsync(operation, group);
                    homingAxes = true;
                    if (!IsHomeAxisReady(group, axis, requireRaised: true))
                        return;
                    bool homed;
                    switch (group)
                    {
                        case MotionGroup.PcbSupply:
                            homed = await _supplyHandler.HomeAxisAsync(axis ?? MotionAxis.Z, operation.Token);
                            if (homed && axis is null)
                                homed = await _supplyHandler.HomeHorizontalAsync(operation.Token);
                            break;
                        case MotionGroup.PcbPlacementHandler:
                            homed = await _placementHandler.HomeAxisAsync(axis ?? MotionAxis.Z, operation.Token);
                            if (homed && axis is null)
                                homed = await _placementHandler.HomeHorizontalAsync(operation.Token);
                            break;
                        case MotionGroup.BoltFastening:
                            homed = await _fasteningGantry.HomeAxisAsync(axis ?? MotionAxis.Z, operation.Token);
                            if (homed && axis is null)
                                homed = await _fasteningGantry.HomeHorizontalAsync(operation.Token);
                            break;
                        case MotionGroup.InspectionGantry:
                            homed = axis is { } selectedAxis
                                ? await _inspectionGantry.HomeAxisAsync(selectedAxis, operation.Token)
                                : await _inspectionGantry.HomeHorizontalAsync(operation.Token);
                            break;
                        default:
                            throw new ArgumentOutOfRangeException(nameof(group));
                    }

                    if (!homed && !operation.Token.IsCancellationRequested)
                        _state.SetError(MachineAlarm.HomeFailed);
                }
                finally
                {
                    _state.IsHoming = false;
                    _state.Refresh();
                }
            }
            catch (OperationCanceledException) when (activeToken.IsCancellationRequested
                || _operations.IsShuttingDown)
            {
            }
            catch (Exception exception)
            {
                ReportManualFailure(MachineAlarm.HomeFailed, exception);
            }
        });
    }

    private async Task RaiseCylindersAsync(
        OperationCancellation.Operation operation, MotionGroup? group = null)
    {
        async Task ObserveRaiseAsync(Task raising, MachineAlarm alarm)
        {
            try
            {
                await raising;
            }
            catch (OperationCanceledException) when (operation.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                if (!_state.IsError)
                    _state.SetError(alarm, exception);
                else
                    _log?.LogError(exception, "{Message}", $"Cylinder raise {alarm} failed while stopping; existing alarm={_state.Alarm}.");
                operation.Cancel();
            }
            operation.Token.ThrowIfCancellationRequested();
        }

        operation.Token.ThrowIfCancellationRequested();
        if (group is MotionGroup.PcbSupply or MotionGroup.PcbPlacementHandler
            || group is null && PcbHandlersEnabled)
        {
            // Nearby material may still need support. Presence conservatively inhibits IPM lifting;
            // it does not establish PCB grip or advance the automatic sequence.
            var pcbDetected = _io.GetInput(InputIo.PcbPlacementPcbDetected);
            await ObserveRaiseAsync(
                _placementHandler.SetLiftDownAsync(false, operation.Token), MachineAlarm.PcbPlacement);
            if (!pcbDetected && !_io.GetInput(InputIo.PcbPlacementPcbDetected))
                await ObserveRaiseAsync(
                    _placementHandler.SetIpmLiftDownAsync(false, operation.Token), MachineAlarm.PcbPlacement);
        }
        if (group == MotionGroup.BoltFastening || group is null && _units.BoltFastening)
            await ObserveRaiseAsync(_fasteningGantry.RaiseCylindersAsync(operation.Token), MachineAlarm.BoltFastening);
        if (group == MotionGroup.InspectionGantry || group is null && InspectionGantryEnabled)
            await ObserveRaiseAsync(
                _ngTransfer.SetLiftUpAsync(true, operation.Token), MachineAlarm.NgCarrierTransfer);
        operation.Token.ThrowIfCancellationRequested();
    }

    public async Task HomeAsync(CancellationToken cancellationToken)
    {
        await Task.Run(() => HomeAllAxesAsync(cancellationToken), cancellationToken);
    }

    private async Task HomeAllAxesAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!IsHomeAllowedFor(_state.MotionReadiness))
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
        var homingAxes = false;
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
                    || GetHomeBlock(requireRaised: homingAxes) != HomeBlockReason.None)
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
            _state.IsHoming = true;
            await RaiseCylindersAsync(operation);
            homingAxes = true;
            StopWhenHomeBecomesUnavailable();
            cancellationToken.ThrowIfCancellationRequested();
            await Task.WhenAll(
                _units.PcbPlacement
                    ? CheckHomeAsync(
                        _placementHandler.HomeAxisAsync(MotionAxis.Z, cancellationToken), cancellationToken)
                    : Task.CompletedTask,
                _units.PcbSupply
                    ? CheckHomeAsync(
                        _supplyHandler.HomeAxisAsync(MotionAxis.Z, cancellationToken), cancellationToken)
                    : Task.CompletedTask,
                _units.BoltFastening
                    ? CheckHomeAsync(
                        _fasteningGantry.HomeAxisAsync(MotionAxis.Z, cancellationToken), cancellationToken)
                    : Task.CompletedTask);
            cancellationToken.ThrowIfCancellationRequested();
            await Task.WhenAll(
                _units.PcbPlacement
                    ? CheckHomeAsync(_placementHandler.HomeHorizontalAsync(cancellationToken), cancellationToken)
                    : Task.CompletedTask,
                _units.PcbSupply
                    ? CheckHomeAsync(_supplyHandler.HomeHorizontalAsync(cancellationToken), cancellationToken)
                    : Task.CompletedTask,
                _units.BoltFastening
                    ? CheckHomeAsync(_fasteningGantry.HomeHorizontalAsync(cancellationToken), cancellationToken)
                    : Task.CompletedTask,
                InspectionGantryEnabled
                    ? CheckHomeAsync(_inspectionGantry.HomeHorizontalAsync(cancellationToken), cancellationToken)
                    : Task.CompletedTask);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _state.SetError(_state.IsError ? _state.Alarm
                : exception is IOException ? MachineAlarm.IoCommunication : MachineAlarm.HomeFailed, exception);
        }
        finally
        {
            _state.Changed -= StopWhenHomeBecomesUnavailable;
            _state.IsHoming = false;
            _state.Refresh();
        }
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
}
