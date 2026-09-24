using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.Conveyor;

public sealed partial class MainConveyor
{
    private void SetSmemaOutput(OutputIo output, bool value)
    {
        // The selector contact is ON in teaching/manual mode; direct OUTPUTS remain available.
        if (!_io.GetInput(InputIo.AutoMode))
            _io.SetOutput(output, value);
    }

    public async Task RunMotorAsync(CancellationToken cancellationToken = default)
    {
        using var runCancellation = BeginConveyorOperation(cancellationToken);
        cancellationToken = runCancellation.Token;
        using var motor = new ConveyorRun(_io, OutputIo.MainConveyorRun, cancellationToken, OutputIo.MainConveyorReadyToFront2, OutputIo.MainConveyorAvailableToRear);
        try
        {
            StartMotor(cancellationToken);
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            motor.Failure = exception;
        }
    }

    private OperationCancellation.Operation BeginConveyorOperation(CancellationToken cancellationToken)
    {
        Stop();
        var operation = _operations.Link(cancellationToken);
        _runCancellation = operation;
        operation.Disposed += () =>
        {
            if (ReferenceEquals(_runCancellation, operation))
                _runCancellation = null;
        };
        return operation;
    }

    public void Stop()
    {
        var run = _runCancellation;
        _runCancellation = null;
        Exception? failure = null;
        try
        {
            run?.Cancel();
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            StopOutputs(failure,
                OutputIo.MainConveyorRun,
                OutputIo.MainConveyorReadyToFront2,
                OutputIo.MainConveyorAvailableToRear);
        }
    }

    private void StopOutputs(Exception? operationFailure, params OutputIo[] outputs)
    {
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

        if (failures is not null && operationFailure is not null)
            failures.Insert(0, operationFailure);
        if (failures?.Count == 1)
            ExceptionDispatchInfo.Throw(failures[0]);
        if (failures is not null)
            throw new AggregateException("Main conveyor outputs could not all be stopped.", failures);
    }

    private void StartMotor(CancellationToken cancellationToken, bool reverse = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _io.SetOutput(OutputIo.MainConveyorNormalSpeed, true);
        cancellationToken.ThrowIfCancellationRequested();
        _io.SetOutput(OutputIo.MainConveyorForward, !reverse);
        cancellationToken.ThrowIfCancellationRequested();
        _io.SetOutput(OutputIo.MainConveyorRun, true);
    }
}
