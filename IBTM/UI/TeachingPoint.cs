using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.UI;

public partial class TeachingPoint : ObservableObject
{
    public TeachingPoint(TeachingPosition position)
    {
        Position = position;
        Refresh();
    }

    public TeachingPosition Position { get; }

    public int BoltNumber
    {
        get
        {
            return Position.Bolt?.Number ?? 0;
        }
    }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(PositionLabel))]
    private double _x;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(PositionLabel))]
    private double _y;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(PositionLabel))]
    private double? _z;

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

            return (Position.Target, Position.MotionGroup) switch
            {
                (TeachingTarget.SafeZ, MotionGroup.PcbSupply) => "Transport / Rotation Z",
                (TeachingTarget.SafeZ, MotionGroup.PcbPlacementHandler) => "Approach Z",
                _ => Position.Target.GetDescription(),
            };
        }
    }

    public TeachingPointGroup Group
    {
        get
        {
            return Position.Target switch
            {
                TeachingTarget.BoltPosition => TeachingPointGroup.Calculated,
                TeachingTarget.SupplyBufferBoundary1 or TeachingTarget.SupplyBufferBoundary2
                    or TeachingTarget.PlacementBufferBoundary1 or TeachingTarget.PlacementBufferBoundary2
                    => TeachingPointGroup.Interference,
                TeachingTarget.SafeZ or TeachingTarget.SupplyCarrierY or TeachingTarget.SupplyBufferClearZ
                    or TeachingTarget.NgPickupSafeX
                    or TeachingTarget.CarrierUpperLeftLocatingPin or TeachingTarget.CarrierLowerRightLocatingPin
                    or TeachingTarget.ShootingHeadUpperLeftLocatingPin or TeachingTarget.ShootingHeadLowerRightLocatingPin
                    or TeachingTarget.PickupHeadUpperLeftLocatingPin or TeachingTarget.PickupHeadLowerRightLocatingPin
                    => TeachingPointGroup.MachineReference,
                _ => TeachingPointGroup.Work,
            };
        }
    }

    public string PositionLabel
    {
        get
        {
            if (!Position.HasPosition)
                return "—";
            return Position.Mode switch
            {
                TeachMode.Image or TeachMode.XYOnly => $"X {X:F3}  Y {Y:F3}",
                TeachMode.XZOnly => $"X {X:F3}  Z {Z:F3}",
                TeachMode.XOnly => $"X {X:F3}",
                TeachMode.YOnly => $"Y {Y:F3}",
                TeachMode.ZOnly => Z is { } z ? $"Z {z:F3}" : "—",
                _ => $"X {X:F3}  Y {Y:F3}  Z {Z:F3}",
            };
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
    [Description("Handoff interference area")]
    Interference,
}

public enum TeachingSaveBehavior
{
    [Description("Teach give XY. Supply holds the PCB at Transport / Rotation Z until Placement detects the PCB, vacuum and closed gripper. Apply & Save Handoff before leaving Teaching.")]
    SupplyHandoff,
    [Description("Teach receiving XYZ. After gripping, Placement waits for Supply to leave before lifting away. Apply & Save Handoff before leaving Teaching.")]
    PlacementHandoff,
    [Description("After releasing the PCB, Supply moves to this Z and withdraws X. This is clearance for withdrawal. Apply & Save Handoff before leaving Teaching.")]
    SupplyClearance,
    [Description("Common pickup Y for both PCB slots. Each slot teaches only X and Z. Saves automatically.")]
    SupplyCarrierY,
    [Description("Teach pickup Y. Move To uses NG Pickup Safe X and this Y. Saves automatically.")]
    NgPickup,

    [Description("Teach with Head 1 down; saves automatically. Move To lowers Head 1 at pickup XY, then moves Z. Vacuum is unchanged.")]
    BoltPickup,

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
    [Description("Add Current Image, then draw the bolt ROI on the saved FOV. This heat sink is taught independently. Saves automatically.")]
    Image,
    [Description("Live: jog, stop, Add Current Image. Draw the barcode ROI on the saved FOV for this heat sink. Reading returns to the captured XY. Saves automatically.")]
    BarcodeFov,
}
