using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;

namespace IBTM;

public sealed partial class MachineController
{
    public bool RequiresManualClear
    {
        get
        {
            return _conveyor.RequiresManualClear
                || _ngConveyor.RequiresManualClear
                || _pcbPlacement.RequiresManualClear
                || !_state.AutomaticRunning
                    && (_fasteningGantry.HasPendingResult || _fasteningStation.HasPendingResult);
        }
    }

    public bool CanStart
    {
        get
        {
            return IsStartAllowed(StartBlock);
        }
    }

    private bool IsStartAllowed(StartBlockReason block, bool? running = null)
    {
        return !_operations.IsShuttingDown
            && !(running ?? _state.IsRunning)
            && block == StartBlockReason.None;
    }

    public StartBlockReason StartBlock
    {
        get
        {
            return GetStartBlock(_state.MotionReadiness);
        }
    }

    private StartBlockReason GetStartBlock(MotionReadiness motion, bool? bufferConflict = null)
    {
        if (_state.Alarm == MachineAlarm.EmergencyStop)
            return StartBlockReason.EmergencyStop;
        if (_state.Alarm == MachineAlarm.DoorOpen)
            return StartBlockReason.DoorOpen;
        if (_state.Alarm == MachineAlarm.AirPressureLow)
            return StartBlockReason.AirPressure;
        if (_state.Alarm == MachineAlarm.BufferConflict || (bufferConflict ?? _state.Buffer.HasConflict()))
            return StartBlockReason.BufferConflict;
        if (_state.IsError)
            return StartBlockReason.Alarm;
        if (_options.UseEmergencyStop && !_state.EmergencyStopReleased)
            return StartBlockReason.EmergencyStop;
        if (_options.UseAirPressureInterlock && !_state.AirPressureOk)
            return StartBlockReason.AirPressure;
        if (RequiresManualClear)
            return StartBlockReason.ManualClearRequired;
        if (!_state.AutomaticRunning
            && _ngTransfer.CarrierDetected)
        {
            return StartBlockReason.NgCarrierHeld;
        }
        if (motion.Faulted)
            return StartBlockReason.MotionFault;
        if (!motion.ServosOn || !_state.ServoMainContactorOn)
            return StartBlockReason.ServoOff;
        if (!_state.DoorInterlockReady)
            return StartBlockReason.DoorOpen;
        if (!motion.Homed)
            return StartBlockReason.HomeRequired;
        if (_state.RepeatEnabled && !_state.ManualMode)
            return StartBlockReason.TeachingMode;
        if (!TeachingReady)
            return StartBlockReason.TeachingIncomplete;
        if (_state.RepeatEnabled
            && (!_units.MainConveyor
                || !_units.NgCarrierTransfer
                || _units.NgConveyor && !_units.NgShuttle))
            return StartBlockReason.RepeatRouteUnavailable;
        return _units.HasEnabledUnit() ? StartBlockReason.None : StartBlockReason.NoUnitEnabled;
    }

    public bool TeachingReady
    {
        get
        {
            if ((_units.BoltFastening || _units.Inspection)
                && _recipe.Pcb.BoltPoints.Count == 0)
                return false;

            if (_units.BoltFastening
                && (!_carrierReference.IsDefined
                    || _recipe.Pcb.GetBolts().Any(bolt =>
                        bolt.X is null || bolt.Y is null || !_fasteningGantry.HasReference(bolt.Head))))
            {
                return false;
            }

            return !_units.Inspection
                || _recipe.Pcb.GetBolts().All(_boltInspector.HasPosition)
                    && Enum.GetValues<HeatSinkSlot>().All(_boltInspector.HasBarcodeRegion);
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!IsStartAllowed(StartBlock))
                return;
        }
        catch (Exception exception) when (exception is IOException or MotionException)
        {
            StopAndReportFailure(_state.IsError ? _state.Alarm : MachineAlarm.MotionUnavailable, exception);
            return;
        }

