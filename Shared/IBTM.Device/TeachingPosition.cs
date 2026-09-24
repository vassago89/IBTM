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

    [Description("X")]
    XOnly,

    [Description("Y")]
    YOnly,

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
    [Description("Safe Z")]
    SafeZ,

    [Description("PCB 1 Pickup")]
    SupplyPcb1Pick,

    [Description("PCB 2 Pickup")]
    SupplyPcb2Pick,

    [Description("PCB Handoff")]
    SupplyHandoff,

    [Description("PCB Receive Standby")]
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

    [Description("Backup Plate Pin - Upper Left")]
    CarrierUpperLeftLocatingPin,

    [Description("Backup Plate Pin - Lower Right")]
    CarrierLowerRightLocatingPin,

    [Description("Shooting Head Pin - Upper Left")]
    ShootingHeadUpperLeftLocatingPin,

    [Description("Shooting Head Pin - Lower Right")]
    ShootingHeadLowerRightLocatingPin,

    [Description("Pickup Head Pin - Upper Left")]
    PickupHeadUpperLeftLocatingPin,

    [Description("Pickup Head Pin - Lower Right")]
    PickupHeadLowerRightLocatingPin,

    [Description("Bolt Pickup (Feeder)")]
    BoltPickup,

    [Description("Bolt Inspection")]
    BoltReference,

    [Description("Data Matrix Inspection")]
    DataMatrix,

    [Description("Shooting Head Fastening Z")]
    ShootingHeadFasteningZ,
    [Description("Pickup Head Fastening Z")]
    PickupHeadFasteningZ,

    [Description("PCB Receive Z")]
    PlacementReceiveZ,

    [Description("Waiting")]
    InspectionWaiting,

}

// Command metadata only. Teaching values and writes are owned by the teaching UI.
public sealed record TeachingPosition(
    TeachingTarget Target,
    MotionGroup MotionGroup,
    TeachMode Mode,
    bool HasPosition = true)
{
    public BoltPoint? Bolt { get; init; }
    public bool IsTeachAllowed => Mode != TeachMode.Image;
}
