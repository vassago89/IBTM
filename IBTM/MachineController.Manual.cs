using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;

namespace IBTM;

public sealed partial class MachineController
{
    public bool IsTestBoltHeadAllowed
    {
        get
        {
            return _state.ManualSetupEnabled;
        }
    }

    public bool IsUseAdcProtocolAllowed => AdcProtocolAvailable && !_state.IsRunning;

    public bool AdcProtocolAvailable => !_operations.IsShuttingDown && _state.ManualMode && _state.SafetyReady;

    internal bool IsManualMotionReady(MotionGroup group, bool live = true)
    {
        if (_operations.IsShuttingDown
            || !_io.IsReady
            || !_state.ManualMode
            || !_state.SafetyReady
            || !_units.IsMotionEnabled(group))
            return false;
        var motion = _state.GetMotionStatus(group);
        return (!live || motion.Feedback.IsReady)
            && motion.Feedback.Axes.All(
                axis =>
                    (live ? motion.Feedback.GetAxisState(axis) : motion.Axes[axis].State)
                        is { Homed: true, ServoOn: true, Alarm: false, Emergency: false });
    }

    internal MachineAlarm GetMotionAlarm(MotionGroup group)
    {
        switch (group)
        {
            case MotionGroup.PcbSupply:
                return MachineAlarm.PcbSupply;
            case MotionGroup.PcbPlacementHandler:
                return MachineAlarm.PcbPlacement;
            case MotionGroup.BoltFastening:
                return MachineAlarm.BoltFastening;
            case MotionGroup.InspectionGantry:
                return _units.Inspection
                    ? MachineAlarm.Inspection
                    : MachineAlarm.NgCarrierTransfer;
            default:
                throw new ArgumentOutOfRangeException(nameof(group));
        }
    }

    // Acquires ownership and watches availability. The caller executes the device command.
    internal OperationCancellation.Operation? BeginManualOperation(
        Func<bool> available,
        CancellationToken cancellationToken,
        CancellationToken viewCancellation = default)
    {
        var operation = _operations.TryBegin(cancellationToken, viewCancellation);
        if (operation is null)
            return null;

        void StopWhenUnavailable()
        {
            if (!operation.IsCancellationRequested && !available())
                operation.Cancel();
        }

        _state.Changed += StopWhenUnavailable;
        operation.Disposed += () => _state.Changed -= StopWhenUnavailable;
        try
        {
            StopWhenUnavailable();
            return operation;
        }
        catch
        {
            operation.Dispose();
            throw;
        }
    }

    internal static bool IsDeviceFailure(Exception exception)
    {
        return exception is IOException or MotionException or MotionInterlockException or IoTimeoutException
            || exception is AggregateException aggregate
                && aggregate.Flatten().InnerExceptions.Any(
                    error => error is IOException or MotionException or MotionInterlockException or IoTimeoutException);
    }

    internal void ReportManualFailure(MachineAlarm alarm, Exception exception)
    {
        if (_state.IsError)
            alarm = _state.Alarm;
        else if (IsMotionFailure(exception) && alarm != MachineAlarm.HomeFailed)
            alarm = MachineAlarm.MotionUnavailable;

        if (exception is IoTimeoutException)
            _state.SetError(alarm, exception);
        else
            StopAndReportFailure(alarm, exception);
    }

    internal bool IsSetServoAllowed(MotionGroup group, bool live = true)
    {
        return _units.IsMotionEnabled(group)
            && (live
                ? _state.ManualMode && _state.SafetyReady && !_state.IsRunningFor()
                : _state.Available
                    && !_state.AutoMode
                    && _state.SafetyReady
                    && !_state.IsRunning)
            && (live
                ? _state.GetMotionStatus(group).Feedback.IsReady
                : _state.GetMotionStatus(group).Axes.Values.All(axis => axis.State is not null));
    }

    internal void ToggleServo(MotionGroup group, MotionAxis axis)
    {
        try
        {
            if (!IsSetServoAllowed(group))
                return;
            using var operation = _operations.TryBegin();
            if (operation is null)
                return;
            var on = !_state.GetMotionStatus(group).Feedback.GetAxisState(axis).ServoOn;
            switch (group)
            {
                case MotionGroup.PcbSupply:
                    _pcbSupply.SetServo(axis, on);
                    break;
                case MotionGroup.PcbPlacementHandler:
                    _pcbPlacement.SetServo(axis, on);
                    break;
                case MotionGroup.BoltFastening:
                    _fasteningStation.SetServo(axis, on);
                    break;
                case MotionGroup.InspectionGantry:
                    _inspectionStation.SetServo(axis, on);
                    break;
            }
        }
        catch (Exception exception)
        {
            _state.SetError(_state.IsError ? _state.Alarm : MachineAlarm.MotionUnavailable, exception);
            _operations.Cancel();
        }
    }

    internal OperationCancellation.Operation BeginAdcProtocol(CancellationToken cancellationToken)
    {
        if (!IsUseAdcProtocolAllowed || _state.IsRunningFor())
        {
            throw new InvalidOperationException("ADC diagnostics require an idle machine in manual mode.");
        }

        var operation = _operations.TryBegin(cancellationToken);
        if (operation is null)
            throw new InvalidOperationException("Another machine operation acquired control before ADC diagnostics started.");
        return operation;
    }

    internal void EnsureBoltTestAvailable()
    {
        if (!AdcProtocolAvailable)
        {
            throw new InvalidOperationException("Bolt testing requires safe manual mode.");
        }

    }
}
