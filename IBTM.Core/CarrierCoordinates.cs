using System;

namespace IBTM.Core;

public static class CarrierCoordinates
{
    public static bool IsDefined(AxisPosition? first, AxisPosition? second)
    {
        return first is not null
            && second is not null
            && (first.X != second.X || first.Y != second.Y);
    }

    public static AxisPosition FromMachine(AxisPosition position, AxisPosition origin)
    {
        return new()
        {
            X = position.X - origin.X,
            Y = position.Y - origin.Y,
            Z = position.Z,
        };
    }

    public static AxisPosition ToMachine(AxisPosition position, AxisPosition origin)
    {
        return new()
        {
            X = origin.X + position.X,
            Y = origin.Y + position.Y,
            Z = position.Z,
        };
    }

    public static AxisPosition ToMachine(
        AxisPosition position,
        AxisPosition sourceUpperLeftLocatingPin,
        AxisPosition sourceLowerRightLocatingPin,
        AxisPosition targetUpperLeftLocatingPin,
        AxisPosition targetLowerRightLocatingPin)
    {
        var (cosine, sine) = Rotation(
            sourceUpperLeftLocatingPin,
            sourceLowerRightLocatingPin,
            targetUpperLeftLocatingPin,
            targetLowerRightLocatingPin);
        return new AxisPosition
        {
            X = targetUpperLeftLocatingPin.X + (cosine * position.X) - (sine * position.Y),
            Y = targetUpperLeftLocatingPin.Y + (sine * position.X) + (cosine * position.Y),
            Z = position.Z,
        };
    }

    private static (double Cosine, double Sine) Rotation(
        AxisPosition sourceUpperLeftLocatingPin,
        AxisPosition sourceLowerRightLocatingPin,
        AxisPosition targetUpperLeftLocatingPin,
        AxisPosition targetLowerRightLocatingPin)
    {
        var sourceX = sourceLowerRightLocatingPin.X - sourceUpperLeftLocatingPin.X;
        var sourceY = sourceLowerRightLocatingPin.Y - sourceUpperLeftLocatingPin.Y;
        var sourceLength = Math.Sqrt((sourceX * sourceX) + (sourceY * sourceY));
        var targetX = targetLowerRightLocatingPin.X - targetUpperLeftLocatingPin.X;
        var targetY = targetLowerRightLocatingPin.Y - targetUpperLeftLocatingPin.Y;
        var targetLength = Math.Sqrt((targetX * targetX) + (targetY * targetY));
        sourceX /= sourceLength;
        sourceY /= sourceLength;
        targetX /= targetLength;
        targetY /= targetLength;
        return ((sourceX * targetX) + (sourceY * targetY), (sourceX * targetY) - (sourceY * targetX));
    }
}
