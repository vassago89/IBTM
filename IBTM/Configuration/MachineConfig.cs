using IBTM.Core.Geometry;
using IBTM.Core.Machine;
using IBTM.Stations.BoltFastening;
using IBTM.Stations.Inspection;
using IBTM.Transport;

namespace IBTM.Configuration;

public sealed class MachineConfig
{
    public CalibrationSettings Calibration { get; set; } = new();
    public ConveyorSettings Conveyor { get; set; } = new();
    public StationMotionSettings PcbPlacementMotion { get; set; } = new();
    public BoltFasteningOptions BoltFastening { get; set; } = new();
    public InspectionOptions Inspection { get; set; } = new();
}

public sealed class CalibrationSettings
{
    public AxisPos PcbPlacementReference { get; set; } = new();
    public AxisPos BoltFasteningReference { get; set; } = new();
    public AxisPos InspectionReference { get; set; } = new();
    public AxisPos InspectionToPcbPlacementOffset { get; set; } = new();
    public AxisPos InspectionToBoltFasteningOffset { get; set; } = new();

    public void ComputeOffsets()
    {
        InspectionToPcbPlacementOffset = new AxisPos
        {
            X = InspectionReference.X - PcbPlacementReference.X,
            Y = InspectionReference.Y - PcbPlacementReference.Y,
            Z = InspectionReference.Z - PcbPlacementReference.Z,
        };
        InspectionToBoltFasteningOffset = new AxisPos
        {
            X = InspectionReference.X - BoltFasteningReference.X,
            Y = InspectionReference.Y - BoltFasteningReference.Y,
            Z = InspectionReference.Z - BoltFasteningReference.Z,
        };
    }

    public AxisPos ToPcbPlacement(AxisPos position) => new()
    {
        X = position.X - InspectionToPcbPlacementOffset.X,
        Y = position.Y - InspectionToPcbPlacementOffset.Y,
        Z = position.Z - InspectionToPcbPlacementOffset.Z,
    };

    public AxisPos ToBoltFastening(AxisPos position) => new()
    {
        X = position.X - InspectionToBoltFasteningOffset.X,
        Y = position.Y - InspectionToBoltFasteningOffset.Y,
        Z = position.Z - InspectionToBoltFasteningOffset.Z,
    };

    public AxisPos FromPcbPlacementToInspection(AxisPos position) => new()
    {
        X = position.X + InspectionToPcbPlacementOffset.X,
        Y = position.Y + InspectionToPcbPlacementOffset.Y,
        Z = position.Z + InspectionToPcbPlacementOffset.Z,
    };

    public AxisPos FromBoltFasteningToInspection(AxisPos position) => new()
    {
        X = position.X + InspectionToBoltFasteningOffset.X,
        Y = position.Y + InspectionToBoltFasteningOffset.Y,
        Z = position.Z + InspectionToBoltFasteningOffset.Z,
    };
}
