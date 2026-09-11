using System;
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

    [Description("XZ")]
    XZOnly,

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

    [Description("Buffer setup")]
    Buffer,
}

public enum TeachingTarget
{
    [Description("Safe Z")]
    SafeZ,

    [Description("Supply Carrier Y")]
    SupplyCarrierY,

    [Description("PCB 1 Pick")]
    SupplyPcb1Pick,

    [Description("PCB 2 Pick")]
    SupplyPcb2Pick,

    [Description("Supply Buffer Handoff")]
    SupplyBufferHandoff,

    [Description("Supply Buffer Clear Z")]
    SupplyBufferClearZ,

    [Description("Placement Buffer Handoff")]
    PlacementBufferHandoff,

    [Description("Supply Buffer Boundary 1")]
    SupplyBufferBoundary1,

    [Description("Supply Buffer Boundary 2")]
    SupplyBufferBoundary2,

    [Description("Placement Buffer Boundary 1")]
    PlacementBufferBoundary1,

    [Description("Placement Buffer Boundary 2")]
    PlacementBufferBoundary2,

    [Description("Heat Sink 1 PCB Placement")]
    HeatSink1PcbPlacement,

    [Description("Heat Sink 2 PCB Placement")]
    HeatSink2PcbPlacement,

    [Description("Bolt Position")]
    BoltPosition,

    [Description("NG Carrier Pickup Y")]
    NgCarrierPickup,

    [Description("NG Shuttle Place")]
    NgShuttlePlace,

    [Description("Backup Plate Upper Left Pin")]
    CarrierUpperLeftLocatingPin,

    [Description("Backup Plate Lower Right Pin")]
    CarrierLowerRightLocatingPin,

    [Description("Shooting Head Upper Left Locating Pin")]
    ShootingHeadUpperLeftLocatingPin,

    [Description("Shooting Head Lower Right Locating Pin")]
    ShootingHeadLowerRightLocatingPin,

    [Description("Pickup Head Upper Left Locating Pin")]
    PickupHeadUpperLeftLocatingPin,

    [Description("Pickup Head Lower Right Locating Pin")]
    PickupHeadLowerRightLocatingPin,

    [Description("Bolt Pickup")]
    BoltPickup,

    [Description("Bolt Reference")]
    BoltReference,

    [Description("Data Matrix")]
    DataMatrix,

    [Description("NG Pickup Safe X")]
    NgPickupSafeX,

    [Description("Bolt")]
    BoltTeaching,
}

public sealed class TeachingPosition(
    TeachingTarget target,
    MotionGroup motionGroup,
    TeachMode mode,
    Func<AxisPosition> read,
    Action<AxisPosition>? apply,
    Setting? setting = null,
    Func<bool>? isDefined = null)
{
    public TeachingTarget Target { get; } = target;
    public MotionGroup MotionGroup { get; } = motionGroup;
    public TeachMode Mode { get; } = mode;
    public Setting? Setting { get; } = setting;
    public BoltTarget? Bolt { get; init; }
    public bool Staged { get; init; }

    public TeachingStorage Storage
    {
        get
        {
            return Staged
                ? TeachingStorage.Buffer
                : Setting is null ? TeachingStorage.Recipe : TeachingStorage.Machine;
        }
    }

    public bool HasPosition
    {
        get
        {
            return isDefined?.Invoke() ?? true;
        }
    }

    public bool CanTeach
    {
        get
        {
            return apply is not null;
        }
    }

    public AxisPosition Read()
    {
        return read();
    }

    public void Apply(AxisPosition position)
    {
        apply!(position);
    }
}
