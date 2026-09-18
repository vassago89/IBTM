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

    [Description("Handoff setup")]
    Buffer,
}

public enum TeachingTarget
{
    [Description("Safe Z")]
    SafeZ,

    [Description("PCB Pickup Common Y")]
    SupplyCarrierY,

    [Description("PCB 1 Pick")]
    SupplyPcb1Pick,

    [Description("PCB 2 Pick")]
    SupplyPcb2Pick,

    [Description("PCB Give Position")]
    SupplyBufferHandoff,

    [Description("PCB Receive Position")]
    PlacementBufferHandoff,

    [Description("Interference X Boundary 1")]
    SupplyBufferBoundary1,

    [Description("Interference X Boundary 2")]
    SupplyBufferBoundary2,

    [Description("Interference Area Corner 1")]
    PlacementBufferBoundary1,

    [Description("Interference Area Corner 2")]
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

    [Description("Shooting Head Fastening Z")]
    ShootingHeadFasteningZ,
    [Description("Pickup Head Fastening Z")]
    PickupHeadFasteningZ,

}

public sealed class TeachingPosition
{
    private readonly Func<AxisPosition> _read;
    private readonly Action<AxisPosition>? _apply;
    private readonly Func<bool>? _isDefined;

    public TeachingPosition(
        TeachingTarget target,
        MotionGroup motionGroup,
        TeachMode mode,
        Func<AxisPosition> read,
        Action<AxisPosition>? apply,
        Setting? setting = null,
        Func<bool>? isDefined = null)
    {
        _read = read;
        _apply = apply;
        _isDefined = isDefined;
        Target = target;
        MotionGroup = motionGroup;
        Mode = mode;
        Setting = setting;
    }

    public TeachingTarget Target { get; }
    public MotionGroup MotionGroup { get; }
    public TeachMode Mode { get; }
    public Setting? Setting { get; }
    public BoltPoint? Bolt { get; init; }
    public bool Staged { get; init; }

    public TeachingStorage Storage
    {
        get
        {
            if (Staged)
            {
                return TeachingStorage.Buffer;
            }

            if (Setting is null)
            {
                return TeachingStorage.Recipe;
            }

            return TeachingStorage.Machine;
        }
    }

    public bool HasPosition
    {
        get
        {
            return _isDefined?.Invoke() ?? true;
        }
    }

    public bool CanTeach
    {
        get
        {
            return _apply is not null;
        }
    }

    public AxisPosition Read()
    {
        return _read();
    }

    public void Apply(AxisPosition position)
    {
        _apply!(position);
    }
}
