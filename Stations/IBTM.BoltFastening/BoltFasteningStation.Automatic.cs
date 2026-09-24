using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;
using Microsoft.Extensions.Logging;

namespace IBTM.BoltFastening;

public sealed partial class BoltFasteningStation
{
    private bool _repeat;
    private HeatSinkSlot[]? _runTargets;
    // Selected work belongs only to this run; STOP discards it.
    private BoltPoint[]? _runBolts;
    private int _boltIndex;
    private ConveyorStation.Job? _runJob;
    private CancellationTokenSource? _carrierOperation;

    private BoltPoint? ActiveBolt
    {
        get
        {
            var index = _boltIndex;
            return _runBolts is { } bolts && index < bolts.Length ? bolts[index] : null;
        }
    }

    private BoltPoint? StandbyBolt
    {
        get
        {
            return _recipes.Current.Pcb.GetBolts(HeatSinkSlot.HeatSink1)
                .Where(bolt => bolt.Head == FasteningHead.Shooting)
                .OrderBy(bolt => bolt.Number)
                .FirstOrDefault();
        }
    }

    private IEnumerable<HeatSinkSlot> Targets
    {
        get
        {
            return _runTargets is { } targets
                ? targets
                : Enum.GetValues<HeatSinkSlot>().Where(Station.IsHeatSinkPresent);
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken = default, bool repeat = false)
    {
        if (cancellationToken.IsCancellationRequested)
            return;
        _repeat = repeat;
        Exception? failure = null;
        try
        {
            BeginRun();
            if (Enabled)
                Station.Restart(Station.CurrentJob);
            while (!cancellationToken.IsCancellationRequested)
            {
                if (Enabled && repeat && !_units.MainConveyor && Station.Completed)
                {
                    if (!Station.CarrierSeated || !IsHorizontalMoveAllowed || !IsAtSafeZ())
                        throw new InvalidOperationException("Fastening repeat requires the original seated carrier and both heads at safe height.");
                    Station.StartRepeat(Station.CurrentJob);
                }
                var step = GetNextStep();
                if (!await ExecuteStepAsync(step, cancellationToken))
                    await WaitForChangeAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            _repeat = false;
            ClearCarrierOperation();
            try
            {
                if (Enabled)
                    StopShooting(failure);
            }
            finally
            {
                EndRun(cancellationToken);
            }
        }
    }

    public BoltFasteningState GetNextStep(bool live = true)
    {
        var bolt = _runBolts is null ? ApplicableBolts.FirstOrDefault() : ActiveBolt;
        if (!Enabled)
            return BoltFasteningState.Disabled;
        if (!IsReadyToFasten)
        {
            var standby = StandbyBolt;
            return standby is not null && standby.IsFasteningPositionDefined
                && (!IsHorizontalMoveAllowed || !IsAt(standby, live, atSafeZ: true)
                    || PickupTablePosition != BoltCylinderState.Up)
                ? BoltFasteningState.MovingToStandby
                : BoltFasteningState.Waiting;
        }

        if (_runJob is null || _carrierOperation?.IsCancellationRequested == true
            || !ReferenceEquals(_runJob, Station.CurrentJob))
            return BoltFasteningState.PreparingCarrier;

        switch (bolt?.Head)
        {
            case FasteningHead.Shooting:
                return BoltFasteningState.FasteningPcb;
            case FasteningHead.Pickup:
                return BoltFasteningState.FasteningPickup;
            default:
                return BoltFasteningState.CompletingCarrier;
        }
    }

    private async Task<bool> ExecuteStepAsync(BoltFasteningState step, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnterStep(step, GetActiveBolt(step)?.ToString(), Station.CurrentJob.Id);
        switch (step)
        {
            case BoltFasteningState.Disabled:
                if (Station.CarrierSeated)
                    Station.Complete(Station.CurrentJob);
                return false;
            case BoltFasteningState.MovingToStandby:
                var position = _settings.GetBoltPosition(StandbyBolt!);
                await RaiseCylindersAsync(cancellationToken);
                await MoveToSafeZAsync(cancellationToken);
                await SetPickupTableDownAsync(false, cancellationToken);
                EnsureCanMoveHorizontal(cancellationToken);
                await _motion.MoveToXYAsync(position.X, position.Y, _settings.Motion.HorizontalSpeed, cancellationToken);
                return true;
            case BoltFasteningState.Waiting:
                return false;
            case BoltFasteningState.PreparingCarrier:
                ClearCarrierOperation();
                _runTargets = Enum.GetValues<HeatSinkSlot>().Where(Station.IsHeatSinkPresent).ToArray();
                foreach (var heatSink in _runTargets)
                {
                    if (!_recipes.Current.Pcb.GetBolts(heatSink).Any())
                        throw new InvalidOperationException(
                            $"{heatSink.GetDescription()} has no taught bolts. Complete bolt teaching before fastening.");
                }
                _runJob = Station.CurrentJob;
                _runBolts = ApplicableBolts.ToArray();
                _carrierOperation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                Station.Changed += CheckCarrier;
                CheckCarrier();
                NotifyChanged();
                return true;
            case BoltFasteningState.FasteningPcb or BoltFasteningState.FasteningPickup or BoltFasteningState.CompletingCarrier:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(step));
        }

        var operation = _carrierOperation
            ?? throw new InvalidOperationException("No fastening work is selected.");
        var job = _runJob!;
        var token = operation.Token;
        try
        {
            CheckCarrier();
            token.ThrowIfCancellationRequested();
            Station.RequireCurrentJob(job);
            if (step == BoltFasteningState.CompletingCarrier)
            {
                foreach (var heatSink in _runTargets!)
                    Station.GetAssembly(job, heatSink).CompleteFastening();
                await FinishFasteningAsync(FasteningHead.Pickup, token);
                await FinishFasteningAsync(FasteningHead.Shooting, token);
                await MoveToSafeZAsync(token);
                token.ThrowIfCancellationRequested();
                Station.Complete(job);
                ClearCarrierOperation();
                return true;
            }

            var bolt = ActiveBolt ?? throw new InvalidOperationException("No bolt is selected.");
            var feeding = !_repeat && _units.IsBoltFeederEnabled(bolt.Head);
            switch (bolt.Head)
            {
                case FasteningHead.Shooting:
                {
                    if (PickupTablePosition != BoltCylinderState.Up)
                    {
                        await RaiseCylindersAsync(token);
                        await MoveToSafeZAsync(token);
                        await SetPickupTableDownAsync(false, token);
                    }
                    if (ShootingHeadPosition != BoltCylinderState.Up
                        && (!IsAt(bolt) || feeding))
                        await ClearHeadAsync(FasteningHead.Shooting, token);
                    var moveRequired = !IsAt(bolt);
                    if (moveRequired || feeding)
                        await RaiseCylindersAsync(token);

                    if (moveRequired && feeding)
                    {
                        using var preparation = CancellationTokenSource.CreateLinkedTokenSource(token);
                        var moving = MoveToBoltAsync(bolt, preparation.Token);
                        if (moving.IsCompleted)
                            await moving;
                        var shooting = ShootBoltAsync(preparation.Token);
                        var first = await Task.WhenAny(moving, shooting);
                        if (!first.IsCompletedSuccessfully)
                            preparation.Cancel();
                        // Drain both operations, including STOP/air-OFF cleanup on failure.
                        await Task.WhenAll(moving, shooting);
                    }
                    else if (moveRequired)
                        await MoveToBoltAsync(bolt, token);
                    else if (feeding)
                        await ShootBoltAsync(token);
                    break;
                }
                case FasteningHead.Pickup:
                {
                    if (ShootingHeadPosition != BoltCylinderState.Up)
                        await ClearHeadAsync(FasteningHead.Shooting, token);
                    if (PickupTablePosition != BoltCylinderState.Down)
                    {
                        await RaiseCylindersAsync(token);
                        await MoveToSafeZAsync(token);
                        await SetPickupTableDownAsync(true, token);
                    }

                    if (!PickupBoltLoaded)
                    {
                        await MoveToPickupXYAsync(token);
                        var retryCount = _settings.PickupRetryCount;
                        for (var retry = 0; ; retry++)
                        {
                            token.ThrowIfCancellationRequested();
                            // Late feedback at Safe Z can confirm the previous attempt.
                            if (retry > 0 && PickupBoltLoaded)
                                break;
                            if (feeding)
                                await WaitForBoltSupplyAsync(FasteningHead.Pickup, token);
                            await MoveToPickupZAsync(token);
                            if (feeding)
                                await SetVacuumAsync(FasteningHead.Pickup, true, token, waitForFeedback: false);
                            await ReturnFromPickupAsync(token);
                            if (!feeding)
                                break;
                            try
                            {
                                await _io.WaitForInputAsync(InputIo.PickupHeadVacuumDetected, true, token);
                                break;
                            }
                            catch (IoTimeoutException) when (retry < retryCount && !token.IsCancellationRequested)
                            {
                                _log?.LogWarning(
                                    "Pickup bolt {Bolt}, {HeatSink}: vacuum not detected at Safe Z; retry {Retry}/{RetryCount}.",
                                    bolt.Number, bolt.HeatSink, retry + 1, retryCount);
                            }
                        }
                        token.ThrowIfCancellationRequested();
                        Station.RequireCurrentJob(job);
                    }
                    else if (IsAtPickupXY())
                        await ReturnFromPickupAsync(token);
                    if (!IsAt(bolt))
                    {
                        await RaiseCylindersAsync(token);
                        await MoveToBoltAsync(bolt, token);
                    }
                    break;
                }
                default:
                    throw new ArgumentOutOfRangeException(nameof(bolt.Head));
            }
            var assembly = Station.GetAssembly(job, bolt.HeatSink);
            var result = await FastenAsync(bolt, token);
            Exception? clearFailure = null;
            try
            {
                // Result notifications can write to disk. Finish physical clearance first.
                await ClearHeadAsync(bolt.Head, token);
            }
            catch (Exception exception)
            {
                clearFailure = exception;
                throw;
            }
            finally
            {
                // Keep the measured result with its original carrier even if clearance
                // is cancelled or fails. A storage failure must not hide a motion failure.
                try
                {
                    switch (bolt.Head)
                    {
                        case FasteningHead.Shooting:
                            assembly.RecordPcbBolt(bolt.Number, result);
                            break;
                        case FasteningHead.Pickup:
                            assembly.RecordPickupBolt(bolt.Number, result);
                            break;
                    }
                }
                catch (Exception recordFailure) when (clearFailure is not null)
                {
                    throw new AggregateException(clearFailure, recordFailure);
                }
            }

            token.ThrowIfCancellationRequested();
            Station.RequireCurrentJob(job);
            _boltIndex++;
            NotifyChanged();
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested)
        {
            ClearCarrierOperation();
        }
        return true;
    }

    private void CheckCarrier()
    {
        if (_carrierOperation is not { } operation)
            return;
        lock (operation)
        {
            if (ReferenceEquals(operation, _carrierOperation)
                && (!Station.CarrierSeated || !ReferenceEquals(_runJob, Station.CurrentJob)))
                operation.Cancel();
        }
    }

    private void ClearCarrierOperation()
    {
        Station.Changed -= CheckCarrier;
        if (_carrierOperation is { } operation)
        {
            lock (operation)
            {
                _carrierOperation = null;
                operation.Dispose();
            }
        }
        _runJob = null;
        _runTargets = null;
        _runBolts = null;
        _boltIndex = 0;
    }

    public BoltPoint? GetActiveBolt(BoltFasteningState? state = null)
    {
        switch (state ?? (Step is BoltFasteningState step ? step : GetNextStep()))
        {
            case BoltFasteningState.MovingToStandby:
                return StandbyBolt;
            case BoltFasteningState.PreparingCarrier or BoltFasteningState.FasteningPcb or BoltFasteningState.FasteningPickup:
                return _runBolts is null ? ApplicableBolts.FirstOrDefault() : ActiveBolt;
            default:
                return null;
        }
    }

    private async Task<BoltResult> FastenAsync(
        BoltPoint bolt,
        CancellationToken cancellationToken)
    {
        var job = Station.CurrentJob;
        var head = GetHead(bolt.Head);
        await head.SelectPresetAsync(1, cancellationToken);
        // The motor rotates only; the cylinder supplies the forward feed.
        // Raise before a new start, including a retry at the same XY.
        await RaiseCylindersAsync(cancellationToken);
        Station.RequireCurrentJob(job);
        if (!IsAt(bolt))
            throw new InvalidOperationException("The head must be at the bolt's fastening XYZ before starting.");

        using var fastening = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        void CheckPickupTable()
        {
            if (bolt.Head == FasteningHead.Shooting
                && PickupTablePosition != BoltCylinderState.Up)
                fastening.Cancel();
        }

        Changed += CheckPickupTable;
        try
        {
            CheckPickupTable();
            var dryRunMilliseconds = _repeat || !_units.IsBoltFeederEnabled(bolt.Head)
                ? _settings.DryRunMilliseconds : 0;
            _log?.LogInformation(
                "Bolt {Head}, {HeatSink}, point {Bolt}: starting {Controller}; requesting head DOWN; dry run={DryRunMilliseconds} ms (0=wait for fastening result).",
                bolt.Head, bolt.HeatSink, bolt.Number, head.GetType().Name, dryRunMilliseconds);
            var completed = await head.TightenAsync(fastening.Token, LowerHeadWhileFasteningAsync, dryRunMilliseconds);
            completed = completed with { RecordedAt = completed.RecordedAt ?? DateTimeOffset.Now };
            _log?.LogInformation(
                "Bolt {Head}, {HeatSink}, point {Bolt}: cycle completed; success={Success}, source={Source}, error={Error}.",
                bolt.Head, bolt.HeatSink, bolt.Number, completed.Success, completed.Source, completed.Error);
            Station.RequireCurrentJob(job);
            return completed;
        }
        catch (OperationCanceledException) when (fastening.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested)
        {
            throw new MotionInterlockException("Keep the pickup table raised during shooting fastening.");
        }
        finally
        {
            Changed -= CheckPickupTable;
        }

        Task LowerHeadWhileFasteningAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            _log?.LogInformation("Bolt {Head}: motor START completed; requesting head DOWN.", bolt.Head);
            // Screw contact can stop the cylinder before its DOWN sensor.
            _io.SetOutput(bolt.Head == FasteningHead.Pickup
                ? OutputIo.PickupHeadDown : OutputIo.ShootingHeadDown, true);
            token.ThrowIfCancellationRequested();
            _log?.LogInformation("Bolt {Head}: head DOWN output sent.", bolt.Head);
            return Task.CompletedTask;
        }
    }

    private async Task ClearHeadAsync(FasteningHead head, CancellationToken cancellationToken)
    {
        await FinishFasteningAsync(head, cancellationToken);
        await RaiseCylindersAsync(cancellationToken);
        await MoveToSafeZAsync(cancellationToken);
    }

    private IEnumerable<BoltPoint> ApplicableBolts
    {
        get
        {
            return _recipes.Current.Pcb
                .BoltPoints
                .Where(bolt => Targets.Contains(bolt.HeatSink))
                .OrderBy(bolt => bolt.Head == FasteningHead.Shooting ? 0 : 1)
                .ThenBy(bolt => bolt.HeatSink)
                .ThenBy(bolt => bolt.Number);
        }
    }
}
