using System.Collections.Generic;
using System.IO;
using IBTM.Core;

namespace IBTM.Device;

// Decoded torque samples with the fastening result they belong to.
public sealed record AdcTorqueCurve(
    long ReceivedAt, int SampleMilliseconds, double[] Torques,
    ushort FasteningMilliseconds, double TargetTorque, double FinalTorque,
    ushort ScrewCount, ushort ErrorCode)
{
    // A full ADC buffer contains the last 200 points of the fastening.
    public int StartMilliseconds => Torques.Length == 200
        ? FasteningMilliseconds - Torques.Length * SampleMilliseconds : 0;

    public static AdcTorqueCurve FromRegisters(IReadOnlyList<int> values, long receivedAt)
    {
        // 15 result registers followed by the samples for each channel.
        if (values.Count < 15)
            throw new InvalidDataException("ADC graph metadata is incomplete.");
        var sampleMilliseconds = values[2] switch
        {
            1 => 5,
            2 => 10,
            3 => 15,
            4 => 30,
            _ => throw new InvalidDataException($"ADC graph sampling code: {values[2]}."),
        };
        var length = values[4];
        var offset = values[0] == 1 ? 15 : values[1] == 1 && values[0] != 8 ? 15 + length : -1;
        if (offset < 0 || length <= 0 || values.Count < offset + length)
            throw new InvalidDataException("ADC graph has no complete torque/time channel.");
        var torques = new double[length];
        for (var index = 0; index < length; index++)
            torques[index] = values[offset + index] / 100.0;
        return new(receivedAt, sampleMilliseconds, torques,
            unchecked((ushort)values[5]), values[6] / 100.0, values[7] / 100.0,
            unchecked((ushort)values[13]), unchecked((ushort)values[12]));
    }

    public bool Matches(BoltControllerData result, double? torque)
    {
        return FasteningMilliseconds == result.FasteningTimeMilliseconds
            && TargetTorque == result.TargetTorque && FinalTorque == torque
            && ScrewCount == result.ScrewCount && ErrorCode == result.ErrorCode;
    }
}
