using System;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;

namespace IBTM.BoltFastening;

public enum BoltRouteState
{
    [Description("Fastening gantry is not ready")]
    Unavailable,
    [Description("Waiting for carrier")]
    WaitingForCarrier,
    [Description("Raise the backup plate and lower the stopper")]
    WaitingForSeat,
    [Description("Ready to move")]
    Ready,
    [Description("Raising heads")]
    RaisingHeads,
    [Description("Moving to Safe Z")]
    MovingToSafeZ,
    [Description("Moving to bolt pickup")]
    MovingToPickup,
    [Description("Lowering pickup head")]
    LoweringForPickup,
    [Description("Moving to pickup Z")]
    MovingToPickupZ,
    [Description("At pickup position · No vacuum")]
    AtPickup,
    [Description("Moving to bolt")]
    MovingToBolt,
    [Description("Lowering head at bolt point")]
    LoweringAtPoint,
    [Description("At bolt point · No fastening")]
    AtPoint,
    [Description("Point complete")]
    PointComplete,
}

public enum BoltRouteDirection
{
    [Description("Forward")]
    Forward,
    [Description("Return")]
    Return,
}

// Axis and head-cylinder dry run. No vacuum, shooting, ADC command or production result.
public sealed class BoltRouteDryRun : AutoUnit
{
    private readonly BoltFasteningGantry _gantry;
    private readonly BoltFasteningWork _work;
    private readonly Func<PcbLayout> _getPcb;
    private Target[] _targets = [];
    private Target? _current;
    private Stage _stage;

    public BoltRouteDryRun(BoltFasteningGantry gantry, BoltFasteningWork work, Func<PcbLayout> getPcb)
    {
        _gantry = gantry;
        _work = work;
        _getPcb = getPcb;
        work.Changed += NotifyChanged;
        gantry.Changed += NotifyChanged;
        gantry.Feedback.StateChanged += NotifyChanged;
    }

    public override event Action? Changed;
    public bool Ready
    {
        get
        {
            return _work.CarrierSeated;
        }
    }

    public BoltTarget? ActiveBolt
    {
        get
        {
            return _current?.Bolt;
        }
    }

    public FasteningPass? ActivePass
    {
        get
        {
            return _current?.Pass;
        }
    }

    public BoltRouteDirection Direction { get; private set; }
    public int CompletedPasses { get; private set; }

