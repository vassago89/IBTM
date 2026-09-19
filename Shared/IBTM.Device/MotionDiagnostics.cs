using System;
using System.ComponentModel;
using System.IO;

namespace IBTM.Device;
// Read-only monitoring, independent of motion initialization and command admission.
public interface IMotionDiagnostics
{
    (AxisState? State, Exception? Error) ReadDiagnosticState(MotionAxis axis);
    (double? Position, Exception? Error) ReadDiagnosticPosition(MotionAxis axis);
}

public sealed record MotionDiagnosticSnapshot(AxisState? State, double? Position, Exception? ReadError)
{
    public AxisCondition Condition => AxisStatus.GetCondition(State);

    public bool? Faulted => State is { } state ? state.Alarm || state.Emergency : null;
}

public sealed class MotionDiagnostics : INotifyPropertyChanged
{
    private MotionDiagnosticSnapshot _snapshot;

    public MotionDiagnostics()
    {
        _snapshot = new(null, null, null);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public MotionDiagnosticSnapshot Snapshot => System.Threading.Volatile.Read(ref _snapshot);

    internal void Invalidate(Exception error)
    {
        Update(new(null, null, error));
    }

    private void Update(MotionDiagnosticSnapshot snapshot)
    {
        var previous = Snapshot;
        if (previous == snapshot)
            return;
        System.Threading.Volatile.Write(ref _snapshot, snapshot);
        PropertyChanged?.Invoke(this, new(nameof(Snapshot)));
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

        Update(new(state, position, error));
    }

    private static bool IsReadFailure(Exception error)
    {
        return error is IOException
            or DllNotFoundException
            or EntryPointNotFoundException
            or BadImageFormatException;
    }
}
