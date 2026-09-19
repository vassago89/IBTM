using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.UI;

public partial class TeachingPoint : ObservableObject
{
    [ObservableProperty, NotifyPropertyChangedFor(nameof(PositionLabel))]
    public partial double X { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(PositionLabel))]
    public partial double Y { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(PositionLabel))]
    public partial double? Z { get; set; }

    public TeachingPoint(TeachingPosition position)
    {
        Position = position;
        Refresh();
    }

    public TeachingPosition Position { get; }

    public int BoltNumber => Position.Bolt?.Number ?? 0;

    public string Name
    {
        get
        {
            if (Position.Bolt is { } bolt)
            {
                return Position.Target == TeachingTarget.BoltPosition
                    ? $"B{bolt.Number} · {bolt.Head.GetDescription()}"
                    : $"B{bolt.Number} · Inspection FOV";
            }

            switch ((Position.Target, Position.MotionGroup))
            {
                case (TeachingTarget.SafeZ, MotionGroup.PcbSupply):
                    return "Rotation Z";
                case (TeachingTarget.SafeZ, MotionGroup.BoltFastening):
                    return "Safe Z (Travel)";
                default:
                    return Position.Target.GetDescription();
            }
        }
    }

    public TeachingPointGroup Group
    {
        get
        {
            switch (Position.Target)
            {
                case TeachingTarget.BoltPosition:
                    return TeachingPointGroup.Calculated;
                case TeachingTarget.SafeZ:
                case TeachingTarget.ShootingHeadFasteningZ:
                case TeachingTarget.PickupHeadFasteningZ:
                case TeachingTarget.SupplyCarrierY:
                case TeachingTarget.NgPickupSafeX:
                case TeachingTarget.CarrierUpperLeftLocatingPin:
                case TeachingTarget.CarrierLowerRightLocatingPin:
                case TeachingTarget.ShootingHeadUpperLeftLocatingPin:
                case TeachingTarget.ShootingHeadLowerRightLocatingPin:
                case TeachingTarget.PickupHeadUpperLeftLocatingPin:
                case TeachingTarget.PickupHeadLowerRightLocatingPin:
                    return TeachingPointGroup.MachineReference;
                default:
                    return TeachingPointGroup.Work;
            }
        }
    }

    public string PositionLabel
    {
        get
        {
            if (!Position.HasPosition)
                return "—";
            switch (Position.Mode)
            {
                case TeachMode.Image or TeachMode.XYOnly:
                    return $"X {X:F3}  Y {Y:F3}";
                case TeachMode.XZOnly:
                    return $"X {X:F3}  Z {Z:F3}";
                case TeachMode.XOnly:
                    return $"X {X:F3}";
                case TeachMode.YOnly:
                    return $"Y {Y:F3}";
                case TeachMode.ZOnly:
                    return Z is { } z ? $"Z {z:F3}" : "—";
                default:
                    return $"X {X:F3}  Y {Y:F3}  Z {Z:F3}";
            }
        }
    }

    public void Teach(double x, double y, double z)
    {
        if (Position.Mode is TeachMode.Image
            or TeachMode.XYOnly
            or TeachMode.Full
            or TeachMode.XZOnly
            or TeachMode.XOnly)
        {
            X = x;
        }

        if (Position.Mode is TeachMode.Image or TeachMode.XYOnly or TeachMode.Full or TeachMode.YOnly)
        {
            Y = y;
        }

        if (Position.Mode is TeachMode.Full or TeachMode.XZOnly or TeachMode.ZOnly)
        {
            Z = z;
        }
    }

    public AxisPosition Read()
    {
        return new()
        {
            X = X,
            Y = Y,
            Z = Position.Mode == TeachMode.Image ? 0 : Z!.Value,
        };
    }

    public void Apply()
    {
        Position.Apply(Read());
    }

    public void Refresh()
    {
        var position = Position.Read();
        X = position.X;
        Y = position.Y;
        Z = position.Z;
        OnPropertyChanged(nameof(PositionLabel));
    }
}

public enum TeachingPointGroup
{
    [Description("Work positions")]
    Work,
    [Description("Machine references")]
    MachineReference,
    [Description("Calculated positions · Move To for verification")]
    Calculated,
}

public enum TeachingSaveBehavior
{
    [Description("Teach give XYZ. Supply picks while Rotated, unrotates at Rotation Z, moves to give Z, then moves XY and holds until Placement detects the PCB, vacuum and closed gripper. Apply & Save Handoff before leaving Teaching.")]
    SupplyHandoff,
    [Description("Teach receiving XYZ. This Z is shared by XY travel, rotation and receipt, with clearance while the handler cylinder is Up. Either handler may arrive first; only the cylinder lowers after both arrive. Apply & Save Handoff before leaving Teaching.")]
    PlacementHandoff,
    [Description("Common pickup Y for both PCB slots. Each slot teaches only X and Z. Saves automatically.")]
    SupplyCarrierY,
    [Description("Teach pickup Y. Move To uses NG Pickup Safe X and this Y. Saves automatically.")]
    NgPickup,

    [Description("Teach with Head 1 down; saves automatically. Move To lowers Head 1 at pickup XY, then moves Z. Vacuum is unchanged.")]
    BoltPickup,

    [Description("Work Z for this head: Shooting for PCB, Pickup for one fastening per picked bolt. Automatic operation reaches this Z with both heads raised, starts rotation, then immediately lowers the selected head to feed the bolt. Saves automatically.")]
    FasteningZ,

    [Description("Calculated from this heat sink's bolt teaching and head reference pins. Move to verify at Safe Z; teach bolt positions in Inspection.")]
    BoltPosition,

    [Description("Align the pin with the live camera center, then Teach. Saves automatically.")]
    CameraCenter,

    [Description("Machine position · Teach saves automatically.")]
    Machine,
    [Description("Recipe position · Use Save Recipe after teaching.")]
    Recipe,
    [Description("PCB handoff · Apply & Save Handoff before leaving Teaching; otherwise staged changes are discarded.")]
    Buffer,
    [Description("Center the bolt in Live, then Grab. Resize the centered square ROI. This heat sink is taught independently. Saves automatically.")]
    Image,
    [Description("Center the Data Matrix in Live, stop, then Grab. Resize the centered square ROI. Reading returns to the captured XY. Saves automatically.")]
    BarcodeFov,
}
