using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.NgConveyor;

public sealed partial class NgCarrierConveyor
{
    public async Task WaitForRepeatEndAsync(CancellationToken cancellationToken)
    {
        var transfer = _transfer
            ?? throw new InvalidOperationException("Connect NG transfer feedback before waiting for Repeat completion.");
        var changed = new AsyncAutoResetEvent();
        Changed += changed.Set;
        StepChanged += changed.Set;
        transfer.Changed += changed.Set;
        try
        {
            while (Step is not NgConveyorState.ReadyToEject || RunCommandOn
                || !Position1Occupied || Position3Occupied
                || ShuttleLift != NgShuttleLiftState.Up || !IsTransferClear)
                await changed.WaitAsync(cancellationToken);
        }
        finally
        {
            Changed -= changed.Set;
            StepChanged -= changed.Set;
            transfer.Changed -= changed.Set;
        }
    }

    public async Task RunRepeatAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var forward = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var conveyor = RunAsync(forward.Token, repeat: true);
            var end = WaitForRepeatEndAsync(forward.Token);
            try
            {
                var completed = await Task.WhenAny(conveyor, end);
                await completed;
            }
            finally
            {
                forward.Cancel();
                await Task.WhenAll(conveyor, end);
            }
            cancellationToken.ThrowIfCancellationRequested();
            await ReturnFromConveyorAsync(cancellationToken);
        }
    }

    public async Task ReturnFromConveyorAsync(CancellationToken cancellationToken)
    {
        if (!IsTransferClear)
            throw new InvalidOperationException("Release the NG transfer and raise the open pickup before returning the conveyor carrier.");
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var motor = new ConveyorRun(
            _io, OutputIo.NgConveyorRun, operation.Token,
            OutputIo.NgCarrierEjectLamp, OutputIo.NgCarrierEjectCompleteLamp);
        void CheckPickup()
        {
            if (!IsTransferClear)
                operation.Cancel();
        }

        _transfer!.Changed += CheckPickup;
        try
        {
            BeginRun();
            EnterStep(NgConveyorState.ReturningToShuttle);
            CheckPickup();
            operation.Token.ThrowIfCancellationRequested();
            if (!Position3Occupied)
                await SetShuttleDownAsync(true, operation.Token);
            if (ShuttleLift != NgShuttleLiftState.Down && !Position3Occupied)
                throw new InvalidOperationException("Lower the NG shuttle before returning the carrier.");

            _movement = Movement.None;
            _ejectionPhase = EjectionPhase.Idle;
            await SetStopperDownAsync(true, operation.Token);
            await RunUntilAsync(InputIo.NgShuttleCarrierDetected, true, true, operation.Token);
            await SetShuttleDownAsync(false, operation.Token);
            operation.Token.ThrowIfCancellationRequested();
        }
        catch (Exception exception)
        {
            motor.Failure = exception;
        }
        finally
        {
            _transfer!.Changed -= CheckPickup;
            EndRun(cancellationToken);
        }
    }
}
