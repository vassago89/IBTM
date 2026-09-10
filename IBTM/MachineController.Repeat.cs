using System;
using System.Collections.Generic;
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
    [Description("Auto → NG end")]
    Automatic,
    [Description("NG end → Shuttle")]
    ReturnToShuttle,
    [Description("Shuttle → Station 3")]
    ReturnToStation3,
    [Description("Station 3 → Front → Station 1")]
    ReturnToStart,
}

public sealed partial class MachineController
{
    // An unfinished route destination survives STOP; carrier position still comes from I/O.
    private volatile RepeatPhase _repeatPhase;
    private int _repeatCycles;

    private async Task RunAutomaticUnitsAsync(CancellationTokenSource cycle, bool repeat)
    {
        var runningUnits = new List<Task>();
        void StartUnit(bool enabled, MachineAlarm alarm, Func<Task> start)
        {
            if (enabled && !cycle.IsCancellationRequested)
            {
                runningUnits.Add(RunUnitAsync(alarm, start));
            }
        }
    
        async Task RunUnitAsync(MachineAlarm alarm, Func<Task> start)
        {
            try
            {
                await start();
                if (!cycle.IsCancellationRequested && !_state.IsError)
                {
                    _state.SetError(alarm);
                }
            }
            catch (OperationCanceledException) when (cycle.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                if (!_state.IsError)
                {
                    _state.SetError(
                        exception is MotionException ? MachineAlarm.MotionUnavailable : alarm,
                        exception);
                }
                else
                {
                    _log?.Error(
                        $"Automatic unit {alarm} failed while stopping; existing alarm={_state.Alarm}.",
                        exception);
                }
            }
            finally
            {
                cycle.Cancel();
            }
        }
    
        StartUnit(
            _units.MainConveyor,
            MachineAlarm.MainConveyor,
            () => _conveyor.RunAsync(cycle.Token, repeat));
        StartUnit(
            _units.PcbSupply,
            MachineAlarm.PcbSupply,
            () => _pcbSupply.RunAsync(_recipe.PcbSupply, cycle.Token));
        StartUnit(
            _units.PcbPlacement,
            MachineAlarm.PcbPlacement,
            () => _pcbPlacement.RunAsync(_recipe.PcbPlacement, cycle.Token));
        StartUnit(
            _units.PickupBoltFeeder,
            MachineAlarm.PickupBoltFeeder,
            () => _pickupBoltFeeder.RunAsync(cycle.Token));
        StartUnit(
            _units.ShootingBoltFeeder,
            MachineAlarm.ShootingBoltFeeder,
            () => _shootingBoltFeeder.RunAsync(cycle.Token));
        StartUnit(
            _units.BoltFastening,
            MachineAlarm.BoltFastening,
            () => _fasteningStation.RunAsync(_recipe.BoltFastening, cycle.Token));
        StartUnit(
            InspectionGantryEnabled,
            _units.Inspection ? MachineAlarm.Inspection : MachineAlarm.NgCarrierTransfer,
            () => _inspectionStation.RunAsync(_recipe.Pcb.GetBolts().ToArray(), cycle.Token, repeat));
        StartUnit(
            _units.NgShuttle,
            MachineAlarm.NgShuttle,
            () => _ngShuttle.RunAsync(cycle.Token));
        StartUnit(
            _units.NgConveyor,
            MachineAlarm.NgConveyor,
            () => _ngConveyor.RunAsync(cycle.Token, repeat));
    
        await Task.WhenAll(runningUnits).ConfigureAwait(false);
    }

    private async Task RunRepeatAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_repeatPhase == RepeatPhase.Automatic)
            {
                var carriers = CarrierInputs.Count(input =>
                    input != InputIo.NgCarrierDetected && _io.GetInput(input));
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
                        await RunToNgEndAsync(cancellationToken);
                        SetRepeatPhase(RepeatPhase.ReturnToShuttle);
                        break;

                    case RepeatPhase.ReturnToShuttle:
                        await ReturnNgCarrierAsync(cancellationToken);
                        SetRepeatPhase(RepeatPhase.ReturnToStation3);
                        break;

                    case RepeatPhase.ReturnToStation3:
                        await _io.SetOutputAndWaitAsync(
                            OutputIo.InspectionStopperUp, false, cancellationToken);
                        await _inspectionWork.Station.RaiseBackupPlateAsync(cancellationToken);
                        await _ngShuttle.SetDownAsync(false, cancellationToken);
                        await _ngMove.RunToAsync(NgTransferDestination.Station, cancellationToken);
                        SetRepeatPhase(RepeatPhase.ReturnToStart);
                        break;

                    case RepeatPhase.ReturnToStart:
                        await ReturnMainCarrierAsync(cancellationToken);
                        cancellationToken.ThrowIfCancellationRequested();
                        _repeatCycles++;
                        _log?.Write($"Repeat cycle {_repeatCycles} completed at Station 1.");
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
            var alarm = exception is MotionException
                ? MachineAlarm.MotionUnavailable
                : _repeatPhase switch
                {
                    RepeatPhase.ReturnToShuttle => MachineAlarm.NgConveyor,
                    RepeatPhase.ReturnToStation3 => MachineAlarm.NgCarrierTransfer,
                    _ => MachineAlarm.MainConveyor,
                };
            _state.SetError(alarm, exception);
        }
    }

    private async Task RunToNgEndAsync(CancellationToken cancellationToken)
    {
        using var cycle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var automatic = RunAutomaticUnitsAsync(cycle, repeat: true);
        try
        {
            await _io.WaitForInputAsync(
                InputIo.NgConveyorPosition1Occupied, true, Timeout.Infinite, cycle.Token);
        }
        finally
        {
            cycle.Cancel();
            // Reverse begins only after every forward unit has released its commands.
            await automatic;
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task ReturnNgCarrierAsync(CancellationToken cancellationToken)
    {
        await _ngTransfer.RaiseAsync(cancellationToken);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        void CheckPickup()
        {
            if (!_ngTransfer.IsRaised)
                operation.Cancel();
        }

        _ngTransfer.Changed += CheckPickup;
        try
        {
            CheckPickup();
            operation.Token.ThrowIfCancellationRequested();
            if (!_ngShuttle.Feedback.CarrierDetected)
                await _ngShuttle.SetDownAsync(true, operation.Token);
            await _ngConveyor.ReturnToShuttleAsync(operation.Token);
            await _ngShuttle.SetDownAsync(false, operation.Token);
            operation.Token.ThrowIfCancellationRequested();
        }
        finally
        {
            _ngTransfer.Changed -= CheckPickup;
            _ngConveyor.Stop();
        }
    }

    private async Task ReturnMainCarrierAsync(CancellationToken cancellationToken)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        void CheckPath()
        {
            if (!MainConveyorPathClear)
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
