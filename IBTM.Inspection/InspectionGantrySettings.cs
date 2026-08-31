using IBTM.Core;
using IBTM.Device;

namespace IBTM.Inspection;

public sealed class InspectionGantrySettings : Setting
{
    public MotionSettings Motion { get; set; } = new();
    public AxisPos CarrierScanUpperLeft { get; set; } = new();
    public AxisPos CarrierScanLowerRight { get; set; } = new();
    public double CarrierScanOverlapMillimeters { get; set; } = 1.0;

    public AxisPos GetBoltPosition(
        BoltPoint bolt,
        CarrierReferenceSettings reference) =>
        CarrierCoordinates.ToMachine(
            new AxisPos
            {
                X = bolt.X!.Value,
                Y = bolt.Y!.Value,
            },
            reference.UpperLeftPin!);
}
