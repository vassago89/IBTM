using IBTM.Device;

namespace IBTM.Core.Process;

public sealed record BoltResult(bool Success, double Torque);

public sealed record FiducialResult(
    bool Found,
    double OffsetX,
    double OffsetY);

public sealed record InspectionOutcome(InspectionResult Result, ImageFrame Image);

public sealed record CarrierInspectionResult(
    InspectionOutcome Pcb1,
    InspectionOutcome Pcb2)
{
    public InspectionResult Result =>
        Pcb1.Result == InspectionResult.Good
        && Pcb2.Result == InspectionResult.Good
            ? InspectionResult.Good
            : InspectionResult.Ng;
}
