using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;
using Microsoft.Extensions.Logging;

namespace IBTM;

public sealed partial class MachineController
{
    // Display only. Each invocation executes a new route from its first step.
    private volatile RepeatPhase _repeatDisplayPhase;

    public RepeatPhase RepeatDisplayPhase
    {
        get => _repeatDisplayPhase;
        private set
        {
            _repeatDisplayPhase = value;
            PropertyChanged?.Invoke(this, new(nameof(RepeatDisplayPhase)));
        }
    }

    private async Task RunRepeatAsync(CancellationToken cancellationToken)
    {
        using var repeat = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cancellationToken = repeat.Token;
        var independentUnits = new List<Task>();
        try
        {
            if (_units.PcbSupply && !_units.PcbPlacement)
                independentUnits.Add(ObserveAutomaticUnitAsync(
                    MachineAlarm.PcbSupply,
                    _pcbSupply.RunAsync(_recipes.Current.PcbSupply, _pcbPlacement, repeat.Token, repeat: true),
                    repeat));
            if (_units.NgShuttle && !_units.NgCarrierTransfer)
                independentUnits.Add(ObserveAutomaticUnitAsync(
                    MachineAlarm.NgShuttle,
                    _ngConveyor.RunShuttleRepeatAsync(_units.NgConveyor, repeat.Token),
                    repeat));
            if (!_units.MainConveyor && !_units.NgCarrierTransfer)
            {
                using var units = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                await RunAutomaticUnitsAsync(units, repeat: true);
                return;
            }

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RepeatDisplayPhase = RepeatPhase.Automatic;
                await RunToRepeatEndAsync(cancellationToken);
                if (_units.NgCarrierTransfer && _units.NgConveyor)
                {
                    RepeatDisplayPhase = RepeatPhase.ReturnToShuttle;
                    await _inspectionStation.SetLiftUpAsync(true, cancellationToken);
                    await _ngConveyor.ReturnFromConveyorAsync(cancellationToken);
                }
                else if (_units.NgCarrierTransfer && _units.NgShuttle)
                {
                    RepeatDisplayPhase = RepeatPhase.CycleShuttle;
                    await _ngConveyor.CycleShuttleAsync(cancellationToken);
                }

                if (_units.NgCarrierTransfer)
                {
                    RepeatDisplayPhase = RepeatPhase.ReturnToStation3;
                    await _inspectionStation.ReturnToStationAsync(cancellationToken);
                    RepeatDisplayPhase = RepeatPhase.ClearStation3;
                    await _inspectionStation.ClearStationAsync(cancellationToken);
                }
                if (_units.MainConveyor)
                {
                    RepeatDisplayPhase = RepeatPhase.ReturnToStart;
                    await ReturnMainCarrierAsync(cancellationToken);
                }
                cancellationToken.ThrowIfCancellationRequested();
                _log?.LogInformation("Repeat carrier returned to its starting support.");
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
                : RepeatDisplayPhase switch
                {
                    RepeatPhase.ReturnToShuttle => MachineAlarm.NgConveyor,
                    RepeatPhase.CycleShuttle => MachineAlarm.NgShuttle,
                    RepeatPhase.ReturnToStation3 or RepeatPhase.ClearStation3 => MachineAlarm.NgCarrierTransfer,
                    _ => MachineAlarm.MainConveyor,
                };
            _state.SetError(_state.IsError ? _state.Alarm : alarm, exception);
        }
        finally
        {
            repeat.Cancel();
            await Task.WhenAll(independentUnits);
            RepeatDisplayPhase = RepeatPhase.Automatic;
        }
    }

    private async Task RunToRepeatEndAsync(CancellationToken cancellationToken)
    {
        using var cycle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var automatic = RunAutomaticUnitsAsync(cycle, repeat: true);
        try
        {
            if (!_units.NgCarrierTransfer)
                await _conveyor.WaitForRepeatEndAsync(cycle.Token);
            else if (_units.NgConveyor)
                await _ngConveyor.WaitForRepeatEndAsync(cycle.Token);
            else
                await _inspectionStation.WaitForRepeatEndAsync(!_units.NgShuttle, cycle.Token);
        }
        finally
        {
            cycle.Cancel();
            // Reverse begins only after every forward unit has released its commands.
            await automatic;
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private OutputBlockReason MainConveyorReturnBlock
    {
        get
        {
            if (_units.PcbPlacement)
            {
                if (!_pcbPlacement.HandlerRaised)
                    return OutputBlockReason.PlacementNotRaised;
                if (!_pcbPlacement.IsAtHorizontalZ())
                    return OutputBlockReason.PlacementNotAtSafeZ;
            }

            if (_units.BoltFastening)
            {
                if (!_fasteningStation.IsHorizontalMoveAllowed)
                    return OutputBlockReason.FasteningNotRaised;
                if (!_fasteningStation.IsAtSafeZ())
                    return OutputBlockReason.FasteningNotAtSafeZ;
            }

            if (_units.Inspection || _units.NgCarrierTransfer)
            {
                if (!_inspectionStation.IsRaised)
                    return OutputBlockReason.NgPickupNotRaised;
                if (_inspectionStation.IsTransferPending)
                    return OutputBlockReason.NgTransferPending;
            }

            return OutputBlockReason.None;
        }
    }

    private async Task ReturnMainCarrierAsync(CancellationToken cancellationToken)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        void CheckPath()
        {
            if (MainConveyorReturnBlock != OutputBlockReason.None)
                operation.Cancel();
        }

        _state.Changed += CheckPath;
        _pcbPlacement.Changed += CheckPath;
        _fasteningStation.Changed += CheckPath;
        _inspectionStation.Changed += CheckPath;
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
            _pcbPlacement.Changed -= CheckPath;
            _fasteningStation.Changed -= CheckPath;
            _inspectionStation.Changed -= CheckPath;
        }
    }

}
