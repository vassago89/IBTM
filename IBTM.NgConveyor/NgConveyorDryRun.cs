using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.NgConveyor;

public enum NgConveyorDestination
{
    [Description("Load one carrier")] None,
    [Description("P1")] Position1,
    [Description("Shuttle / P3")] Shuttle,
}

public enum NgConveyorDryRunState
{
    [Description("Load one carrier")] WaitingForCarrier,
    [Description("Raise the NG pickup")] WaitingForPickup,
    [Description("Lowering shuttle")] LoweringShuttle,
    [Description("Preparing stopper")] PreparingStopper,
    [Description("Moving to P1")] MovingToPosition1,
    [Description("Returning to P3")] ReturningToShuttle,
    [Description("Raising shuttle")] RaisingShuttle,
    [Description("Destination reached")] Arrived,
}

public sealed class NgConveyorDryRun : AutoUnit
{
    private readonly NgCarrierConveyor _conveyor;
    private readonly NgShuttle _shuttle;
    private readonly INgCarrierTransferFeedback _pickup;

    public NgConveyorDryRun(NgCarrierConveyor conveyor, NgShuttle shuttle, INgCarrierTransferFeedback pickup)
    {
        _conveyor = conveyor;
        _shuttle = shuttle;
        _pickup = pickup;
        conveyor.Changed += NotifyChanged;
        shuttle.Changed += NotifyChanged;
        pickup.Changed += NotifyChanged;
    }

    public override event Action? Changed;
    public NgConveyorDestination Destination { get; private set; }
    public int CompletedPasses { get; private set; }
    public bool Ready => _pickup.IsRaised;
    public NgConveyorDryRunState State
    {
        get
        {
            if (!Ready) return NgConveyorDryRunState.WaitingForPickup;
            if (Destination == NgConveyorDestination.None) return NgConveyorDryRunState.WaitingForCarrier;
            var arrived = Destination == NgConveyorDestination.Position1
                ? _conveyor.Position1Occupied : _shuttle.Feedback.CarrierDetected;
            if (arrived) return _shuttle.Feedback.Lift == NgShuttleLiftState.Up
                ? NgConveyorDryRunState.Arrived : NgConveyorDryRunState.RaisingShuttle;
            if (_shuttle.Feedback.Lift != NgShuttleLiftState.Down)
                return NgConveyorDryRunState.LoweringShuttle;
            if (Destination == NgConveyorDestination.Position1)
                return !_conveyor.StopperUp ? NgConveyorDryRunState.PreparingStopper
                    : NgConveyorDryRunState.MovingToPosition1;
            return !_conveyor.StopperDown ? NgConveyorDryRunState.PreparingStopper
                : NgConveyorDryRunState.ReturningToShuttle;
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_conveyor.CarrierCount > 1 || Destination == NgConveyorDestination.None && _conveyor.CarrierCount != 1)
            throw new InvalidOperationException("NG conveyor round trip requires exactly one carrier.");
        if (Destination == NgConveyorDestination.None)
            Destination = _conveyor.Position1Occupied ? NgConveyorDestination.Shuttle : NgConveyorDestination.Position1;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        void CheckPickup() { if (!Ready) operation.Cancel(); }
        _pickup.Changed += CheckPickup;
        try
        {
            CheckPickup();
            await RunLoopAsync(ExecuteAsync, operation.Token);
        }
        finally
        {
            _pickup.Changed -= CheckPickup;
            _conveyor.Stop();
        }
    }

    private async Task ExecuteAsync(CancellationToken token)
    {
        switch (State)
        {
            case NgConveyorDryRunState.LoweringShuttle:
                await _shuttle.SetDownAsync(true, token); break;
            case NgConveyorDryRunState.PreparingStopper:
                await _conveyor.SetStopperUpAsync(Destination == NgConveyorDestination.Position1, token); break;
            case NgConveyorDryRunState.MovingToPosition1:
                await _conveyor.RunUntilAsync(InputIo.NgConveyorPosition1Occupied, true, false, token); break;
            case NgConveyorDryRunState.ReturningToShuttle:
                await _conveyor.RunUntilAsync(InputIo.NgShuttleCarrierDetected, true, true, token); break;
            case NgConveyorDryRunState.RaisingShuttle:
                await _shuttle.SetDownAsync(false, token); break;
            case NgConveyorDryRunState.Arrived:
                CompletedPasses++;
                Destination = Destination == NgConveyorDestination.Position1
                    ? NgConveyorDestination.Shuttle : NgConveyorDestination.Position1;
                NotifyChanged();
                break;
            default: await WaitForChangeAsync(token); break;
        }
    }

    private void NotifyChanged() => Changed?.Invoke();
}
