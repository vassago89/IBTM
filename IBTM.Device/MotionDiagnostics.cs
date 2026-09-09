using System;
using System.IO;

namespace IBTM.Device;

// Read-only monitoring, independent of motion initialization and command admission.
public interface IMotionDiagnostics
{
    AxisState ReadDiagnosticState(MotionAxis axis);
    double ReadDiagnosticPosition(MotionAxis axis);
}

public sealed record MotionDiagnosticSnapshot(AxisState? State, double? Position, Exception? ReadError);

public sealed class MotionDiagnostics
{
    private MotionDiagnosticSnapshot _snapshot = new(null, null, null);
    public MotionDiagnosticSnapshot Snapshot => System.Threading.Volatile.Read(ref _snapshot);

    internal void Invalidate(Exception error) =>
        System.Threading.Volatile.Write(ref _snapshot, new(null, null, error));

    public void Refresh(IMotionDiagnostics feedback, MotionAxis axis)
    {
        AxisState? state = null;
        double? position = null;
        Exception? error = null;
        try { state = feedback.ReadDiagnosticState(axis); }
        catch (Exception exception) when (IsReadFailure(exception)) { error = exception; }
        // A status-query failure must not hide a readable position (or another axis).
        try { position = feedback.ReadDiagnosticPosition(axis); }
        catch (Exception exception) when (IsReadFailure(exception)) { error ??= exception; }
        System.Threading.Volatile.Write(ref _snapshot, new(state, position, error));
    }

    private static bool IsReadFailure(Exception error) => error is IOException
        or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException;
}
