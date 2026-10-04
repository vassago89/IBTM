using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace IBTM.Device;

// Owns cancellation and output cleanup only; conveyor sequences stay in their caller.
public sealed class ConveyorRun : IDisposable
{
    private readonly IIoService _io;
    private readonly OutputIo[] _outputs;
    private readonly CancellationTokenRegistration _stopRegistration;
    private Exception? _cancellationFailure;

    public ConveyorRun(
        IIoService io,
        OutputIo motor,
        OutputIo normalSpeed,
        CancellationToken cancellationToken,
        params OutputIo[] otherOutputs)
    {
        _io = io;
        _outputs = [motor, normalSpeed, .. otherOutputs];
        _stopRegistration = cancellationToken.Register(StopMotor);
    }

    public Exception? Failure { get; set; }

    private void StopMotor()
    {
        List<Exception>? failures = null;
        // Stop RUN and its speed selection immediately; sequence outputs clear on disposal.
        foreach (var output in _outputs.AsSpan(0, 2))
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
        _cancellationFailure = failures?.Count switch
        {
            null => null,
            1 => failures[0],
            _ => new AggregateException(failures!),
        };
    }

    public void Dispose()
    {
        // Wait for an in-progress cancellation callback before collecting its error.
        _stopRegistration.Dispose();
        List<Exception> failures = [];
        if (Failure is not null)
            failures.Add(Failure);
        if (_cancellationFailure is not null)
            failures.Add(_cancellationFailure);

        foreach (var output in _outputs)
        {
            try
            {
                _io.SetOutput(output, false);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        if (failures.Count == 1)
            ExceptionDispatchInfo.Throw(failures[0]);
        if (failures.Count > 1)
            throw new AggregateException(failures);
    }
}
