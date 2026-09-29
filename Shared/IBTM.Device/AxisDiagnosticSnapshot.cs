using System;

namespace IBTM.Device;

public sealed record AxisDiagnosticSnapshot(AxisState? State, double? Position, Exception? ReadError)
{
    public AxisCondition Condition => AxisStatus.GetCondition(State);

    public bool? Faulted => State is { } state ? state.Alarm || state.Emergency : null;
}
