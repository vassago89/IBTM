using System;

namespace IBTM.Device;
// Read-only monitoring, independent of motion initialization and command admission.
public interface IMotionDiagnostics
{
    (AxisState? State, Exception? Error) ReadDiagnosticState(MotionAxis axis);
    (double? Position, Exception? Error) ReadDiagnosticPosition(MotionAxis axis);
}
