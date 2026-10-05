using IBTM.Core;

namespace IBTM.Device;

// Decoded torque samples with the fastening result they belong to.
public sealed record AdcTorqueCurve(
    long ReceivedAt, int SampleMilliseconds, double[] Torques,
    ushort FasteningMilliseconds, double TargetTorque, double FinalTorque,
    ushort ScrewCount, ushort ErrorCode)
{
    public bool Matches(BoltControllerData result, double? torque)
    {
        return FasteningMilliseconds == result.FasteningTimeMilliseconds
            && TargetTorque == result.TargetTorque && FinalTorque == torque
            && ScrewCount == result.ScrewCount && ErrorCode == result.ErrorCode;
    }
}
