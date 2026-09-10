using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.PcbPlacement;
using IBTM.PcbSupply;

namespace IBTM;

public enum PcbDryRunDirection
{
    [Description("Load one PCB in Supply")]
    Ready,
    [Description("Supply → Heat sink")]
    Forward,
    [Description("Heat sink → Supply")]
    Return,
}

// One actual PCB, stationary carrier. Production pickup/SMEMA loops are not started.
public sealed class PcbDryRun : AutoUnit
{
    private readonly PcbSupplier _supply;
    private readonly PcbPlacer _placement;
    private readonly PcbReturn _return;
    private readonly PcbPlacementWork _work;
    private readonly Recipe _recipe;

    public PcbDryRun(
        PcbSupplier supply,
        PcbPlacer placement,
        PcbReturn returning,
        PcbPlacementWork work,
        Recipe recipe)
    {
        _supply = supply;
        _placement = placement;
        _return = returning;
        _work = work;
        _recipe = recipe;
        supply.Changed += NotifyChanged;
        placement.Changed += NotifyChanged;
    }

    public override event Action? Changed;
    public PcbDryRunDirection Direction { get; private set; }
    public HeatSinkSlot HeatSink { get; private set; }
    public int CompletedCycles { get; private set; }

    public Enum State
    {
        get
        {
            return Direction switch
            {
                PcbDryRunDirection.Ready => PcbDryRunDirection.Ready,
                PcbDryRunDirection.Return => _return.State,
                _
                    => _supply.TransferState switch
                    {
                        PcbSupplyState.WaitingForPlacement
                            or PcbSupplyState.WaitingForBuffer
                            or PcbSupplyState.WaitingForCarrierExit
                            => _placement.State(_recipe.PcbPlacement, HeatSink),
                        var state => state,
                    },
            };
        }
    }

    public Task RunAsync(HeatSinkSlot heatSink, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CheckTarget(Direction == PcbDryRunDirection.Ready ? heatSink : HeatSink);
        if (Direction == PcbDryRunDirection.Ready)
            BeginForward(heatSink);
        return RunLoopAsync(token => ExecuteAsync(heatSink, token), cancellationToken);
    }

    private async Task ExecuteAsync(HeatSinkSlot requestedHeatSink, CancellationToken token)
    {
        if (Direction == PcbDryRunDirection.Forward)
        {
            if (_supply.TransferComplete && _placement.PlacementComplete(HeatSink))
            {
                _return.Begin(HeatSink);
                Direction = PcbDryRunDirection.Return;
                NotifyChanged();
                return;
            }

            await (_supply.TransferStepAsync(token) ?? _placement.PlaceStepAsync(
                _recipe.PcbPlacement,
                HeatSink,
                token) ?? WaitForChangeAsync(token));
            return;
        }

        if (_return.State != PcbReturnState.Completed)
            await _return.RunAsync(HeatSink, token);
        token.ThrowIfCancellationRequested();
        CheckTarget(requestedHeatSink);
        CompletedCycles++;
        BeginForward(requestedHeatSink);
    }

    private void BeginForward(HeatSinkSlot heatSink)
    {
        HeatSink = heatSink;
        _work.PrepareRecovery([(heatSink, false)]);
        Direction = PcbDryRunDirection.Forward;
        NotifyChanged();
    }

    private void CheckTarget(HeatSinkSlot heatSink)
    {
        if (!_work.CarrierSeated || !_work.HeatSinkPresent(heatSink))
            throw new InvalidOperationException("PCB round trip requires a seated carrier and the selected heat sink.");
    }

    private void NotifyChanged()
    {
        Changed?.Invoke();
    }
}