        var repeat = _state.RepeatEnabled;
        var startedInManual = _state.ManualMode;
        var feedbackStartedAt = Stopwatch.GetTimestamp();
        using var operation = _operations.TryBegin(cancellationToken);
        if (operation is null)
            return;
        void StopWhenOperationBecomesUnavailable()
        {
            if (operation.IsCancellationRequested)
                return;
            if (_state.IsError)
            {
                operation.Cancel();
                return;
            }

            try
            {
                var modeReady = _state.ManualMode == startedInManual;
                // An unsafe DI already requires a stop. Do not wait for unrelated
                // axis diagnostics before requesting it; safe operation still uses live SDK feedback.
                if (!_io.IsReady
                    || !modeReady
                    || !_state.SafetyReady
                    || !_state.DoorInterlockReady)
                {
                    _log?.Write("Automatic stop: I/O, selector, emergency stop, air or door condition changed.");
                    operation.Cancel();
                    return;
                }

                if (!_state.ServoMainContactorOn)
                {
                    _state.SetError(MachineAlarm.MotionUnavailable);
                }
                else
                {
                    var motion = _state.MotionReadiness;
                    if (!motion.Homed || !motion.ServosOn || motion.Faulted)
                        _state.SetError(MachineAlarm.MotionUnavailable);
                    else if (!_state.Buffer.HasConflict())
                        return;
                }
            }
            catch (Exception exception)
            {
                _state.SetError(MachineAlarm.MotionUnavailable, exception);
            }

            operation.Cancel();
        }

        void StopWhenMotionFeedbackBecomesUnavailable(MotionGroup group, MotionFeedbackSample sample)
        {
            // Live admission already checked the equipment. Do not apply a scan that
            // began before this run (for example, while Home was still completing).
            if (operation.IsCancellationRequested
                || sample.StartedAt < feedbackStartedAt
                || !sample.Enabled
                || !_units.IsMotionEnabled(group))
                return;
            if (_state.IsError)
            {
                operation.Cancel();
                return;
            }

            var motion = sample.Readiness;
            if (sample.IoReady
                && sample.ReadError is null
                && motion.Homed
                && motion.ServosOn
                && !motion.Faulted)
                return;
            // Consume the completed scan, not a second native read that could miss a
            // transient fault. Disabled axes have already been excluded from this snapshot.
            try
            {
                _state.SetError(
                    sample.IoReady ? MachineAlarm.MotionUnavailable : MachineAlarm.IoCommunication,
                    sample.ReadError ?? new InvalidOperationException(
                        $"Motion feedback {group} became unavailable during automatic operation: "
                            + $"homed={motion.Homed}, servosOn={motion.ServosOn}, faulted={motion.Faulted}."));
            }
            finally
            {
                operation.Cancel();
            }
        }

        try
        {
            var (startAlarm, startError) = await InitializeHardwareAsync(operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            if (startAlarm != MachineAlarm.None)
            {
                _state.SetError(startAlarm, startError);
                return;
            }

            _state.Changed += StopWhenOperationBecomesUnavailable;
            _feedback.Sampled += StopWhenMotionFeedbackBecomesUnavailable;
            StopWhenOperationBecomesUnavailable();
            if (operation.IsCancellationRequested)
            {
                return;
            }

            if (_units.MainConveyor)
            {
                try
                {
                    await _conveyor.PrepareEmptyStationsAsync(operation.Token);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    if (!_state.IsError)
                        _state.SetError(MachineAlarm.MainConveyor, exception);
                    else
                        _log?.Error("Main conveyor startup plate lowering failed while stopping.", exception);
                    return;
                }
            }

            operation.Token.ThrowIfCancellationRequested();
            _state.SetAutomaticRunning(true);
            if (repeat)
            {
                await RunRepeatAsync(operation.Token);
            }
            else
            {
                using var cycle = CancellationTokenSource.CreateLinkedTokenSource(operation.Token);
                await RunAutomaticUnitsAsync(cycle, repeat: false);
            }
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
        }
        finally
        {
            _state.Changed -= StopWhenOperationBecomesUnavailable;
            _feedback.Sampled -= StopWhenMotionFeedbackBecomesUnavailable;
            _state.SetAutomaticRunning(false);
            StopAndReportFailure();
        }
    }

