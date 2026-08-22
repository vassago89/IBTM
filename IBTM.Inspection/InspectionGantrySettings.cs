using IBTM.Core;
using IBTM.Device;

namespace IBTM.Inspection;

public sealed class InspectionGantrySettings : Setting
{
    public MotionSettings Motion { get; set; } = new();
    public AxisPos CarrierScanUpperLeft { get; set; } = new();
    public AxisPos CarrierScanLowerRight { get; set; } = new();
    public double CarrierScanPitchX { get; set; } = 15.0;
    public double CarrierScanPitchY { get; set; } = 11.0;
    public AxisPos? UpperLeftLocatingPin { get; set; }
    public AxisPos? LowerRightLocatingPin { get; set; }
    public AxisPos NgCarrierJigPickupPosition { get; set; } = new();
    public AxisPos NgShuttlePlacePosition { get; set; } = new();
}
