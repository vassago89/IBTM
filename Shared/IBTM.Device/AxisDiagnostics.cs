using System;
using System.ComponentModel;
using System.IO;

namespace IBTM.Device;

public sealed record AxisDiagnosticSample(AxisState? State, double? Position, Exception? ReadError)
{
    public AxisCondition Condition => AxisStatus.GetCondition(State);

    public bool? Faulted => State is { } state ? state.Alarm || state.Emergency : null;
}

public sealed class AxisDiagnostics : INotifyPropertyChanged
{
    private AxisDiagnosticSample _sample;

    public AxisDiagnostics()
    {
        _sample = new(null, null, null);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public AxisDiagnosticSample Sample
    {
        get => System.Threading.Volatile.Read(ref _sample);
        private set
        {
            if (Sample == value)
                return;
            System.Threading.Volatile.Write(ref _sample, value);
            PropertyChanged?.Invoke(this, new(nameof(Sample)));
        }
    }

    internal void Invalidate(Exception error)
    {
        Sample = new(null, null, error);
    }

    public void Refresh(IMotionDiagnostics feedback, MotionAxis axis)
    {
        AxisState? state = null;
        double? position = null;
        Exception? error = null;
        try
        {
            (state, error) = feedback.ReadDiagnosticState(axis);
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            error = exception;
        }

        // A status-query failure must not hide a readable position (or another axis).
        try
        {
            var read = feedback.ReadDiagnosticPosition(axis);
            position = read.Position;
            if (read.Error is { } positionError)
                error = error is null ? positionError : new AggregateException(error, positionError);
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            error = error is null
                ? exception
                : new AggregateException(error, exception);
        }

        Sample = new(state, position, error);
    }

    private static bool IsReadFailure(Exception error)
    {
        return error is IOException
            or DllNotFoundException
            or EntryPointNotFoundException
            or BadImageFormatException;
    }
}
