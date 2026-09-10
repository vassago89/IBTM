using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;

namespace IBTM.Inspection;

public sealed class NgTransferDryRun : AutoUnit
{
    private readonly NgCarrierMove _move;

    public NgTransferDryRun(NgCarrierMove move)
    {
        _move = move;
        move.Changed += NotifyChanged;
    }

    public override event Action? Changed;
    // Retain operation intent, not a cached carrier or position, across Stop.
    public NgTransferDestination Destination { get; private set; } = NgTransferDestination.Shuttle;
    public int CompletedTransfers { get; private set; }

    public NgTransferState State
    {
        get
        {
            return _move.State(Destination, canPickUp: true);
        }
    }

    public Task RunAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (State == NgTransferState.Completed)
            Destination = NgCarrierMove.Opposite(Destination);
        NotifyChanged();
        return RunLoopAsync(ExecuteAsync, cancellationToken);
    }

    private Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var state = State;
        switch (state)
        {
            case NgTransferState.Completed:
                CompletedTransfers++;
                break;
            case NgTransferState.WaitingForCarrier when _move.CarrierPresent(Destination):
                break;
            default:
                return _move.ExecuteAsync(Destination, state, cancellationToken) ?? WaitForChangeAsync(
                    cancellationToken);
        }

        Destination = NgCarrierMove.Opposite(Destination);
        NotifyChanged();
        return Task.CompletedTask;
    }

    private void NotifyChanged()
    {
        Changed?.Invoke();
    }
}
