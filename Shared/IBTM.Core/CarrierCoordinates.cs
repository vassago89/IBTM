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

    public static AxisPosition ToMachine(
        AxisPosition cameraPosition,
        AxisPosition sourceUpperLeftLocatingPin,
        AxisPosition sourceLowerRightLocatingPin,
        AxisPosition targetUpperLeftLocatingPin,
        AxisPosition targetLowerRightLocatingPin)
    {
        if (!IsDefined(sourceUpperLeftLocatingPin, sourceLowerRightLocatingPin)
            || !IsDefined(targetUpperLeftLocatingPin, targetLowerRightLocatingPin))
        {
            throw new InvalidOperationException(
                "Record two distinct Upper/Lower reference positions for the camera and fastening head before converting bolt positions.");
        }

        var sourceX = sourceLowerRightLocatingPin.X - sourceUpperLeftLocatingPin.X;
        var sourceY = sourceLowerRightLocatingPin.Y - sourceUpperLeftLocatingPin.Y;
        var targetX = targetLowerRightLocatingPin.X - targetUpperLeftLocatingPin.X;
        var targetY = targetLowerRightLocatingPin.Y - targetUpperLeftLocatingPin.Y;
        var rotation = Math.Atan2(targetY, targetX) - Math.Atan2(sourceY, sourceX);
        var cosine = Math.Cos(rotation);
        var sine = Math.Sin(rotation);
        var relativeX = cameraPosition.X - sourceUpperLeftLocatingPin.X;
        var relativeY = cameraPosition.Y - sourceUpperLeftLocatingPin.Y;
        return new AxisPosition
        {
            X = targetUpperLeftLocatingPin.X + cosine * relativeX - sine * relativeY,
            Y = targetUpperLeftLocatingPin.Y + sine * relativeX + cosine * relativeY,
            Z = cameraPosition.Z,
        };
    }
}
