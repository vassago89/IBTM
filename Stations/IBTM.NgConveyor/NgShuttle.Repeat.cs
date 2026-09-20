using System;
using System.Threading;
using System.Threading.Tasks;

namespace IBTM.NgConveyor;

public sealed partial class NgShuttle
{
    public async Task RunRepeatAsync(bool useConveyor, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!useConveyor)
            {
                await WaitForCarrierAsync(cancellationToken);
                await CycleAsync(cancellationToken);
                continue;
            }
            using var forward = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var shuttle = RunAsync(forward.Token);
            var conveyor = _conveyor.RunAsync(forward.Token, repeat: true);
            var end = _conveyor.WaitForRepeatEndAsync(forward.Token);
            try
            {
                var completed = await Task.WhenAny(shuttle, conveyor, end);
                await completed;
            }
            finally
            {
                forward.Cancel();
                await Task.WhenAll(shuttle, conveyor, end);
            }
            cancellationToken.ThrowIfCancellationRequested();
            await ReturnFromConveyorAsync(cancellationToken);
        }
    }

    public async Task CycleAsync(CancellationToken cancellationToken)
    {
        if (!CarrierDetected || !_transfer.IsRaised)
        {
            throw new InvalidOperationException("Shuttle repeat requires a carrier on the shuttle and the NG pickup raised.");
        }

        await SetDownAsync(true, cancellationToken);

        if (!CarrierDetected || !_transfer.IsRaised)
        {
            throw new InvalidOperationException("Shuttle repeat lost its carrier or raised pickup feedback before ascent.");
        }

        await SetDownAsync(false, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
    }

    public async Task ReturnFromConveyorAsync(CancellationToken cancellationToken)
    {
        if (!_transfer.IsRaised)
            throw new InvalidOperationException("Raise the NG pickup before returning the conveyor carrier.");
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        void CheckPickup()
        {
            if (!_transfer.IsRaised)
                operation.Cancel();
        }

        _transfer.Changed += CheckPickup;
        Exception? failure = null;
        try
        {
            CheckPickup();
            operation.Token.ThrowIfCancellationRequested();
            if (!CarrierDetected)
                await SetDownAsync(true, operation.Token);
            await _conveyor.ReturnToShuttleAsync(operation.Token);
            await SetDownAsync(false, operation.Token);
            operation.Token.ThrowIfCancellationRequested();
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            _transfer.Changed -= CheckPickup;
            try
            {
                _conveyor.Stop();
            }
            catch (Exception cleanupFailure) when (failure is not null)
            {
                throw new AggregateException(failure, cleanupFailure);
            }
        }
    }
}
