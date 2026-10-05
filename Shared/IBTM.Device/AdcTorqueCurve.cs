using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using IBTM.Core;

namespace IBTM.Device;

// ADC curve output: 15 metadata registers followed by up to 400 samples.
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

    public static AdcTorqueCurve FromFrame(byte[] frame, byte slaveAddress)
    {
        if (frame.Length < 36 || frame[0] != slaveAddress || frame[1] is not (0x64 or 0xC8)
            || BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(2)) != frame.Length - 6
            || BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(^2))
                != AdcRtuFrame.CalculateCrc(frame.AsSpan(0, frame.Length - 2)))
            throw new InvalidDataException("Invalid ADC torque curve frame.");
        var registers = new ushort[(frame.Length - 6) / 2];
        for (var index = 0; index < registers.Length; index++)
            registers[index] = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(4 + index * 2));
        if ((frame.Length - 6) % 2 != 0 || registers[0] != 1 || registers[1] != 0
            || registers[2] is < 1 or > 3 || registers[3] != 1
            || registers[4] is 0 or > 400 || registers.Length != 15 + registers[4])
            throw new InvalidDataException("ADC curve must contain one torque channel with a valid sample count and interval.");
        var torques = new double[registers[4]];
        for (var index = 0; index < torques.Length; index++)
            torques[index] = registers[index + 15] / 100.0;
        return new(Stopwatch.GetTimestamp(), registers[2] * 5, torques,
            registers[5], registers[6] / 100.0, registers[7] / 100.0, registers[13], registers[12]);
    }
}
