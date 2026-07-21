using IBTM.Core.Geometry;
using IBTM.Core.Machine;
using IBTM.Stations.BoltFastening;
using IBTM.Stations.Inspection;

namespace IBTM.Configuration;

public sealed class MachineConfig
{
    public CalibrationSettings Calibration { get; set; } = new();
    public ZoneMotionParams PcbPlacementMotion { get; set; } = new();
    public BoltFasteningOptions BoltFastening { get; set; } = new();
    public InspectionOptions Inspection { get; set; } = new();
}

public sealed class CalibrationSettings
{
    public AxisPos Zone1Ref { get; set; } = new();
    public AxisPos Zone2Ref { get; set; } = new();
    public AxisPos Zone3Ref { get; set; } = new();
    public AxisPos Offset3To1 { get; set; } = new();
    public AxisPos Offset3To2 { get; set; } = new();

    public void ComputeOffsets()
    {
        Offset3To1 = new AxisPos
        {
            X = Zone3Ref.X - Zone1Ref.X,
            Y = Zone3Ref.Y - Zone1Ref.Y,
            Z = Zone3Ref.Z - Zone1Ref.Z,
        };
        Offset3To2 = new AxisPos
        {
            X = Zone3Ref.X - Zone2Ref.X,
            Y = Zone3Ref.Y - Zone2Ref.Y,
            Z = Zone3Ref.Z - Zone2Ref.Z,
        };
    }

    public AxisPos ToZone1(AxisPos position) => new()
    {
        X = position.X - Offset3To1.X,
        Y = position.Y - Offset3To1.Y,
        Z = position.Z - Offset3To1.Z,
    };

    public AxisPos ToZone2(AxisPos position) => new()
    {
        X = position.X - Offset3To2.X,
        Y = position.Y - Offset3To2.Y,
        Z = position.Z - Offset3To2.Z,
    };

    public AxisPos FromZone1ToZone3(AxisPos position) => new()
    {
        X = position.X + Offset3To1.X,
        Y = position.Y + Offset3To1.Y,
        Z = position.Z + Offset3To1.Z,
    };

    public AxisPos FromZone2ToZone3(AxisPos position) => new()
    {
        X = position.X + Offset3To2.X,
        Y = position.Y + Offset3To2.Y,
        Z = position.Z + Offset3To2.Z,
    };
}
