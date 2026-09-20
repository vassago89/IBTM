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
    [Description("Travel Z")]
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

    [Description("Carrier Pickup Y (S3)")]
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

    [Description("Carrier Pickup X (Approach)")]
    NgPickupSafeX,

    [Description("Shooting Head Fastening Z")]
    ShootingHeadFasteningZ,
    [Description("Pickup Head Fastening Z")]
    PickupHeadFasteningZ,

    [Description("PCB Receive Z")]
    PlacementReceiveZ,

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
            switch (true)
            {
                case true when Staged:
                    return TeachingStorage.Handoff;
                case true when Setting is null:
                    return TeachingStorage.Recipe;
                default:
                    return TeachingStorage.Machine;
            }
        }
    }

    public bool HasPosition => _isDefined?.Invoke() ?? true;

    public bool IsTeachAllowed => _apply is not null;

    public AxisPosition Read()
    {
        return _read();
    }

    public void Apply(AxisPosition position)
    {
        _apply!(position);
    }
}
