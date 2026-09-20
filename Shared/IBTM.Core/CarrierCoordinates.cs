using System;

namespace IBTM.Core;

public static class CarrierCoordinates
{
    public static bool IsDefined(AxisPosition? first, AxisPosition? second)
    {
        return first is not null
            && second is not null
            && first.X != second.X
            && first.Y != second.Y;
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
        if (!IsDefined(sourceUpperLeftLocatingPin, sourceLowerRightLocatingPin)
            || !IsDefined(targetUpperLeftLocatingPin, targetLowerRightLocatingPin))
        {
            throw new InvalidOperationException(
                "Record Upper/Lower reference positions with different X and Y coordinates before converting bolt positions.");
        }

        var scaleX = (targetLowerRightLocatingPin.X - targetUpperLeftLocatingPin.X)
            / (sourceLowerRightLocatingPin.X - sourceUpperLeftLocatingPin.X);
        var scaleY = (targetLowerRightLocatingPin.Y - targetUpperLeftLocatingPin.Y)
            / (sourceLowerRightLocatingPin.Y - sourceUpperLeftLocatingPin.Y);
        // Recorded bolt XY is already relative to the Inspection Upper reference.
        return new AxisPosition
        {
            X = targetUpperLeftLocatingPin.X + position.X * scaleX,
            Y = targetUpperLeftLocatingPin.Y + position.Y * scaleY,
            Z = position.Z,
        };
    }
}
