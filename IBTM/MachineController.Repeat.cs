using System;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;
using IBTM.Inspection;

namespace IBTM;

public enum RepeatPhase
{
    [Description("Forward repeat transfer")]
    Automatic,
    [Description("NG end → Shuttle")]
    ReturnToShuttle,
    [Description("Shuttle → Station 3")]
    ReturnToStation3,
    [Description("Returning to entry sensor")]
    ReturnToStart,
    [Description("Shuttle down → up")]
    CycleShuttle,
    [Description("Station 3 → First inspection FOV")]
    ClearStation3,
}

public sealed partial class MachineController
{
    // An unfinished route destination survives STOP; carrier position still comes from I/O.
    private volatile RepeatPhase _repeatPhase;
    private int _repeatCycles;

    private async Task RunRepeatAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_repeatPhase == RepeatPhase.Automatic)
            {
                var carriers = _conveyor.CarrierCount
                    + (_units.NgShuttle && _ngShuttle.Feedback.CarrierDetected ? 1 : 0)
                    + (_units.NgConveyor && _ngConveyor.Position1Occupied ? 1 : 0)
                    + (_units.NgConveyor && _ngConveyor.Position2Occupied ? 1 : 0);
                if (carriers == 0 && _ngTransfer.CarrierDetected)
                    carriers = 1;
                if (carriers != 1 || _conveyor.ExitCarrierDetected)
                    throw new InvalidOperationException("Repeat requires one carrier inside the machine, with known presence feedback.");
            }

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                switch (_repeatPhase)
                {
                    case RepeatPhase.Automatic:
                        await RunToRepeatEndAsync(cancellationToken);
                        SetRepeatPhase(_units.NgConveyor
                            ? RepeatPhase.ReturnToShuttle
                            : _units.NgShuttle
                                ? RepeatPhase.CycleShuttle
                                : RepeatPhase.ReturnToStation3);
                        break;

                    case RepeatPhase.ReturnToShuttle:
                        await ReturnNgCarrierAsync(cancellationToken);
                        SetRepeatPhase(RepeatPhase.ReturnToStation3);
                        break;

                    case RepeatPhase.ReturnToStation3:
                        await _ngMove.ReturnToStationAsync(cancellationToken);
                        SetRepeatPhase(RepeatPhase.ClearStation3);
                        break;

                    case RepeatPhase.ClearStation3:
                        await _ngMove.ClearStationAsync(
                            _recipe.CarrierImages.MinBy(image => image.Number)?.Center,
                            cancellationToken);
                        SetRepeatPhase(RepeatPhase.ReturnToStart);
                        break;

                    case RepeatPhase.CycleShuttle:
                        await _ngShuttle.CycleAsync(cancellationToken);
                        SetRepeatPhase(RepeatPhase.ReturnToStation3);
                        break;

                    case RepeatPhase.ReturnToStart:
                        await ReturnMainCarrierAsync(cancellationToken);
                        cancellationToken.ThrowIfCancellationRequested();
                        _repeatCycles++;
                        _log?.Write($"Repeat cycle {_repeatCycles} returned to the entry sensor.");
                        SetRepeatPhase(RepeatPhase.Automatic);
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var alarm = IsMotionFailure(exception)
                ? MachineAlarm.MotionUnavailable
                : _repeatPhase switch
                {
                    RepeatPhase.ReturnToShuttle => MachineAlarm.NgConveyor,
                    RepeatPhase.CycleShuttle => MachineAlarm.NgShuttle,
                    RepeatPhase.ReturnToStation3 or RepeatPhase.ClearStation3 => MachineAlarm.NgCarrierTransfer,
                    _ => MachineAlarm.MainConveyor,
                };
            _state.SetError(_state.IsError ? _state.Alarm : alarm, exception);
        }
    }

    private async Task RunToRepeatEndAsync(CancellationToken cancellationToken)
    {
        using var cycle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var transferChanged = new AsyncAutoResetEvent();
        if (!_units.NgConveyor)
            _ngMove.Changed += transferChanged.Set;
        var automatic = RunAutomaticUnitsAsync(cycle, repeat: true);
        try
        {
            if (_units.NgConveyor)
            {
                await _io.WaitForInputAsync(
                    InputIo.NgConveyorPosition1Occupied, true, Timeout.Infinite, cycle.Token);
            }
            else
            {
                var holdAtShuttle = !_units.NgShuttle;
                var endState = holdAtShuttle
                    ? NgTransferState.HoldingAtDestination
                    : NgTransferState.Completed;
                while (_ngMove.State(
                    NgTransferDestination.Shuttle,
                    canPickUp: true,
                    holdAtDestination: holdAtShuttle) != endState)
                {
                    await transferChanged.WaitAsync(cycle.Token);
                }
            }
        }
        finally
        {
            _ngMove.Changed -= transferChanged.Set;
            cycle.Cancel();
            // Reverse begins only after every forward unit has released its commands.
            await automatic;
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task ReturnNgCarrierAsync(CancellationToken cancellationToken)
    {
        await _ngTransfer.SetLiftUpAsync(true, cancellationToken);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        void CheckPickup()
        {
            if (!_ngTransfer.IsRaised)
                operation.Cancel();
        }

        _ngTransfer.Changed += CheckPickup;
        Exception? failure = null;
        try
        {
            CheckPickup();
            operation.Token.ThrowIfCancellationRequested();
            if (!_ngShuttle.Feedback.CarrierDetected)
                await _ngShuttle.SetUpAsync(false, operation.Token);
            await _ngConveyor.ReturnToShuttleAsync(operation.Token);
            await _ngShuttle.SetUpAsync(true, operation.Token);
            operation.Token.ThrowIfCancellationRequested();
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            _ngTransfer.Changed -= CheckPickup;
            try
            {
                _ngConveyor.Stop();
            }
            catch (Exception cleanupFailure) when (failure is not null)
            {
                throw new AggregateException(failure, cleanupFailure);
            }
        }
    }

    private OutputBlockReason GetMainConveyorReturnBlock()
    {
        if (_units.PcbPlacement)
        {
            if (!_placementHandler.CanMoveHorizontal)
                return OutputBlockReason.PlacementNotRaised;
            if (!_placementHandler.IsAtHorizontalZ())
                return OutputBlockReason.PlacementNotAtSafeZ;
        }

        if (_units.BoltFastening)
        {
            if (!_fasteningGantry.CanMoveHorizontal)
                return OutputBlockReason.FasteningNotRaised;
            if (!_fasteningGantry.IsAtSafeZ())
                return OutputBlockReason.FasteningNotAtSafeZ;
        }

        if (_units.Inspection || _units.NgCarrierTransfer)
        {
            if (!_ngTransfer.IsRaised)
                return OutputBlockReason.NgPickupNotRaised;
            if (_ngTransfer.CarrierDetected)
                return OutputBlockReason.NgCarrierDetected;
        }

        return OutputBlockReason.None;
    }

    private async Task ReturnMainCarrierAsync(CancellationToken cancellationToken)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        void CheckPath()
        {
            if (GetMainConveyorReturnBlock() != OutputBlockReason.None)
                operation.Cancel();
        }

        _state.Changed += CheckPath;
        _placementHandler.Changed += CheckPath;
        _fasteningGantry.Changed += CheckPath;
        _ngTransfer.Changed += CheckPath;
        try
        {
            CheckPath();
            operation.Token.ThrowIfCancellationRequested();
            await _conveyor.ReturnToStartAsync(operation.Token);
            operation.Token.ThrowIfCancellationRequested();
        }
        finally
        {
            _state.Changed -= CheckPath;
            _placementHandler.Changed -= CheckPath;
            _fasteningGantry.Changed -= CheckPath;
            _ngTransfer.Changed -= CheckPath;
        }
    }

    private void SetRepeatPhase(RepeatPhase phase)
    {
        _repeatPhase = phase;
        _state.RequestDisplayRefresh();
    }
}
