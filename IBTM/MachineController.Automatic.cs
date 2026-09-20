using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;
using Microsoft.Extensions.Logging;

namespace IBTM;

public sealed partial class MachineController
{
    public bool IsStartAllowed => IsStartAllowedFor(StartBlock);

    public StartBlockReason StartBlock => GetStartBlock(_state.MotionReadiness);

    public bool TeachingReady
    {
        get
        {
            switch (true)
            {
                case true when (_units.BoltFastening || _units.Inspection)
                    && _recipes.Current.Pcb.BoltPoints.Count == 0:
                    return false;
                case true when _units.BoltFastening
                    && (!_carrierReference.IsDefined
                        || _recipes.Current.Pcb.BoltPoints.Any(bolt =>
                            bolt.X is null || bolt.Y is null || !_fasteningGantry.HasReference(bolt.Head))):
                    return false;
                default:
                    return !_units.Inspection
                        || _recipes.Current.Pcb.BoltPoints.All(_boltInspector.HasPosition)
                            && Enum.GetValues<HeatSinkSlot>().All(_boltInspector.HasBarcodeRegion);
            }
        }
    }

    private bool IsStartAllowedFor(StartBlockReason block, bool? running = null)
    {
        return !_operations.IsShuttingDown
            && !(running ?? _state.IsRunning)
            && block == StartBlockReason.None;
    }

    private StartBlockReason GetStartBlock(MotionReadiness motion)
    {
        switch (true)
        {
            case true when _state.Alarm == MachineAlarm.EmergencyStop:
                return StartBlockReason.EmergencyStop;
            case true when _state.Alarm == MachineAlarm.DoorOpen:
                return StartBlockReason.DoorOpen;
            case true when _state.Alarm == MachineAlarm.AirPressureLow:
                return StartBlockReason.AirPressure;
            case true when _state.IsError:
                return StartBlockReason.Alarm;
            case true when _options.UseEmergencyStop && !_state.EmergencyStopReleased:
                return StartBlockReason.EmergencyStop;
            case true when _options.UseAirPressureInterlock && !_state.AirPressureOk:
                return StartBlockReason.AirPressure;
            case true when motion.Faulted:
                return StartBlockReason.MotionFault;
            case true when !motion.ServosOn || !_state.ServoMainContactorOn:
                return StartBlockReason.ServoOff;
            case true when !_state.DoorInterlockReady:
                return StartBlockReason.DoorOpen;
            case true when !motion.Homed:
                return StartBlockReason.HomeRequired;
            case true when _state.RepeatEnabled && !_state.ManualMode:
                return StartBlockReason.TeachingMode;
            case true when !TeachingReady:
                return StartBlockReason.TeachingIncomplete;
            case true when _state.RepeatEnabled
                && (!_units.MainConveyor
                    || !_units.NgCarrierTransfer
                    || _units.NgConveyor && !_units.NgShuttle):
                return StartBlockReason.RepeatRouteUnavailable;
            default:
                return _units.IsAnyUnitEnabled ? StartBlockReason.None : StartBlockReason.NoUnitEnabled;
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await Task.Run(() => RunAutomaticAsync(cancellationToken), cancellationToken);
    }

    private async Task RunAutomaticAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!IsStartAllowedFor(StartBlock))
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
            switch (true)
            {
                case true when operation.IsCancellationRequested:
                    return;
                case true when _state.IsError:
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
                    _log?.LogInformation("Automatic stop: I/O, selector, emergency stop, air or door condition changed.");
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
                    else
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
            switch (true)
            {
                case true when operation.IsCancellationRequested
                    || sample.StartedAt < feedbackStartedAt
                    || !sample.Enabled
                    || !_units.IsMotionEnabled(group):
                    return;
                case true when _state.IsError:
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

            await RaiseCylindersAsync(operation);

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
                        _log?.LogError(exception, "Main conveyor startup plate lowering failed while stopping.");
                    return;
                }
            }