    public BoltRouteState State
    {
        get
        {
            if (!_work.CarrierPresent)
                return BoltRouteState.WaitingForCarrier;
            if (!Ready)
                return BoltRouteState.WaitingForSeat;
            if (_current is not { } target)
                return BoltRouteState.Ready;
            // The stage records travel intent, not a simulated bolt-present sensor.
            // Pickup and return both pass Safe Z, so feedback alone cannot distinguish them.
            if (_stage == Stage.Stroke)
            {
                var down = target.Bolt.Head == FasteningHead.Pickup
                    ? _gantry.PickupHeadPosition
                    : _gantry.ShootingHeadPosition;
                if (down != BoltCylinderState.Down)
                    return target.Location == Location.Pickup
                        ? BoltRouteState.LoweringForPickup
                        : BoltRouteState.LoweringAtPoint;
                return target.Location == Location.Pickup
                    ? _gantry.AtPickupPosition
                        ? BoltRouteState.AtPickup
                        : BoltRouteState.MovingToPickupZ
                    : BoltRouteState.AtPoint;
            }

            // Leave the pickup with Z first, then raise the cylinder before moving XY.
            if (!_gantry.AtSafeZ)
                return BoltRouteState.MovingToSafeZ;
            if (!_gantry.CanMoveHorizontal)
                return BoltRouteState.RaisingHeads;
            if (_stage == Stage.Return)
                return BoltRouteState.PointComplete;
            return target.Location == Location.Pickup
                ? _gantry.AtPickupXY ? BoltRouteState.LoweringForPickup : BoltRouteState.MovingToPickup
                : _gantry.IsAt(target.Bolt)
                    ? BoltRouteState.LoweringAtPoint
                    : BoltRouteState.MovingToBolt;
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bolts = _getPcb()
            .GetBolts()
            .Where(bolt => _work.HeatSinkPresent(bolt.HeatSink))
            .OrderBy(bolt => bolt.HeatSink)
            .ThenBy(bolt => bolt.Number)
            .ToArray();
        if (bolts.Length == 0)
            throw new InvalidOperationException("Bolt route requires taught bolt points on a detected heat sink.");
        if (bolts.Any(bolt => !_gantry.HasPosition(bolt)))
            throw new InvalidOperationException("Teach the carrier reference, head references and bolt positions before bolt route dry run.");

        var targets = bolts.Where(bolt => bolt.Head == FasteningHead.Shooting)
            .Select(bolt => new Target(bolt, FasteningPass.Pcb, Location.Bolt))
            .Concat(
                bolts.Where(bolt => bolt.Head == FasteningHead.Pickup)
                    .SelectMany(
                        bolt =>
                            new[] { new Target(bolt, FasteningPass.IpmSeating, Location.Pickup), new Target(
                                bolt,
                                FasteningPass.IpmSeating,
                                Location.Bolt), }))
            .Concat(
                bolts.Where(bolt => bolt.Head == FasteningHead.Pickup)
                    .Select(bolt => new Target(bolt, FasteningPass.IpmFinal, Location.Bolt)))
            .ToArray();
        var previous = _current;
        var next = targets.FirstOrDefault(
            target =>
                previous is not null
                    && target.Pass == previous.Pass
                    && target.Location == previous.Location
                    && target.Bolt.HeatSink == previous.Bolt.HeatSink
                    && target.Bolt.Number == previous.Bolt.Number);
        if (next is null)
        {
            Direction = BoltRouteDirection.Forward;
            _stage = Stage.Approach;
        }
        else if (next.Location == Location.Pickup ? !_gantry.AtPickupXY : !_gantry.IsAt(next.Bolt))
        {
            // Manual repositioning while stopped requires approaching the target again.
            _stage = Stage.Approach;
        }

        _targets = targets;
        _current = next ?? targets[0];

        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        void CheckCarrier()
        {
            if (!Ready)
                operation.Cancel();
        }

        _work.Changed += CheckCarrier;
        try
        {
            CheckCarrier();
            NotifyChanged();
            await RunLoopAsync(ExecuteAsync, operation.Token);
        }
        finally
        {
            _work.Changed -= CheckCarrier;
        }
    }

    private async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        switch (State)
        {
            case BoltRouteState.RaisingHeads:
                await _gantry.RaiseCylindersAsync(cancellationToken);
                return;
            case BoltRouteState.MovingToSafeZ:
                await _gantry.MoveToSafeZAsync(cancellationToken);
                return;
            case BoltRouteState.MovingToBolt:
                await _gantry.MoveToBoltAsync(_current!.Bolt, cancellationToken);
                return;
            case BoltRouteState.MovingToPickup:
                await _gantry.MoveToPickupXYAsync(cancellationToken);
                return;
            case BoltRouteState.LoweringForPickup:
            case BoltRouteState.LoweringAtPoint:
                _stage = Stage.Stroke;
                await _gantry.SetHeadDownAsync(_current!.Bolt.Head, true, cancellationToken);
                return;
            case BoltRouteState.MovingToPickupZ:
                await _gantry.MoveToPickupZAsync(cancellationToken);
                return;
            case BoltRouteState.AtPickup:
            case BoltRouteState.AtPoint:
                cancellationToken.ThrowIfCancellationRequested();
                _stage = Stage.Return;
                NotifyChanged();
                return;
            case BoltRouteState.PointComplete:
                cancellationToken.ThrowIfCancellationRequested();
                var index = Array.IndexOf(_targets, _current!);
                if (Direction == BoltRouteDirection.Forward
                    && index == _targets.Length - 1
                    || Direction == BoltRouteDirection.Return
                    && index == 0)
                {
                    CompletedPasses++;
                    Direction = Direction == BoltRouteDirection.Forward
                        ? BoltRouteDirection.Return
                        : BoltRouteDirection.Forward;
                }

                if (_targets.Length > 1)
                    _current = _targets[index + (Direction == BoltRouteDirection.Forward ? 1 : -1)];
                _stage = Stage.Approach;
                NotifyChanged();
                return;
            default:
                await WaitForChangeAsync(cancellationToken);
                return;
        }
    }

    private void NotifyChanged()
    {
        Changed?.Invoke();
    }

    private enum Stage
    {
        Approach,
        Stroke,
        Return
    }

    private enum Location
    {
        Bolt,
        Pickup
    }

    private sealed record Target(BoltTarget Bolt, FasteningPass Pass, Location Location);
}
