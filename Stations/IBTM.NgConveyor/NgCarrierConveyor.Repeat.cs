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
            while (Step is not NgConveyorState.ReadyToEject || Io.GetOutput(OutputIo.NgConveyorRun)
                || !Io.GetInput(InputIo.NgConveyorPosition1Occupied) || Io.GetInput(InputIo.NgShuttleCarrierDetected)
                || ShuttleLift != StationCylinderState.Up || !IsTransferClear)
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
                try
                {
                    await Task.WhenAll(conveyor, end);
                }
                catch (OperationCanceledException) when (forward.IsCancellationRequested) { }
            }
            cancellationToken.ThrowIfCancellationRequested();
            await ReturnFromConveyorAsync(cancellationToken);
        }
    }

    public async Task ReturnFromConveyorAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_ejectionPhase != EjectionPhase.Idle || Volatile.Read(ref _ejectRequested) != 0)
            throw new InvalidOperationException("Complete NG carrier ejection before returning the conveyor carrier.");
        if (!IsTransferClear)
            throw new InvalidOperationException("Release the NG transfer and raise the open pickup before returning the conveyor carrier.");
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var motor = new ConveyorRun(
            Io, OutputIo.NgConveyorRun, operation.Token,
            OutputIo.NgCarrierEjectLamp, OutputIo.NgCarrierEjectCompleteLamp);
        void CheckPickup()
        {
            if (!IsTransferClear)
                OperationCancellation.CancelIfNotDisposed(operation);
        }

        _transfer!.Changed += CheckPickup;
        try
        {
            BeginRun();
            EnterStep(NgConveyorState.ReturningToShuttle);
            CheckPickup();
            operation.Token.ThrowIfCancellationRequested();
            if (!Io.GetInput(InputIo.NgShuttleCarrierDetected))
                await SetShuttleDownAsync(true, operation.Token);
            if (ShuttleLift != StationCylinderState.Down && !Io.GetInput(InputIo.NgShuttleCarrierDetected))
                throw new InvalidOperationException("Lower the NG shuttle before returning the carrier.");

            await Io.SetOutputAndWaitAsync(OutputIo.NgConveyorStopperUp, false, operation.Token);
            _movement = Movement.ReturningToShuttle;
            await RunUntilAsync(InputIo.NgShuttleCarrierDetected, true, operation.Token);
            await SetShuttleDownAsync(false, operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            _movement = Movement.None;
        }
        catch (Exception exception)
        {
            motor.Failure = exception;
        }
        finally
        {
            _transfer!.Changed -= CheckPickup;
            try
            {
                motor.Dispose();
            }
            finally
            {
                _movement = Movement.None;
                EndRun(cancellationToken);
            }
        }
    }
}