            operation.Token.ThrowIfCancellationRequested();
            _state.AutomaticRunning = true;
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
        catch (IOException exception)
        {
            _state.SetError(_state.IsError ? _state.Alarm : MachineAlarm.IoCommunication, exception);
        }
        finally
        {
            _state.Changed -= StopWhenOperationBecomesUnavailable;
            _feedback.Sampled -= StopWhenMotionFeedbackBecomesUnavailable;
            _state.AutomaticRunning = false;
            StopAndReportFailure();
        }
    }

    private async Task RunAutomaticUnitsAsync(CancellationTokenSource cycle, bool repeat)
    {
        var runningUnits = new List<Task>();
        // Repeat reuses the PCB on the carrier instead of feeding a new PCB.
        if (!cycle.IsCancellationRequested && (_units.PcbSupply && !repeat))
        {
            runningUnits.Add(ObserveAutomaticUnitAsync(
                MachineAlarm.PcbSupply,
                _pcbSupply.RunAsync(_recipes.Current.PcbSupply, cycle.Token),
                cycle));
        }

        if (!cycle.IsCancellationRequested)
        {
            runningUnits.Add(ObserveAutomaticUnitAsync(
                MachineAlarm.PcbPlacement,
                _pcbPlacement.RunAsync(_recipes.Current.PcbPlacement, cycle.Token, repeat),
                cycle));
        }

        if (!cycle.IsCancellationRequested && _units.PickupBoltFeeder)
        {
            runningUnits.Add(ObserveAutomaticUnitAsync(
                MachineAlarm.PickupBoltFeeder,
                _pickupBoltFeeder.RunAsync(cycle.Token),
                cycle));
        }

        if (!cycle.IsCancellationRequested && _units.ShootingBoltFeeder)
        {
            runningUnits.Add(ObserveAutomaticUnitAsync(
                MachineAlarm.ShootingBoltFeeder,
                _shootingBoltFeeder.RunAsync(cycle.Token),
                cycle));
        }

        if (!cycle.IsCancellationRequested)
        {
            if (_units.BoltFastening && !_units.PickupBoltFeeder)
                _log?.LogInformation(
                    "Pickup Feeder OFF; pickup motion and vacuum remain active without bolt detection waits. Motor START and fastening result collection remain active.");
            if (_units.BoltFastening && !_units.ShootingBoltFeeder)
                _log?.LogInformation(
                    "Shooting Feeder OFF; bolt supply and shooting are skipped. Motor START and fastening result collection remain active.");
            runningUnits.Add(ObserveAutomaticUnitAsync(
                MachineAlarm.BoltFastening,
                _fasteningStation.RunAsync(cycle.Token),
                cycle));
        }

        if (!cycle.IsCancellationRequested)
        {
            runningUnits.Add(ObserveAutomaticUnitAsync(
                _units.Inspection ? MachineAlarm.Inspection : MachineAlarm.NgCarrierTransfer,
                _inspectionStation.RunAsync(
                    _recipes.Current.Pcb.BoltPoints.ToArray(),
                    cycle.Token,
                    repeat,
                    holdAtShuttle: repeat && !_units.NgShuttle),
                cycle));
        }

        if (!cycle.IsCancellationRequested && (_units.NgShuttle && (!repeat || _units.NgConveyor)))
        {
            runningUnits.Add(ObserveAutomaticUnitAsync(
                MachineAlarm.NgShuttle,
                _ngShuttle.RunAsync(cycle.Token),
                cycle));
        }

        if (!cycle.IsCancellationRequested && _units.NgConveyor)
        {
            runningUnits.Add(ObserveAutomaticUnitAsync(
                MachineAlarm.NgConveyor,
                _ngConveyor.RunAsync(cycle.Token, repeat),
                cycle));
        }

        // Stations report existing carrier work before the conveyor selects its first transfer.
        if (!cycle.IsCancellationRequested && _units.MainConveyor)
        {
            runningUnits.Add(ObserveAutomaticUnitAsync(
                MachineAlarm.MainConveyor,
                _conveyor.RunAsync(cycle.Token, repeat),
                cycle));
        }

        // STOP and repeat reversal wait for every unit's command cleanup.
        await Task.WhenAll(runningUnits).ConfigureAwait(false);
    }

    private async Task ObserveAutomaticUnitAsync(
        MachineAlarm alarm,
        Task running,
        CancellationTokenSource cycle)
    {
        try
        {
            await running;
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
                _log?.LogError("{Message}", $"Automatic unit {alarm} failed. {exception.Message}");
                _state.SetError(
                    IsMotionFailure(exception) ? MachineAlarm.MotionUnavailable : alarm,
                    exception);
            }
            else
            {
                _log?.LogError(exception, "{Message}", $"Automatic unit {alarm} failed while stopping; existing alarm={_state.Alarm}.");
            }
        }
        finally
        {
            cycle.Cancel();
        }
    }
}
