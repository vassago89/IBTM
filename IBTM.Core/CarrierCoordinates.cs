using System;

namespace IBTM.Core;

public static class CarrierCoordinates
{
    public static bool IsDefined(AxisPos? first, AxisPos? second) =>
        first is not null
        && second is not null
        && (first.X != second.X || first.Y != second.Y);

    public static AxisPos FromMachine(
        AxisPos position,
        AxisPos upperLeftPin,
        AxisPos lowerRightPin)
    {
        var (cosine, sine) = Direction(upperLeftPin, lowerRightPin);
        var x = position.X - upperLeftPin.X;
        var y = position.Y - upperLeftPin.Y;
        return new AxisPos
        {
            X = (cosine * x) + (sine * y),
            Y = (-sine * x) + (cosine * y),
            Z = position.Z,
        };
    }

    public static AxisPos ToMachine(
        AxisPos position,
        AxisPos upperLeftPin,
        AxisPos lowerRightPin)
    {
        var (cosine, sine) = Direction(upperLeftPin, lowerRightPin);
        return new AxisPos
        {
            X = upperLeftPin.X
                + (cosine * position.X)
                - (sine * position.Y),
            Y = upperLeftPin.Y
                + (sine * position.X)
                + (cosine * position.Y),
            Z = position.Z,
        };
    }

    private static (double Cosine, double Sine) Direction(
        AxisPos upperLeftPin,
        AxisPos lowerRightPin)
    {
        var x = lowerRightPin.X - upperLeftPin.X;
        var y = lowerRightPin.Y - upperLeftPin.Y;
        var length = Math.Sqrt((x * x) + (y * y));
        return (x / length, y / length);
    }
}
