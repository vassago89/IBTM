using System;
using System.ComponentModel;
using System.IO;

namespace IBTM.Device;

public sealed class AxisDiagnostics : INotifyPropertyChanged
{
    private AxisDiagnosticSnapshot _snapshot;

    public AxisDiagnostics()
    {
        _snapshot = new(null, null, null);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public AxisDiagnosticSnapshot Snapshot
    {
        get => System.Threading.Volatile.Read(ref _snapshot);
        private set
        {
            if (Snapshot == value)
                return;
            System.Threading.Volatile.Write(ref _snapshot, value);
            PropertyChanged?.Invoke(this, new(nameof(Snapshot)));
        }
    }

    internal void Invalidate(Exception error)
    {
        Snapshot = new(null, null, error);
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

        Snapshot = new(state, position, error);
    }

    private static bool IsReadFailure(Exception error)
    {
        return error is IOException
            or DllNotFoundException
            or EntryPointNotFoundException
            or BadImageFormatException;
    }
}
