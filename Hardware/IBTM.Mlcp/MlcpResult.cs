using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace IBTM.Mlcp;

public sealed record MlcpMeasurement(decimal Value, bool? Success);

public sealed record MlcpResult(
    string Header,
    IReadOnlyList<MlcpMeasurement> Measurements,
    string RawFrame)
{
    // POS1 can also contain a start number or model name instead of a verdict.
    public bool? Success => Header == "OK" ? true : Header == "NG" ? false : null;

    public static MlcpResult Parse(ReadOnlySpan<byte> frame)
    {
        // ML-CP manual 2026, p.20: ENQ ... ETX @@ CR LF.
        // The manual does not define the two bytes marked @@ as a checksum.
        if (frame.Length < 8 || frame[0] != 0x05 || frame[^5] != 0x03
            || frame[^2] != 0x0D || frame[^1] != 0x0A)
            throw new InvalidDataException("Invalid LVDT ASCII frame: expected ENQ ... ETX, two trailer bytes, CR LF.");
        for (var index = 1; index < frame.Length - 2; index++)
        {
            if (index != frame.Length - 5 && (frame[index] < 0x20 || frame[index] > 0x7E))
                throw new InvalidDataException("LVDT returned non-ASCII data. Set Serial Type to ASCII.");
        }

        var body = Encoding.ASCII.GetString(frame[1..^5]);
        // The example on p.20 has a comma before ETX; the format on p.17 does not.
        if (body.EndsWith(','))
            body = body[..^1];
        var fields = body.Split(',');
        if (fields.Length < 3 || string.IsNullOrWhiteSpace(fields[0])
            || !int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var count)
            || count is < 1 or > 8)
            throw new InvalidDataException("Invalid LVDT header or data count. Set POS2 to Data Number.");

        var groupResults = fields.Length == 2 + count * 2;
        if (!groupResults && fields.Length != 2 + count)
            throw new InvalidDataException($"LVDT data count {count} does not match the received fields.");

        var measurements = new MlcpMeasurement[count];
        for (var index = 0; index < count; index++)
        {
            var fieldIndex = 2 + index * (groupResults ? 2 : 1);
            if (!decimal.TryParse(fields[fieldIndex].AsSpan().Trim(),
                    NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture, out var value))
                throw new InvalidDataException($"Invalid LVDT measurement {index + 1}: '{fields[fieldIndex]}'.");
            bool? success = null;
            if (groupResults)
            {
                switch (fields[fieldIndex + 1])
                {
                    case "OK": success = true; break;
                    case "NG": success = false; break;
                    default:
                        throw new InvalidDataException($"Invalid LVDT group result: '{fields[fieldIndex + 1]}'.");
                }
            }
            measurements[index] = new(value, success);
        }

        return new(fields[0], Array.AsReadOnly(measurements), Encoding.ASCII.GetString(frame));
    }
}