    private async Task RunAutomaticUnitsAsync(CancellationTokenSource cycle, bool repeat)
    {
        var runningUnits = new List<Task>();
        if (_units.MainConveyor)
        {
            runningUnits.Add(RunAutomaticUnitAsync(
                MachineAlarm.MainConveyor,
                () => _conveyor.RunAsync(cycle.Token, repeat),
                cycle));
        }

        // Repeat reuses the PCB on the carrier instead of feeding a new PCB.
        if (_units.PcbSupply && !repeat)
        {
            runningUnits.Add(RunAutomaticUnitAsync(
                MachineAlarm.PcbSupply,
                () => _pcbSupply.RunAsync(_recipe.PcbSupply, cycle.Token),
                cycle));
        }

        if (_units.PcbPlacement)
        {
            runningUnits.Add(RunAutomaticUnitAsync(
                MachineAlarm.PcbPlacement,
                () => _pcbPlacement.RunAsync(_recipe.PcbPlacement, cycle.Token, repeat),
                cycle));
        }

        if (_units.PickupBoltFeeder)
        {
            runningUnits.Add(RunAutomaticUnitAsync(
                MachineAlarm.PickupBoltFeeder,
                () => _pickupBoltFeeder.RunAsync(cycle.Token),
                cycle));
        }

        if (_units.ShootingBoltFeeder)
        {
            runningUnits.Add(RunAutomaticUnitAsync(
                MachineAlarm.ShootingBoltFeeder,
                () => _shootingBoltFeeder.RunAsync(cycle.Token),
                cycle));
        }

        if (_units.BoltFastening)
        {
            if (!_units.PickupBoltFeeder)
                _log?.Write("Pickup Feeder OFF; pickup motion and vacuum remain active without bolt detection waits. Motor START and fastening result collection remain active.");
            if (!_units.ShootingBoltFeeder)
                _log?.Write("Shooting Feeder OFF; bolt supply and shooting are skipped. Motor START and fastening result collection remain active.");
            runningUnits.Add(RunAutomaticUnitAsync(
                MachineAlarm.BoltFastening,
                () => _fasteningStation.RunAsync(_recipe.BoltFastening, cycle.Token),
                cycle));
        }

        if (InspectionGantryEnabled)
        {
            runningUnits.Add(RunAutomaticUnitAsync(
                _units.Inspection ? MachineAlarm.Inspection : MachineAlarm.NgCarrierTransfer,
                () => _inspectionStation.RunAsync(
                    _recipe.Pcb.GetBolts().ToArray(),
                    cycle.Token,
                    repeat,
                    holdAtShuttle: repeat && !_units.NgShuttle),
                cycle));
        }

        if (_units.NgShuttle && (!repeat || _units.NgConveyor))
        {
            runningUnits.Add(RunAutomaticUnitAsync(
                MachineAlarm.NgShuttle,
                () => _ngShuttle.RunAsync(cycle.Token),
                cycle));
        }

        if (_units.NgConveyor)
        {
            runningUnits.Add(RunAutomaticUnitAsync(
                MachineAlarm.NgConveyor,
                () => _ngConveyor.RunAsync(cycle.Token, repeat),
                cycle));
        }

        // STOP and repeat reversal wait for every unit's command cleanup.
        await Task.WhenAll(runningUnits).ConfigureAwait(false);
    }

    private async Task RunAutomaticUnitAsync(
        MachineAlarm alarm,
        Func<Task> start,
        CancellationTokenSource cycle)
    {
        // An earlier unit can fail or receive STOP before its first await.
        if (cycle.IsCancellationRequested)
        {
            return;
        }

        try
        {
            // Invoke inside the error boundary to include synchronous startup failures.
            await start();
            if (!cycle.IsCancellationRequested && !_state.IsError)
            {
                _state.SetError(alarm, new InvalidOperationException(
                    $"Automatic unit {alarm} returned before a stop was requested."));
            }
        }
        catch (OperationCanceledException) when (cycle.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (!_state.IsError)
            {
                // Keep the unit name even when the alarm is classified as MotionUnavailable.
                // SetError records the original exception and its full stack trace.
                _log?.Error($"Automatic unit {alarm} failed. {exception.Message}");
                _state.SetError(
                    IsMotionFailure(exception) ? MachineAlarm.MotionUnavailable : alarm,
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
}
