using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IBTM.BoltFeeder;
using IBTM.Core;
using IBTM.Device;
using IBTM.Storage;

namespace IBTM.BoltFastening;

public sealed class BoltFasteningStation : AutoUnit
{
    private readonly BoltFasteningGantry _gantry;
    private readonly BoltFasteningWork _work;
    private readonly PickupBoltFeeder _pickupFeeder;
    private readonly ShootingBoltFeeder _shootingFeeder;
    private readonly RecipeManager _recipes;
    private readonly UnitSettings _units;
    private HeatSinkSlot[]? _runTargets;
    // The result belongs to this bolt until collection or removal of its carrier.
    private PendingFastening? _pendingFastening;
    // Command history when pickup confirmation is disabled, not a loaded-bolt state.
    private PickupAttempt? _pickupAttempt;

    public BoltFasteningStation(
        BoltFasteningGantry gantry,
        BoltFasteningWork work,
        PickupBoltFeeder pickupFeeder,
        ShootingBoltFeeder shootingFeeder,
        RecipeManager recipes,
        UnitSettings units)
    {
        _gantry = gantry;
        _work = work;
        _pickupFeeder = pickupFeeder;
        _shootingFeeder = shootingFeeder;
        _recipes = recipes;
        _units = units;
    }

    public override event Action? Changed
    {
        add
        {
            _work.Changed += value;
            _gantry.Changed += value;
            _pickupFeeder.Changed += value;
            _shootingFeeder.Changed += value;
        }

        remove
        {
            _work.Changed -= value;
            _gantry.Changed -= value;
            _pickupFeeder.Changed -= value;
            _shootingFeeder.Changed -= value;
        }
    }

    public bool HasPendingResult => PendingResult is not null;

    private PendingFastening? PendingResult
    {
        get
        {
            var pending = _pendingFastening;
            return pending is not null
                && _work.Station.CarrierPresent
                && ReferenceEquals(_work.CurrentJob, pending.Job)
                && _work.Assemblies.Contains(pending.Assembly)
                ? pending
                : null;
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
                : Enum.GetValues<HeatSinkSlot>().Where(_work.Station.IsHeatSinkPresent);
        }
    }

