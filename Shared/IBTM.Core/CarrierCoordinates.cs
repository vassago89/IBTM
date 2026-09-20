using System;

namespace IBTM.Core;

public static class CarrierCoordinates
{
    public static bool IsDefined(AxisPosition? first, AxisPosition? second)
    {
        return first is not null && second is not null;
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
                "Record the camera and fastening head Upper/Lower reference positions before converting bolt positions.");
        }

        var sourceCenterX = (sourceUpperLeftLocatingPin.X + sourceLowerRightLocatingPin.X) / 2;
        var sourceCenterY = (sourceUpperLeftLocatingPin.Y + sourceLowerRightLocatingPin.Y) / 2;
        var targetCenterX = (targetUpperLeftLocatingPin.X + targetLowerRightLocatingPin.X) / 2;
        var targetCenterY = (targetUpperLeftLocatingPin.Y + targetLowerRightLocatingPin.Y) / 2;
        // Restore the camera XY from the stored Upper-relative position, then add the center offset.
        var cameraPosition = ToMachine(position, sourceUpperLeftLocatingPin);
        return new AxisPosition
        {
            X = cameraPosition.X + targetCenterX - sourceCenterX,
            Y = cameraPosition.Y + targetCenterY - sourceCenterY,
            Z = position.Z,
        };
    }
}
