using IBTM.Core;
using IBTM.Device;

namespace IBTM.Inspection;

public sealed class InspectionGantrySettings : Setting
{
    public InspectionGantrySettings()
    {
        Motion = new();
    }

    public MotionSettings Motion { get; set; }

    public TeachingPosition[] GetTeachingPositions(CarrierReferenceSettings reference)
    {
        return [
            new(
                TeachingTarget.CarrierUpperLeftLocatingPin,
                MotionGroup.InspectionGantry,
                TeachMode.XYOnly,
                () => reference.UpperLeftLocatingPin ?? new(),
                p => reference.UpperLeftLocatingPin = p,
                reference,
                () => reference.UpperLeftLocatingPin is not null),
            new(
                TeachingTarget.CarrierLowerRightLocatingPin,
                MotionGroup.InspectionGantry,
                TeachMode.XYOnly,
                () => reference.LowerRightLocatingPin ?? new(),
                p => reference.LowerRightLocatingPin = p,
                reference,
                () => reference.LowerRightLocatingPin is not null),
        ];
    }

    public AxisPosition GetBoltPosition(BoltPoint bolt)
    {
        return new AxisPosition { X = bolt.X!.Value, Y = bolt.Y!.Value };
    }
}
