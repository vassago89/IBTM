using System.Collections.Generic;
using System.Linq;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.Inspection;

public sealed class InspectionGantrySettings : Setting
{
    public MotionSettings Motion { get; set; } = new();
    public double CarrierScanOverlapMillimeters { get; set; } = 1.0;

    public TeachingPosition[] GetTeachingPositions(CarrierReferenceSettings reference) =>
    [
        new(TeachingTarget.CarrierUpperLeftLocatingPin, MotionGroup.InspectionGantry, TeachMode.XYOnly,
            () => reference.UpperLeftLocatingPin ?? new(), p => reference.UpperLeftLocatingPin = p,
            reference, () => reference.UpperLeftLocatingPin is not null),
        new(TeachingTarget.CarrierLowerRightLocatingPin, MotionGroup.InspectionGantry, TeachMode.XYOnly,
            () => reference.LowerRightLocatingPin ?? new(), p => reference.LowerRightLocatingPin = p,
            reference, () => reference.LowerRightLocatingPin is not null),
    ];

    public IEnumerable<TeachingPosition> GetBoltTeachingPositions(
        IEnumerable<BoltPoint> bolts, CarrierReferenceSettings reference) =>
        bolts.Select(bolt => new TeachingPosition(
            TeachingTarget.BoltReference, MotionGroup.InspectionGantry, TeachMode.Image,
            () => HasTeachingPosition(bolt, reference) ? GetBoltPosition(bolt, reference) : new(),
            p =>
            {
                var position = CarrierCoordinates.FromMachine(p, reference.UpperLeftLocatingPin!);
                bolt.X = position.X;
                bolt.Y = position.Y;
            },
            isDefined: () => HasTeachingPosition(bolt, reference)) { Bolt = bolt });

    private static bool HasTeachingPosition(BoltPoint bolt, CarrierReferenceSettings reference) =>
        reference.IsDefined && bolt is { X: not null, Y: not null };

    public AxisPosition GetBoltPosition(
        BoltPoint bolt,
        CarrierReferenceSettings reference) =>
        CarrierCoordinates.ToMachine(
            new AxisPosition
            {
                X = bolt.X!.Value,
                Y = bolt.Y!.Value,
            },
            reference.UpperLeftLocatingPin!);
}
