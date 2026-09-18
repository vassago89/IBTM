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
    public bool CanTestBoltHead
    {
        get
        {
            return _state.ManualSetupEnabled
                && !_fasteningGantry.HasPendingResult
                && !_fasteningStation.HasPendingResult;
        }
    }

    public bool CanUseAdcProtocol
    {
        get
        {
            return AdcProtocolAvailable && !_state.IsRunning;
        }
    }

    public bool AdcProtocolAvailable
    {
        get
        {
            return !_operations.IsShuttingDown && _state.ManualMode && _state.SafetyReady;
        }
    }

    internal bool CanUseManualMotion(MotionGroup group, bool live = true)
    {
        return !(live ? _state.IsRunning : _state.Display.IsRunning)
            && ManualMotionReady(group, live);
    }

    private bool ManualMotionReady(MotionGroup group, bool live = true)
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

    internal Task RunManualMotionAsync(
        MotionGroup group,
        Func<CancellationToken, Task> move,
        CancellationToken cancellationToken,
        CancellationToken viewCancellation)
    {
        return RunManualAsync(
            move,
            group switch
            {
                MotionGroup.PcbSupply => MachineAlarm.PcbSupply,
                MotionGroup.PcbPlacementHandler => MachineAlarm.PcbPlacement,
                MotionGroup.BoltFastening => MachineAlarm.BoltFastening,
                MotionGroup.InspectionGantry => _units.Inspection
                    ? MachineAlarm.Inspection
                    : MachineAlarm.NgCarrierTransfer,
                _ => throw new ArgumentOutOfRangeException(nameof(group)),
            },
            () => CanUseManualMotion(group),
            cancellationToken,
            viewCancellation,
            canContinue: () => ManualMotionReady(group));
    }

    internal Task RunTeachingEditAsync(
        Func<CancellationToken, Task> edit,
        CancellationToken cancellationToken,
        CancellationToken viewCancellation)
    {
        return RunManualAsync(
            edit,
            MachineAlarm.IoCommunication,
            () => _state.SetupEditingEnabled,
            cancellationToken,
            viewCancellation,
            canContinue: () => _state.ManualMode);
    }

    private async Task RunManualAsync(
        Func<CancellationToken, Task> execute,
        MachineAlarm alarm,
        Func<bool> canStart,
        CancellationToken cancellationToken,
        CancellationToken viewCancellation = default,
        Func<bool>? canContinue = null)
    {
        var activeCancellation = cancellationToken;
        try
        {
            if (!canStart())
                return;
            using var operation = _operations.TryBegin(cancellationToken, viewCancellation);
            if (operation is null)
                return;
            activeCancellation = operation.Token;
            void StopWhenUnavailable()
            {
                if (operation.IsCancellationRequested)
                    return;
                if (!(canContinue?.Invoke() ?? (_io.IsReady && _state.ManualMode && _state.SafetyReady)))
                    operation.Cancel();
            }

            _state.Changed += StopWhenUnavailable;
            try
            {
                StopWhenUnavailable();
                operation.Token.ThrowIfCancellationRequested();
                await execute(operation.Token);
            }
            finally
            {
                _state.Changed -= StopWhenUnavailable;
            }
        }
        catch (OperationCanceledException) when (activeCancellation.IsCancellationRequested
            || viewCancellation.IsCancellationRequested
            || _operations.IsShuttingDown)
        {
        }
        catch (IoTimeoutException exception)
        {
            _state.SetError(_state.IsError ? _state.Alarm : alarm, exception);
        }
        catch (Exception exception) when (exception is IOException or MotionException or MotionInterlockException
            || exception is AggregateException aggregate
                && aggregate.Flatten().InnerExceptions.Any(
                    error => error is IOException or MotionException or MotionInterlockException or IoTimeoutException))
        {
            StopAndReportFailure(
                _state.IsError
                    ? _state.Alarm
                    : IsMotionFailure(exception) && alarm != MachineAlarm.HomeFailed
                        ? MachineAlarm.MotionUnavailable
                        : alarm,
                exception);
        }
    }

    internal bool CanSetServo(MotionGroup group, bool live = true)
    {
        return _units.IsMotionEnabled(group)
            && (live
                ? _state.ManualMode && _state.SafetyReady && !_state.IsRunning
                : _state.Display.Available
                    && !_state.Display.AutoMode
                    && _state.Display.SafetyReady
                    && !_state.Display.IsRunning)
            && (live
                ? _state.GetMotionStatus(group).Feedback.IsReady
                : _state.GetMotionStatus(group).Axes.Values.All(axis => axis.State is not null));
    }

    internal void ToggleServo(MotionGroup group, MotionAxis axis)
    {
        try
        {
            if (!CanSetServo(group))
                return;
            using var operation = _operations.TryBegin();
            if (operation is null)
                return;
            var on = !_state.GetMotionStatus(group).Feedback.GetAxisState(axis).ServoOn;
            switch (group)
            {
                case MotionGroup.PcbSupply:
                    _supplyHandler.SetServo(axis, on);
                    break;
                case MotionGroup.PcbPlacementHandler:
                    _placementHandler.SetServo(axis, on);
                    break;
                case MotionGroup.BoltFastening:
                    _fasteningGantry.SetServo(axis, on);
                    break;
                case MotionGroup.InspectionGantry:
                    _inspectionGantry.SetServo(axis, on);
                    break;
            }
        }
        catch (Exception exception)
        {
            _state.SetError(_state.IsError ? _state.Alarm : MachineAlarm.MotionUnavailable, exception);
            _operations.Cancel();
        }
    }

    public async Task RunAdcProtocolAsync(
        Func<CancellationToken, Task> command,
        CancellationToken cancellationToken)
    {
        if (!CanUseAdcProtocol)
        {
            throw new InvalidOperationException("ADC diagnostics require an idle machine in manual mode.");
        }

        using var operation = _operations.TryBegin(cancellationToken);
        if (operation is null)
            throw new InvalidOperationException("Another machine operation acquired control before ADC diagnostics started.");
        void StopWhenUnavailable()
        {
            if (!AdcProtocolAvailable)
                operation.Cancel();
        }

        _state.Changed += StopWhenUnavailable;
        try
        {
            StopWhenUnavailable();
            operation.Token.ThrowIfCancellationRequested();
            await command(operation.Token);
        }
        finally
        {
            _state.Changed -= StopWhenUnavailable;
        }
    }

    internal async Task RunBoltTestAsync(
        Func<CancellationToken, Task> test,
        CancellationToken cancellationToken)
    {
        // RunAdcProtocolAsync owns admission and cancellation for this command.
        if (!AdcProtocolAvailable)
        {
            throw new InvalidOperationException("Bolt testing requires safe manual mode.");
        }

        if (_fasteningGantry.HasPendingResult || _fasteningStation.HasPendingResult)
        {
            throw new InvalidOperationException(
                "A production fastening result is still pending. Resolve that result before testing a bolt head.");
        }

        try
        {
            _state.SetBoltTestRunning(true);
            await test(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _state.SetError(_state.IsError ? _state.Alarm : MachineAlarm.BoltFastening, exception);
            throw;
        }
        finally
        {
            _state.SetBoltTestRunning(false);
        }
    }
}
