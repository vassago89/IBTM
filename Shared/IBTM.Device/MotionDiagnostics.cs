using System;
using System.ComponentModel;
using System.IO;

namespace IBTM.Device;
// Read-only monitoring, independent of motion initialization and command admission.
public interface IMotionDiagnostics
{
    AxisState ReadDiagnosticState(MotionAxis axis);
    double ReadDiagnosticPosition(MotionAxis axis);
}

public sealed record MotionDiagnosticSnapshot(AxisState? State, double? Position, Exception? ReadError)
{
    public AxisCondition Condition
    {
        get
        {
            return AxisStatus.GetCondition(State);
        }
    }

    public bool? Faulted
    {
        get
        {
            return State is { } state ? state.Alarm || state.Emergency : null;
        }
    }
}

public sealed class MotionDiagnostics : INotifyPropertyChanged
{
    private MotionDiagnosticSnapshot _snapshot = new(null, null, null);
    public MotionDiagnosticSnapshot Snapshot
    {
        get
        {
            return System.Threading.Volatile.Read(ref _snapshot);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal void Invalidate(Exception error)
    {
        Update(new(null, null, error));
    }

    private void Update(MotionDiagnosticSnapshot snapshot)
    {
        if (Snapshot == snapshot)
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
            state = feedback.ReadDiagnosticState(axis);
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            error = exception;
        }

        // A status-query failure must not hide a readable position (or another axis).
        try
        {
            position = feedback.ReadDiagnosticPosition(axis);
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            error ??= exception;
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
