using System.ComponentModel;
using IBTM.Core;

namespace IBTM.Device;

public enum TeachMode
{
    [Description("Image")]
    Image,

    [Description("XYZ")]
    Full,

    [Description("XY")]
    XYOnly,

    [Description("Z")]
    ZOnly,
}

public enum TeachingStorage
{
    [Description("Recipe")]
    Recipe,

    [Description("Machine")]
    Machine,

    [Description("Handoff setup")]
    Handoff,
}

public enum TeachingTarget
{
    [Description("Common Safe Z")]
    SafeZ,

    [Description("PCB 1 Pickup")]
    SupplyPcb1Pick,

    [Description("PCB 2 Pickup")]
    SupplyPcb2Pick,

    [Description("PCB Handoff (Supply)")]
    SupplyHandoff,

    [Description("PCB Handoff (Receive)")]
    PlacementHandoff,

    [Description("PCB Placement - Heat Sink 1")]
    HeatSink1PcbPlacement,

    [Description("PCB Placement - Heat Sink 2")]
    HeatSink2PcbPlacement,

    [Description("Bolt Fastening")]
    BoltPosition,

    [Description("Carrier Pickup (S3)")]
    NgCarrierPickup,

    [Description("Carrier Placement (Shuttle)")]
    NgShuttlePlace,

    [Description("Bolt Pickup (Feeder)")]
    BoltPickup,

    [Description("Bolt Inspection")]
    BoltReference,

    [Description("Data Matrix Inspection")]
    DataMatrix,

    [Description("Shooting PCB final Z")]
    ShootingHeadFasteningZ,
    [Description("Pickup IPM final Z")]
    PickupHeadFasteningZ,

    [Description("PCB Receive Z")]
    PlacementReceiveZ,

    [Description("Gantry Waiting Position")]
    InspectionWaiting,

    [Description("Shooting pre-tightening")]
    BoltPreliminaryPosition,

    [Description("Shooting IPM preliminary Z")]
    ShootingHeadPreliminaryZ,

    [Description("Transfer height")]
    BackupTransferHeight,
    [Description("Fastening height")]
    BackupFasteningHeight,

    [Description("Height reference 1")]
    HeightReference1,
    [Description("Height reference 2")]
    HeightReference2,
    [Description("Height reference 3")]
    HeightReference3,

    [Description("Fiducial 1")]
    Fiducial1,
    [Description("Fiducial 2")]
    Fiducial2,
}

// Command metadata only. Teaching values and writes are owned by the teaching UI.
public sealed record TeachingPosition(
    TeachingTarget Target,
    MotionGroup MotionGroup,
    TeachMode Mode,
    bool HasPosition = true)
{
    public BoltPoint? Bolt { get; init; }
}
