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
    // Display the current loop destination only; never resume it after STOP.
    private BoltPoint? _activeBolt;

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
                : Enum.GetValues<HeatSinkSlot>().Where(_work.Station.IsHeatSinkPresent);
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken = default, bool repeat = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _repeat = repeat;
        Exception? failure = null;
        try
        {
            BeginRun();
            if (_work.Enabled)
                _work.Restart(_work.CurrentJob);
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!_work.Enabled)
                {
                    var job = _work.CurrentJob;
                    if (_work.Station.CarrierSeated)
                        _work.Complete(job);
                    TraceStep(BoltFasteningState.Disabled, workId: job.Id,
                        waitingFor: _work.Completed ? "carrier transfer" : "carrier seated");
                    await WaitForChangeAsync(cancellationToken);
                    continue;
                }
                if (repeat && !_units.MainConveyor && _work.Completed)
                {
                    if (!_work.Station.CarrierSeated || !IsHorizontalMoveAllowed || !IsAtSafeZ())
                        throw new InvalidOperationException("Fastening repeat requires the original seated carrier and both heads at safe height.");
                    _work.StartRepeat(_work.CurrentJob);
                }
                await RunCarrierAsync(cancellationToken);
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
            _activeBolt = null;
            try
            {
                if (_work.Enabled)
                    StopShooting(failure);
            }
            finally
            {
                EndRun(cancellationToken);
            }
        }
    }

    private async Task RunCarrierAsync(CancellationToken cancellationToken)
    {
        if (!_work.IsReadyToFasten)
        {
            var state = GetState();
            TraceStep(state, GetActiveBolt(state)?.ToString(), _work.CurrentJob.Id);
            if (state == BoltFasteningState.MovingToStandby)
            {
                var position = _settings.GetBoltPosition(StandbyBolt!);
                await RaiseCylindersAsync(cancellationToken);
                await MoveToSafeZAsync(cancellationToken);
                await SetPickupTableDownAsync(false, cancellationToken);
                EnsureCanMoveHorizontal(cancellationToken);
                await _motion.MoveToXYAsync(position.X, position.Y, _settings.Motion.HorizontalSpeed, cancellationToken);
            }
            else
            {
                await WaitForChangeAsync(cancellationToken);
            }
            return;
        }

        using var carrierOperation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        void CheckCarrier()
        {
            if (!_work.Station.CarrierSeated)
                carrierOperation.Cancel();
        }

        _runTargets = Enum.GetValues<HeatSinkSlot>().Where(_work.Station.IsHeatSinkPresent).ToArray();
        _work.Changed += CheckCarrier;
        try
        {
            CheckCarrier();
            carrierOperation.Token.ThrowIfCancellationRequested();
            foreach (var heatSink in _runTargets)
            {
                if (!_recipes.Current.Pcb.GetBolts(heatSink).Any())
                    throw new InvalidOperationException(
                        $"{heatSink.GetDescription()} has no taught bolts. Complete bolt teaching before fastening.");
            }
            var job = _work.CurrentJob;
            foreach (var bolt in ApplicableBolts)
            {
                carrierOperation.Token.ThrowIfCancellationRequested();
                _work.RequireCurrentJob(job);
                _activeBolt = bolt;
                var state = bolt.Head == FasteningHead.Shooting
                    ? BoltFasteningState.FasteningPcb : BoltFasteningState.FasteningPickup;
                TraceStep(state, bolt.ToString(), job.Id);
                var token = carrierOperation.Token;
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
                            _work.RequireCurrentJob(job);
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
                var assembly = _work.GetAssembly(job, bolt.HeatSink);
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
            }
            carrierOperation.Token.ThrowIfCancellationRequested();
            _work.RequireCurrentJob(job);
            _activeBolt = null;
            TraceStep(BoltFasteningState.CompletingCarrier, workId: job.Id);
            foreach (var heatSink in _runTargets)
                _work.GetAssembly(job, heatSink).CompleteFastening();
            await FinishFasteningAsync(FasteningHead.Pickup, carrierOperation.Token);
            await FinishFasteningAsync(FasteningHead.Shooting, carrierOperation.Token);
            await MoveToSafeZAsync(carrierOperation.Token);
            carrierOperation.Token.ThrowIfCancellationRequested();
            _work.Complete(job);
        }
        catch (OperationCanceledException) when (carrierOperation.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            _work.Changed -= CheckCarrier;
            _runTargets = null;
            _activeBolt = null;
        }
    }

    public BoltFasteningState GetState(bool live = true)
    {
        var bolt = _activeBolt ?? ApplicableBolts.FirstOrDefault();
        if (!_work.Enabled)
            return BoltFasteningState.Disabled;
        if (!_work.IsReadyToFasten)
        {
            var standby = StandbyBolt;
            return standby is not null && standby.IsFasteningPositionDefined
                && (!IsHorizontalMoveAllowed || !IsAt(standby, live, atSafeZ: true)
                    || PickupTablePosition != BoltCylinderState.Up)
                ? BoltFasteningState.MovingToStandby
                : BoltFasteningState.Waiting;
        }

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

    public BoltPoint? GetActiveBolt(BoltFasteningState? state = null)
    {
        switch (state ?? GetState())
        {
            case BoltFasteningState.MovingToStandby:
                return StandbyBolt;
            case BoltFasteningState.FasteningPcb or BoltFasteningState.FasteningPickup:
                return _activeBolt ?? ApplicableBolts.FirstOrDefault();
            default:
                return null;
        }
    }

    private async Task<BoltResult> FastenAsync(
        BoltPoint bolt,
        CancellationToken cancellationToken)
    {
        var job = _work.CurrentJob;
        var head = GetHead(bolt.Head);
        await head.SelectPresetAsync(1, cancellationToken);
        // The motor rotates only; the cylinder supplies the forward feed.
        // Raise before a new start, including a retry at the same XY.
        await RaiseCylindersAsync(cancellationToken);
        _work.RequireCurrentJob(job);
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
            _work.RequireCurrentJob(job);
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
