namespace IBTM.Core.Process;

public sealed record BoltResult(bool Success, double Torque, string Message);

public sealed record FiducialResult(
    bool Found,
    double OffsetX,
    double OffsetY,
    double Confidence);

public sealed record InspectionOutcome(InspectionResult Result, ImageFrame Image);
