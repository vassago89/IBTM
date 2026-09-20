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
                    ? $"Bolt {bolt.Number} Fastening · {bolt.Head.GetDescription()}"
                    : $"Bolt {bolt.Number} Inspection";
            }

            switch ((Position.Target, Position.MotionGroup))
            {
                case (TeachingTarget.SafeZ, MotionGroup.PcbSupply):
                    return "PCB Rotation Z";
                case (TeachingTarget.SafeZ, MotionGroup.BoltFastening):
                    return "Travel Z";
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

    public string Description
    {
        get
        {
            switch (Position.Target)
            {
                case TeachingTarget.SafeZ when Position.MotionGroup == MotionGroup.PcbSupply:
                    return "Z height for PCB rotation and travel above the pickup positions.";
                case TeachingTarget.SafeZ:
                    return "Z height for horizontal travel with both fastening heads raised.";
                case TeachingTarget.SupplyPcb1Pick:
                    return "XYZ where Supply picks PCB 1 from the incoming carrier.";
                case TeachingTarget.SupplyPcb2Pick:
                    return "XYZ where Supply picks PCB 2 from the incoming carrier.";
                case TeachingTarget.SupplyHandoff:
                    return "XYZ where Supply hands the PCB to Placement. This Z is also used for the return XY move.";
                case TeachingTarget.PlacementHandoff:
                    return "XYZ where Placement waits for Supply and returns after receiving the PCB. This Z is also its travel height.";
                case TeachingTarget.PlacementReceiveZ:
                    return "Z where Placement grips the PCB, using the X/Y of PCB Receive Standby.";
                case TeachingTarget.HeatSink1PcbPlacement:
                    return "XYZ where Placement seats the PCB on Heat Sink 1.";
                case TeachingTarget.HeatSink2PcbPlacement:
                    return "XYZ where Placement seats the PCB on Heat Sink 2.";
                case TeachingTarget.BoltPickup:
                    return "XYZ where the pickup head (Head 1) collects a bolt from the feeder.";
                case TeachingTarget.ShootingHeadFasteningZ:
                    return "Z used for fastening with the shooting head (Head 2).";
                case TeachingTarget.PickupHeadFasteningZ:
                    return "Z used for fastening with the pickup head (Head 1).";
                case TeachingTarget.BoltPosition:
                    return "Calculated XY for this bolt. Its fastening Z is set separately for the selected head.";
                case TeachingTarget.BoltReference:
                    return "Camera XY and teaching image for inspecting this bolt.";
                case TeachingTarget.DataMatrix:
                    return "Camera XY and teaching image for reading this heat sink's Data Matrix.";
                case TeachingTarget.CarrierUpperLeftLocatingPin:
                    return "Camera XY centered on the backup plate's upper-left reference pin.";
                case TeachingTarget.CarrierLowerRightLocatingPin:
                    return "Camera XY centered on the backup plate's lower-right reference pin.";
                case TeachingTarget.ShootingHeadUpperLeftLocatingPin:
                    return "Shooting head XY aligned with the backup plate's upper-left reference pin.";
                case TeachingTarget.ShootingHeadLowerRightLocatingPin:
                    return "Shooting head XY aligned with the backup plate's lower-right reference pin.";
                case TeachingTarget.PickupHeadUpperLeftLocatingPin:
                    return "Pickup head XY aligned with the backup plate's upper-left reference pin.";
                case TeachingTarget.PickupHeadLowerRightLocatingPin:
                    return "Pickup head XY aligned with the backup plate's lower-right reference pin.";
                case TeachingTarget.NgPickupSafeX:
                    return "X used to approach and pick up the carrier at Station 3. X moves before Pickup Y.";
                case TeachingTarget.NgCarrierPickup:
                    return "Y where the transfer grips the carrier at Station 3, using Carrier Pickup X (Approach).";
                case TeachingTarget.NgShuttlePlace:
                    return "XY where the transfer places the carrier on the NG shuttle.";
                default:
                    return "";
            }
        }
    }

    public string PositionLabel
    {
        get
        {
            if (!Position.HasPosition)
                return "Not taught";
            switch (Position.Mode)
            {
                case TeachMode.Image or TeachMode.XYOnly:
                    return $"X {X:F3}  Y {Y:F3}";
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
            or TeachMode.XOnly)
        {
            X = x;
        }

        if (Position.Mode is TeachMode.Image or TeachMode.XYOnly or TeachMode.Full or TeachMode.YOnly)
        {
            Y = y;
        }

        if (Position.Mode is TeachMode.Full or TeachMode.ZOnly)
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
    [Description("Reference positions")]
    MachineReference,
    [Description("Calculated fastening positions")]
    Calculated,
}

public enum TeachingSaveBehavior
{
    [Description("Teach updates the pending handoff XYZ. Move To uses this pending value. Use Apply & Save Handoff to apply and save.")]
    SupplyHandoff,
    [Description("Teach updates the pending standby XYZ. Move To uses this pending value. Use Apply & Save Handoff to apply and save.")]
    PlacementHandoff,
    [Description("Teach saves this Z automatically. Move To moves Z only; use PCB Receive Standby for X/Y.")]
    PlacementReceiveZ,
    [Description("Teach saves this Y automatically. Move To uses Carrier Pickup X (Approach), then this Y.")]
    NgPickup,

    [Description("Teach with the pickup head (Head 1) down. Saves automatically. Move To travels to pickup XY, lowers the head, then moves to pickup Z. Vacuum is unchanged.")]
    BoltPickup,

    [Description("Teach saves this head's fastening Z automatically. Move To moves Z only. Automatic fastening uses this Z before lowering the selected head.")]
    FasteningZ,

    [Description("Calculated from bolt inspection and reference pins. Move To checks XY at Travel Z. Teach the bolt in Inspection Gantry.")]
    BoltPosition,

    [Description("Center this backup plate pin in Live, then Teach. Saves automatically.")]
    CameraCenter,

    [Description("Machine setting · Teach saves automatically.")]
    Machine,
    [Description("Recipe setting · Use Save Recipe after teaching.")]
    Recipe,
    [Description("Pending handoff setting · Use Apply & Save Handoff. Unsaved edits are discarded when Teaching closes.")]
    Handoff,
    [Description("Center the bolt in Live, then Grab. Resize the centered square ROI. This heat sink is taught independently. Saves automatically.")]
    Image,
    [Description("Center the Data Matrix in Live, stop the axes, then Grab. Resize the centered square ROI. Move To returns to the captured XY. Read Data Matrix reads the saved image without moving. Saves automatically.")]
    BarcodeFov,
}
