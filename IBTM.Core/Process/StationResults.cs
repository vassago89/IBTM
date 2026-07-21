using IBTM.Device;

namespace IBTM.Core.Process;

public sealed record BoltResult(bool Success, double Torque);

public sealed record FiducialResult(
    bool Found,
    double OffsetX,
    double OffsetY);

public sealed record InspectionOutcome(InspectionResult Result, ImageFrame Image);
