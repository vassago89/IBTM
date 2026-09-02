using IBTM.Core;
using IBTM.Device;

namespace IBTM.Inspection;

public sealed class InspectionGantrySettings : Setting
{
    public MotionSettings Motion { get; set; } = new();
    public AxisPosition CarrierScanUpperLeft { get; set; } = new();
    public AxisPosition CarrierScanLowerRight { get; set; } = new();
    public double CarrierScanOverlapMillimeters { get; set; } = 1.0;

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
