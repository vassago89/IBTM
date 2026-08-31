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
        AxisPos origin) => new()
        {
            X = position.X - origin.X,
            Y = position.Y - origin.Y,
            Z = position.Z,
        };

    public static AxisPos ToMachine(
        AxisPos position,
        AxisPos origin) => new()
        {
            X = origin.X + position.X,
            Y = origin.Y + position.Y,
            Z = position.Z,
        };

    public static AxisPos ToMachine(
        AxisPos position,
        AxisPos sourceUpperLeftPin,
        AxisPos sourceLowerRightPin,
        AxisPos targetUpperLeftPin,
        AxisPos targetLowerRightPin)
    {
        var (cosine, sine) = Rotation(
            sourceUpperLeftPin,
            sourceLowerRightPin,
            targetUpperLeftPin,
            targetLowerRightPin);
        return new AxisPos
        {
            X = targetUpperLeftPin.X
                + (cosine * position.X)
                - (sine * position.Y),
            Y = targetUpperLeftPin.Y
                + (sine * position.X)
                + (cosine * position.Y),
            Z = position.Z,
        };
    }

    private static (double Cosine, double Sine) Rotation(
        AxisPos sourceUpperLeftPin,
        AxisPos sourceLowerRightPin,
        AxisPos targetUpperLeftPin,
        AxisPos targetLowerRightPin)
    {
        var sourceX = sourceLowerRightPin.X - sourceUpperLeftPin.X;
        var sourceY = sourceLowerRightPin.Y - sourceUpperLeftPin.Y;
        var sourceLength = Math.Sqrt(
            (sourceX * sourceX) + (sourceY * sourceY));
        var targetX = targetLowerRightPin.X - targetUpperLeftPin.X;
        var targetY = targetLowerRightPin.Y - targetUpperLeftPin.Y;
        var targetLength = Math.Sqrt(
            (targetX * targetX) + (targetY * targetY));
        sourceX /= sourceLength;
        sourceY /= sourceLength;
        targetX /= targetLength;
        targetY /= targetLength;
        return (
            (sourceX * targetX) + (sourceY * targetY),
            (sourceX * targetY) - (sourceY * targetX));
    }
}
