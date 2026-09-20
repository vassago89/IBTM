using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.UI;

public class TeachingPoint : ObservableObject
{
    public double X => Position.Read().X;
    public double Y => Position.Read().Y;
    public double? Z => Position.Read().Z;

    public TeachingPoint(TeachingPosition position)
    {
        Position = position;
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
                    return "Z height used before PCB rotation and for travel above the pickup positions. Moving to this height moves only Z.";
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
                    return "Bolt recorded in Inspection Gantry. XY adds the selected head's Upper/Lower midpoint minus the camera midpoint; Z uses the head's fastening Z. Position recording is available only in Inspection Gantry. Move to Position uses Safe Z, sets the table down for pickup or up for shooting, then moves XY and fastening Z.";
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
                case TeachingTarget.NgCarrierPickup:
                    return "XY where the transfer grips the carrier at Station 3. X and Y move together.";
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
            {
                if (Position.Target == TeachingTarget.BoltPosition)
                    return Position.Bolt is { X: not null, Y: not null }
                        ? "Teach camera and head Upper / Lower references"
                        : "Record bolt position in Inspection Gantry";
                return "Not taught";
            }
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
        var position = Read();
        if (Position.Mode is TeachMode.Image
            or TeachMode.XYOnly
            or TeachMode.Full
            or TeachMode.XOnly)
        {
            position.X = x;
        }

        if (Position.Mode is TeachMode.Image or TeachMode.XYOnly or TeachMode.Full or TeachMode.YOnly)
        {
            position.Y = y;
        }

        if (Position.Mode is TeachMode.Full or TeachMode.ZOnly)
        {
            position.Z = z;
        }
        Position.Apply(position);
        Refresh();
    }

    public AxisPosition Read()
    {
        var position = Position.Read();
        return new()
        {
            X = position.X,
            Y = position.Y,
            Z = Position.Mode == TeachMode.Image ? 0 : position.Z,
        };
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(X));
        OnPropertyChanged(nameof(Y));
        OnPropertyChanged(nameof(Z));
        OnPropertyChanged(nameof(PositionLabel));
    }
}

public enum TeachingPointGroup
{
    [Description("Work positions")]
    Work,
    [Description("Reference positions")]
    MachineReference,
    [Description("Bolts from Inspection Gantry")]
    Calculated,
}

public enum TeachingSaveBehavior
{
    [Description("Only Record Position changes these handoff coordinates. Move to Position uses them. Save keeps them after restart.")]
    SupplyHandoff,
    [Description("Only Record Position changes these standby coordinates. Move to Position moves Z first, then X/Y. Save keeps them after restart.")]
    PlacementHandoff,
    [Description("Record Position saves this Z automatically. Move to Position moves only Z at the current X/Y. Select PCB Receive Standby to move X/Y.")]
    PlacementReceiveZ,
    [Description("Record Position saves pickup X/Y together automatically. Move to Position moves X and Y together.")]
    NgPickup,

    [Description("Record Position with pickup head (Head 1) down; saves automatically. Move to Position travels to pickup XY, lowers the head, then moves to pickup Z. Vacuum is unchanged.")]
    BoltPickup,

    [Description("Record Position saves this head's Z automatically. Move to Position moves only Z. Automatic fastening reaches this Z before lowering the head.")]
    FasteningZ,

    [Description("Move to Position: Safe Z → table down for pickup / up for shooting → bolt XY → fastening Z. Both heads must be raised. Record the bolt in Inspection Gantry.")]
    BoltPosition,

    [Description("Center this backup plate pin in Live, then press Record Position. Saves automatically.")]
    CameraCenter,

    [Description("Record Position saves this machine coordinate automatically.")]
    Machine,
    [Description("Record Position updates this product's coordinates. Press Save to keep them after restart.")]
    Recipe,
    [Description("Recorded handoff coordinates stay when you leave this page. Save keeps them after restart.")]
    Handoff,
    [Description("Center the bolt in Live, then Record Position to save its coordinates and image. ROI resizing does not change coordinates. Each heat sink is taught independently.")]
    Image,
    [Description("Center the Data Matrix in Live, stop the axes, then Record Position to save its XY and image. ROI resizing does not change coordinates. Move to Position returns to the recorded XY.")]
    BarcodeFov,
}
