using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.NgConveyor;

public sealed partial class NgCarrierConveyor
{
    public Task SetShuttleDownAsync(bool down, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsTransferClear)
            throw new MotionInterlockException("Complete the NG transfer release and raise the open pickup before moving the shuttle.");
        return _io.SetOutputAndWaitAsync(OutputIo.NgShuttleDown, down, cancellationToken);
    }

    public async Task RunMotorAsync(CancellationToken cancellationToken)
    {
        using var motor = new ConveyorRun(_io, OutputIo.NgConveyorRun, cancellationToken, OutputIo.NgCarrierEjectLamp, OutputIo.NgCarrierEjectCompleteLamp);
        try
        {
            StartConveyor(cancellationToken);
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            motor.Failure = exception;
        }
        finally
        {
            _repeat = false;
            _movement = Movement.None;
            _ejectionPhase = EjectionPhase.Idle;
        }
    }

    public void Stop()
    {
        OutputIo[] outputs = [
            OutputIo.NgConveyorRun,
            OutputIo.NgCarrierEjectLamp,
            OutputIo.NgCarrierEjectCompleteLamp,
        ];
        List<Exception>? failures = null;
        foreach (var output in outputs)
        {
            try
            {
                _io.SetOutput(output, false);
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }

        if (failures?.Count == 1)
            ExceptionDispatchInfo.Throw(failures[0]);
        if (failures is not null)
            throw new AggregateException("NG conveyor outputs could not all be stopped.", failures);
    }

    private Task SetStopperDownAsync(bool down, CancellationToken cancellationToken)
    {
        return _io.SetOutputAndWaitAsync(OutputIo.NgConveyorStopperUp, !down, cancellationToken);
    }

    internal async Task RunUntilAsync(
        InputIo destination,
        bool occupied,
        bool reverse,
        CancellationToken cancellationToken)
    {
        if (_io.GetInput(destination) == occupied)
        {
            return;
        }

        Exception? failure = null;
        try
        {
            StartConveyor(cancellationToken, reverse);
            await _io.WaitForInputAsync(destination, occupied, cancellationToken);
            await Task.Delay(5000);

        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            try
            {
                _io.SetOutput(OutputIo.NgConveyorRun, false);
            }
            catch (Exception cleanupFailure) when (failure is not null)
            {
                throw new AggregateException(failure, cleanupFailure);
            }
        }
    }

    private void StartConveyor(CancellationToken cancellationToken, bool reverse = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _io.SetOutput(OutputIo.NgConveyorNormalSpeed, true);
        cancellationToken.ThrowIfCancellationRequested();
        _io.SetOutput(OutputIo.NgConveyorReverse, reverse);
        cancellationToken.ThrowIfCancellationRequested();
        _io.SetOutput(OutputIo.NgConveyorRun, true);
    }
}
