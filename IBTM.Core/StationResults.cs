using IBTM.Device;

namespace IBTM.Core;

public sealed record BoltResult(bool Success, double Torque);

public sealed record FiducialResult(
    double OffsetX,
    double OffsetY);

public readonly record struct CarrierJigState(
    bool Pcb1Present,
    bool Pcb2Present);

public sealed record PcbInspectionResult(
    InspectionResult Result,
    ImageFrame? Image);

public sealed record CarrierInspectionResult(
    PcbInspectionResult Pcb1,
    PcbInspectionResult Pcb2)
{
    public InspectionResult OverallResult =>
        Pcb1.Result == InspectionResult.Good
        && Pcb2.Result == InspectionResult.Good
            ? InspectionResult.Good
            : InspectionResult.Ng;
}