    public void DiscardRemovedCarrierResults()
    {
        if (_work.Station.CarrierPresent)
            throw new InvalidOperationException("Results still belong to the current fastening carrier.");
        if (_pendingFastening is { } pending)
            TraceStep(BoltFasteningState.Waiting, target: $"removed carrier: {pending.Bolt}", workId: pending.Job.Id);
        _pendingFastening = null;
        _pickupAttempt = null;
        _gantry.DiscardPendingResults();
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        Exception? failure = null;
        try
        {
            BeginRun();
            try
            {
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
                    await RunCarrierAsync(cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            finally
            {
                EndRun(cancellationToken);
            }
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            _pickupAttempt = null;
            if (_work.Enabled)
                _gantry.StopShooting(failure);
        }
    }

    private async Task RunCarrierAsync(CancellationToken cancellationToken)
    {
        if (_pendingFastening is not null
            && PendingResult is null)
        {
            _pendingFastening = null;
            _gantry.DiscardPendingResults();
        }

        if (_work.State != BoltFasteningWorkState.ReadyToFasten)
        {
            var state = GetState();
            TraceStep(state, GetActiveBolt(state)?.ToString(), _work.CurrentJob.Id);
            await ExecuteAsync(state, cancellationToken);
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
            while (_work.State == BoltFasteningWorkState.ReadyToFasten)
            {
                carrierOperation.Token.ThrowIfCancellationRequested();
                if (PendingResult is { } pending)
                {
                    var head = _gantry.GetHead(pending.Bolt.Head);
                    var result = await head.ReadPendingResultAsync(carrierOperation.Token);
                    if (result is not null)
                    {
                        RecordResult(pending, result);
                        continue;
                    }
                }

                var state = GetState();
                TraceStep(state, GetActiveBolt(state)?.ToString(), _work.CurrentJob.Id);
                await ExecuteAsync(state, carrierOperation.Token);
            }
        }
        catch (OperationCanceledException) when (carrierOperation.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            _work.Changed -= CheckCarrier;
            _runTargets = null;
            if (_pendingFastening is { } pending
                && !_gantry.GetHead(pending.Bolt.Head).HasPendingResult)
            {
                _pendingFastening = null;
            }
        }
    }

    private async Task ExecuteAsync(
        BoltFasteningState state,
        CancellationToken cancellationToken)
    {
        switch (state)
        {
            case BoltFasteningState.MovingToStandby:
                await _gantry.RaiseCylindersAsync(cancellationToken);
                await _gantry.MoveToBoltAsync(StandbyBolt!, cancellationToken, atTravelZ: true);
                await _gantry.SetPickupTableDownAsync(false, cancellationToken);
                break;
            case BoltFasteningState.WaitingForShootingFeeder:
                await _gantry.WaitForBoltSupplyAsync(FasteningHead.Shooting, cancellationToken);
                break;
            case BoltFasteningState.WaitingForPickupFeeder:
                await _gantry.WaitForBoltSupplyAsync(FasteningHead.Pickup, cancellationToken);
                break;
            case BoltFasteningState.FasteningPcb:
            {
                var bolt = PendingResult?.Bolt ?? PendingPcbBolts.First();
                var feeding = _units.IsBoltFeederEnabled(FasteningHead.Shooting);
                if (_gantry.PickupTablePosition != BoltCylinderState.Up)
                {
                    await _gantry.RaiseCylindersAsync(cancellationToken);
                    await _gantry.MoveToSafeZAsync(cancellationToken);
                    await _gantry.SetPickupTableDownAsync(false, cancellationToken);
                }
                if (_gantry.ShootingHeadPosition != BoltCylinderState.Up
                    && (!_gantry.IsAt(bolt) || feeding && !_gantry.ShootingBoltLoaded))
                    await ClearHeadAsync(FasteningHead.Shooting, cancellationToken);
                if (feeding && _gantry.ShootingTubeBoltDetected)
                    await _gantry.WaitForShootingTubeClearAsync(cancellationToken);
                if (!_gantry.IsAt(bolt))
                    await MoveToBoltAsync(bolt, cancellationToken);

                if (feeding && PendingResult is null && !_gantry.ShootingBoltLoaded)
                {
                    if (_gantry.ShootingEscape == BoltEscapeState.Backward)
                        await _gantry.WaitForBoltSupplyAsync(FasteningHead.Shooting, cancellationToken);
                    // Late head feedback can complete feeding while waiting for the feeder.
                    if (_gantry.ShootingTubeBoltDetected)
                        await _gantry.WaitForShootingTubeClearAsync(cancellationToken);
                    if (!_gantry.ShootingBoltLoaded)
                    {
                        if (_gantry.ShootingHeadPosition != BoltCylinderState.Up)
                            await _gantry.RaiseCylindersAsync(cancellationToken);
                        if (_gantry.ShootingEscape != BoltEscapeState.Forward)
                            await _gantry.SetShootingEscapeForwardAsync(true, cancellationToken);
                        await _gantry.ShootBoltAsync(cancellationToken);
                    }
                }
                if (feeding)
                {
                    await _gantry.WaitForShootingTubeClearAsync(cancellationToken);
                    if (_gantry.ShootingEscape != BoltEscapeState.Backward)
                        await _gantry.SetShootingEscapeForwardAsync(false, cancellationToken);
                }
                await FastenAsync(FasteningHead.Shooting, cancellationToken);
                await ClearHeadAsync(FasteningHead.Shooting, cancellationToken);
                break;
            }
            case BoltFasteningState.FasteningPickup:
            {
                var bolt = PendingResult?.Bolt ?? PendingPickupBolts.First();
                if (_gantry.ShootingHeadPosition != BoltCylinderState.Up)
                    await ClearHeadAsync(FasteningHead.Shooting, cancellationToken);
                if (_gantry.PickupTablePosition != BoltCylinderState.Down)
                {
                    await _gantry.RaiseCylindersAsync(cancellationToken);
                    await _gantry.MoveToSafeZAsync(cancellationToken);
                    await _gantry.SetPickupTableDownAsync(true, cancellationToken);
                }

                var feeding = _units.IsBoltFeederEnabled(FasteningHead.Pickup);
                var pickupAttempted = _pickupAttempt is { } attempt
                    && ReferenceEquals(attempt.Job, _work.CurrentJob)
                    && attempt.Bolt == bolt;
                if (PendingResult is null && (feeding ? !_gantry.PickupBoltLoaded : !pickupAttempted))
                {
                    await _gantry.MoveToPickupXYAsync(cancellationToken);
                    await _gantry.SetHeadDownAsync(FasteningHead.Pickup, true, cancellationToken);
                    await _gantry.MoveToPickupZAsync(cancellationToken);
                    if (feeding)
                        await _gantry.WaitForBoltSupplyAsync(FasteningHead.Pickup, cancellationToken);
                    if (!feeding || !_gantry.PickupBoltLoaded)
                    {
                        var job = _work.CurrentJob;
                        await _gantry.SetVacuumAsync(
                            FasteningHead.Pickup, true, cancellationToken, waitForFeedback: feeding);
                        cancellationToken.ThrowIfCancellationRequested();
                        _work.RequireCurrentJob(job);
                        if (!feeding)
                            _pickupAttempt = new(job, bolt);
                    }
                }
                if (_gantry.IsAtPickupXY())
                {
                    await _gantry.MoveToSafeZAsync(cancellationToken);
                    await _gantry.SetHeadDownAsync(FasteningHead.Pickup, false, cancellationToken);
                }
                if (!_gantry.IsAt(bolt))
                    await MoveToBoltAsync(bolt, cancellationToken);
                await FastenAsync(FasteningHead.Pickup, cancellationToken);
                await ClearHeadAsync(FasteningHead.Pickup, cancellationToken);
                break;
            }
            case BoltFasteningState.CompletingCarrier:
                var completedJob = _work.CurrentJob;
                foreach (var heatSink in Targets)
                    _work.GetAssembly(completedJob, heatSink).CompleteFastening();
                await _gantry.FinishFasteningAsync(FasteningHead.Pickup, cancellationToken);
                await _gantry.FinishFasteningAsync(FasteningHead.Shooting, cancellationToken);
                await _gantry.MoveToSafeZAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                _work.Complete(completedJob);
                break;
            default:
                await WaitForChangeAsync(cancellationToken);
                break;
        }
    }

    public BoltFasteningState GetState(bool live = true)
    {
        switch (true)
        {
            case true when !_work.Enabled:
                return BoltFasteningState.Disabled;
            case true when _work.State != BoltFasteningWorkState.ReadyToFasten:
                var standby = StandbyBolt;
                return standby is not null && _gantry.HasPosition(standby)
                    && (!_gantry.IsHorizontalMoveAllowed || !_gantry.IsAt(standby, live, atTravelZ: true)
                        || _gantry.PickupTablePosition != BoltCylinderState.Up)
                    ? BoltFasteningState.MovingToStandby
                    : BoltFasteningState.Waiting;
            case true when PendingResult is { } pending:
                return pending.Bolt.Head == FasteningHead.Shooting
                    ? BoltFasteningState.FasteningPcb
                    : BoltFasteningState.FasteningPickup;
            case true when PendingPcbBolts.FirstOrDefault() is { } shooting:
                return _units.IsBoltFeederEnabled(FasteningHead.Shooting)
                    && _gantry.PickupTablePosition == BoltCylinderState.Up
                    && _gantry.IsAt(shooting, live)
                    && _gantry.ShootingHeadPosition == BoltCylinderState.Up
                    && !_gantry.ShootingBoltLoaded && !_gantry.ShootingTubeBoltDetected
                    && _gantry.ShootingEscape == BoltEscapeState.Backward
                    && _shootingFeeder.State != BoltFeederState.BoltReady
                    ? BoltFasteningState.WaitingForShootingFeeder
                    : BoltFasteningState.FasteningPcb;
            case true when PendingPickupBolts.Any():
                return _units.IsBoltFeederEnabled(FasteningHead.Pickup)
                    && _gantry.PickupTablePosition == BoltCylinderState.Down
                    && _gantry.IsAtPickupPosition(live)
                    && _gantry.PickupHeadPosition == BoltCylinderState.Down
                    && !_gantry.PickupBoltLoaded && _pickupFeeder.State != BoltFeederState.BoltReady
                    ? BoltFasteningState.WaitingForPickupFeeder
                    : BoltFasteningState.FasteningPickup;
            default:
                return BoltFasteningState.CompletingCarrier;
        }
    }

    public BoltPoint? GetActiveBolt(BoltFasteningState? state = null)
    {
        if (PendingResult is { } pending)
            return pending.Bolt;
        switch (state ?? GetState())
        {
            case BoltFasteningState.MovingToStandby:
                return StandbyBolt;
            case BoltFasteningState.FasteningPcb or BoltFasteningState.WaitingForShootingFeeder:
                return PendingPcbBolts.FirstOrDefault();
            case BoltFasteningState.FasteningPickup or BoltFasteningState.WaitingForPickupFeeder:
                return PendingPickupBolts.FirstOrDefault();
            default:
                return null;
        }
    }

    private async Task FastenAsync(
        FasteningHead fasteningHead,
        CancellationToken cancellationToken)
    {
        var pending = PendingResult;
        if (pending is null)
        {
            var bolt = fasteningHead == FasteningHead.Shooting ? PendingPcbBolts.First() : PendingPickupBolts.First();
            var job = _work.CurrentJob;
            pending = new(bolt, job, _work.GetAssembly(job, bolt.HeatSink));
            await _gantry.GetHead(bolt.Head).SelectPresetAsync(1, cancellationToken);
        }

        _pendingFastening = pending;
        var head = _gantry.GetHead(pending.Bolt.Head);
        // The motor rotates only; the cylinder supplies the forward feed.
        // Raise before a new start, including a retry at the same XY.
        await _gantry.RaiseCylindersAsync(cancellationToken);
        _work.RequireCurrentJob(pending.Job);
        if (!_gantry.IsAt(pending.Bolt))
            throw new InvalidOperationException("The head must be at the bolt's fastening XYZ before starting.");

        using var fastening = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        void CheckPickupTable()
        {
            if (pending.Bolt.Head == FasteningHead.Shooting
                && _gantry.PickupTablePosition != BoltCylinderState.Up)
                fastening.Cancel();
        }

        _gantry.Changed += CheckPickupTable;
        try
        {
            CheckPickupTable();
            var completed = await head.TightenAsync(fastening.Token, LowerHeadWhileFasteningAsync);
            RecordResult(pending, completed);
        }
        catch (OperationCanceledException) when (fastening.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested)
        {
            throw new MotionInterlockException("Keep the pickup table raised during shooting fastening.");
        }
        finally
        {
            _gantry.Changed -= CheckPickupTable;
        }

        Task LowerHeadWhileFasteningAsync(CancellationToken token)
        {
            return _gantry.SetHeadDownAsync(pending.Bolt.Head, true, token);
        }
    }

    private void RecordResult(PendingFastening pending, BoltResult result)
    {
        _work.RequireCurrentJob(pending.Job);
        switch (pending.Bolt.Head)
        {
            case FasteningHead.Shooting:
                pending.Assembly.RecordPcbBolt(pending.Bolt.Number, result);
                break;
            case FasteningHead.Pickup:
                pending.Assembly.RecordPickupBolt(pending.Bolt.Number, result);
                _pickupAttempt = null;
                break;
        }

        _pendingFastening = null;
    }

    private async Task MoveToBoltAsync(BoltPoint bolt, CancellationToken cancellationToken)
    {
        await _gantry.RaiseCylindersAsync(cancellationToken);
        await _gantry.MoveToBoltAsync(bolt, cancellationToken);
    }

    private async Task ClearHeadAsync(FasteningHead head, CancellationToken cancellationToken)
    {
        await _gantry.FinishFasteningAsync(head, cancellationToken);
        await _gantry.RaiseCylindersAsync(cancellationToken);
        await _gantry.MoveToSafeZAsync(cancellationToken);
    }

    private IEnumerable<BoltPoint> PendingPcbBolts
    {
        get
        {
            return ApplicableBolts
                .Where(bolt => bolt.Head == FasteningHead.Shooting)
                .Where(bolt => FindAssembly(bolt.HeatSink)?.PcbBoltResults.ContainsKey(bolt.Number) != true);
        }
    }

    private IEnumerable<BoltPoint> PendingPickupBolts
    {
        get
        {
            return ApplicableBolts
                .Where(bolt => bolt.Head == FasteningHead.Pickup)
                .Where(
                    bolt => FindAssembly(bolt.HeatSink)?.PickupBoltResults.ContainsKey(bolt.Number) != true);
        }
    }

    private HeatSinkAssembly? FindAssembly(HeatSinkSlot heatSink)
    {
        return _work.Assemblies.FirstOrDefault(assembly => assembly.HeatSink == heatSink);
    }

    private IEnumerable<BoltPoint> ApplicableBolts
    {
        get
        {
            return _recipes.Current.Pcb
                .BoltPoints
                .Where(bolt => Targets.Contains(bolt.HeatSink))
                .OrderBy(bolt => bolt.HeatSink)
                .ThenBy(bolt => bolt.Number);
        }
    }

    private sealed record PendingFastening(
            BoltPoint Bolt, StationWork.Job Job, HeatSinkAssembly Assembly);

    private sealed record PickupAttempt(StationWork.Job Job, BoltPoint Bolt);
}
