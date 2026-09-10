using System;
using System.Buffers.Binary;

namespace IBTM.Device;

public static class AdcRtuFrame
{
    public static byte[] Build(byte slaveAddress, AdcFunctionCode function, ReadOnlySpan<byte> data)
    {
        var frame = new byte[data.Length + 4];
        frame[0] = slaveAddress;
        frame[1] = (byte)function;
        data.CopyTo(frame.AsSpan(2));
        BinaryPrimitives.WriteUInt16LittleEndian(
            frame.AsSpan(frame.Length - 2),
            CalculateCrc(frame.AsSpan(0, frame.Length - 2)));
        return frame;
    }

    public static ushort CalculateCrc(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (ushort)((crc & 1) == 1 ? (crc >> 1) ^ 0xA001 : crc >> 1);
            }
        }

        return crc;
    }
}
