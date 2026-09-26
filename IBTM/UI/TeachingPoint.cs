using System;
using System.Linq;
using System.ComponentModel;
using IBTM.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.UI;

public class TeachingPoint : ObservableObject
{
    private readonly MachineSettings _settings;
    private readonly RecipeManager _recipes;
    private readonly HeatSinkSlot _pcb;
    private readonly TeachingPosition _definition;

    public double FasteningZOffset
    {
        get => _definition.Bolt?.FasteningZOffset ?? 0;
        set
        {
            if (_definition.Target != TeachingTarget.BoltPosition || _definition.Bolt is not { } bolt)
                return;
            bolt.FasteningZOffset = value;
            OnPropertyChanged();
            Refresh();
        }
    }

    public TeachingPoint(TeachingPosition definition, MachineSettings settings, RecipeManager recipes,
        HeatSinkSlot pcb = HeatSinkSlot.HeatSink1)
    {
        _definition = definition;
        _settings = settings;
        _recipes = recipes;
        _pcb = pcb;
    }

    public TeachingPosition Position => _definition with { HasPosition = Coordinates is not null };

    public TeachingStorage Storage => _definition.Target is TeachingTarget.SupplyHandoff or TeachingTarget.PlacementHandoff
        ? TeachingStorage.Handoff : Setting is null ? TeachingStorage.Recipe : TeachingStorage.Machine;

    public Setting? Setting => _definition.Target switch
    {
        TeachingTarget.SupplyHandoff => _settings.PcbSupply,
        TeachingTarget.PlacementHandoff or TeachingTarget.PlacementReceiveZ => _settings.PcbPlacementHandler,
        TeachingTarget.SafeZ => _definition.MotionGroup == MotionGroup.PcbSupply
            ? _settings.PcbSupply : _settings.BoltFastening,
        TeachingTarget.BoltPickup or TeachingTarget.ShootingHeadFasteningZ or TeachingTarget.PickupHeadFasteningZ
            or TeachingTarget.ShootingHeadUpperLeftLocatingPin or TeachingTarget.ShootingHeadLowerRightLocatingPin
            or TeachingTarget.PickupHeadUpperLeftLocatingPin or TeachingTarget.PickupHeadLowerRightLocatingPin => _settings.BoltFastening,
        TeachingTarget.CarrierUpperLeftLocatingPin or TeachingTarget.CarrierLowerRightLocatingPin => _settings.CarrierReference,
        TeachingTarget.InspectionWaiting or TeachingTarget.NgCarrierPickup or TeachingTarget.NgShuttlePlace => _settings.NgCarrierTransfer,
        _ => null,
    };

    public AxisPosition? Coordinates
    {
        get
        {
            var recipe = _recipes.Current;
            switch (_definition.Target)
            {
                case TeachingTarget.SafeZ:
                    return new() { Z = _definition.MotionGroup == MotionGroup.PcbSupply
                        ? _settings.PcbSupply.RotationZ : _settings.BoltFastening.SafeZ };
                case TeachingTarget.SupplyPcb1Pick or TeachingTarget.SupplyPcb2Pick:
                    var pick = _definition.Target == TeachingTarget.SupplyPcb1Pick
                        ? recipe.PcbSupply.Pcb1PickPosition : recipe.PcbSupply.Pcb2PickPosition;
                    return pick.Y is { } y ? new() { X = pick.X, Y = y, Z = pick.Z } : null;
                case TeachingTarget.SupplyHandoff:
                    return _settings.PcbSupply.HandoffPosition;
                case TeachingTarget.PlacementHandoff:
                    return _settings.PcbPlacementHandler.HandoffPosition;
                case TeachingTarget.PlacementReceiveZ:
                    return _settings.PcbPlacementHandler.ReceiveZ is { } z ? new() { Z = z } : null;
                case TeachingTarget.HeatSink1PcbPlacement:
                    return recipe.PcbPlacement.HeatSink1PcbPlacementPosition;
                case TeachingTarget.HeatSink2PcbPlacement:
                    return recipe.PcbPlacement.HeatSink2PcbPlacementPosition;
                case TeachingTarget.BoltPickup:
                    return _settings.BoltFastening.PickupPosition;
                case TeachingTarget.BoltPosition:
                    return _definition.Bolt is { IsFasteningPositionDefined: true } bolt
                        ? _settings.BoltFastening.GetBoltPosition(bolt) : null;
                case TeachingTarget.ShootingHeadFasteningZ:
                    return new() { Z = _settings.BoltFastening.ShootingHead.FasteningZ };
                case TeachingTarget.PickupHeadFasteningZ:
                    return new() { Z = _settings.BoltFastening.PickupHead.FasteningZ };
                case TeachingTarget.ShootingHeadUpperLeftLocatingPin:
                    return _settings.BoltFastening.ShootingHead.UpperLeftLocatingPin;
                case TeachingTarget.ShootingHeadLowerRightLocatingPin:
                    return _settings.BoltFastening.ShootingHead.LowerRightLocatingPin;
                case TeachingTarget.PickupHeadUpperLeftLocatingPin:
                    return _settings.BoltFastening.PickupHead.UpperLeftLocatingPin;
                case TeachingTarget.PickupHeadLowerRightLocatingPin:
                    return _settings.BoltFastening.PickupHead.LowerRightLocatingPin;
                case TeachingTarget.CarrierUpperLeftLocatingPin:
                    return _settings.CarrierReference.UpperLeftLocatingPin;
                case TeachingTarget.CarrierLowerRightLocatingPin:
                    return _settings.CarrierReference.LowerRightLocatingPin;
                case TeachingTarget.InspectionWaiting:
                    return _settings.NgCarrierTransfer.WaitingPosition;
                case TeachingTarget.NgCarrierPickup:
                    return _settings.NgCarrierTransfer.CarrierPickupPosition;
                case TeachingTarget.NgShuttlePlace:
                    return _settings.NgCarrierTransfer.ShuttlePlacePosition;
                case TeachingTarget.DataMatrix:
                    return recipe.CarrierImages.SingleOrDefault(tile => tile.IsBarcode && tile.HeatSink == _pcb)?.Center;
                case TeachingTarget.BoltReference:
                    return recipe.CarrierImages.Count(tile => !tile.IsBarcode && tile.HeatSink == _pcb
                        && tile.BoltNumber == _definition.Bolt?.Number) == 1 ? _definition.Bolt?.InspectionPosition : null;
                default:
                    throw new ArgumentOutOfRangeException(nameof(_definition.Target));
            }
        }
    }

    public int BoltNumber => _definition.Bolt?.Number ?? 0;

    public string Name
    {
        get
        {
            if (_definition.Bolt is { } bolt)
            {
                return _definition.Target == TeachingTarget.BoltPosition
                    ? $"Bolt {bolt.Number} Fastening · {bolt.Head.GetDescription()}"
                    : $"Bolt {bolt.Number} Inspection";
            }

            switch ((_definition.Target, _definition.MotionGroup))
            {
                case (TeachingTarget.SafeZ, MotionGroup.PcbSupply):
                    return "PCB Rotation Z";
                default:
                    return _definition.Target.GetDescription();
            }
        }
    }

    public TeachingPointGroup Group
    {
        get
        {
            switch (_definition.Target)
            {
                case TeachingTarget.BoltPosition:
                    return TeachingPointGroup.Fastening;
                case TeachingTarget.InspectionWaiting or TeachingTarget.NgCarrierPickup or TeachingTarget.NgShuttlePlace:
                    return TeachingPointGroup.CarrierTransfer;
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
            switch (_definition.Target)
            {
                case TeachingTarget.SafeZ when _definition.MotionGroup == MotionGroup.PcbSupply:
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
                    return "Independent fastening XY, initially converted from inspection. Record Position updates only this bolt's XY. Z is the head's common fastening Z plus this bolt's offset. Save keeps these adjustments in the recipe.";
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
                case TeachingTarget.InspectionWaiting:
                    return "Independent waiting XY used while idle and after inspection. X and Y move together.";
                case TeachingTarget.NgCarrierPickup:
                    return "Carrier pickup XY at Station 3, also used before raising its backup plate. X and Y move together.";
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
            if (Coordinates is not { } position)
            {
                if (_definition.Target == TeachingTarget.BoltPosition)
                    return "Record fastening XY; initial conversion needs inspection XY and both sets of reference pins";
                return "Not taught";
            }
            if (_definition.Target == TeachingTarget.BoltPosition)
                return $"X {position.X:F3}  Y {position.Y:F3}  Z {position.Z:F3}";
            switch (_definition.Mode)
            {
                case TeachMode.Image or TeachMode.XYOnly:
                    return $"X {position.X:F3}  Y {position.Y:F3}";
                case TeachMode.ZOnly:
                    return $"Z {position.Z:F3}";
                default:
                    return $"X {position.X:F3}  Y {position.Y:F3}  Z {position.Z:F3}";
            }
        }
    }

    public void Teach(double x, double y, double z)
    {
        var position = Read();
        if (_definition.Mode is TeachMode.Image or TeachMode.XYOnly or TeachMode.Full)
        {
            position.X = x;
            position.Y = y;
        }

        if (_definition.Mode is TeachMode.Full or TeachMode.ZOnly)
        {
            position.Z = z;
        }
        switch (_definition.Target)
        {
            case TeachingTarget.SafeZ when _definition.MotionGroup == MotionGroup.PcbSupply:
                _settings.PcbSupply.RotationZ = position.Z;
                break;
            case TeachingTarget.SafeZ:
                _settings.BoltFastening.SafeZ = position.Z;
                break;
            case TeachingTarget.SupplyPcb1Pick or TeachingTarget.SupplyPcb2Pick:
                var pick = _definition.Target == TeachingTarget.SupplyPcb1Pick
                    ? _recipes.Current.PcbSupply.Pcb1PickPosition : _recipes.Current.PcbSupply.Pcb2PickPosition;
                (pick.X, pick.Y, pick.Z) = (position.X, position.Y, position.Z);
                break;
            case TeachingTarget.SupplyHandoff:
                var supply = _settings.PcbSupply.HandoffPosition;
                (supply.X, supply.Y, supply.Z) = (position.X, position.Y, position.Z);
                break;
            case TeachingTarget.PlacementHandoff:
                var placement = _settings.PcbPlacementHandler.HandoffPosition;
                (placement.X, placement.Y, placement.Z) = (position.X, position.Y, position.Z);
                break;
            case TeachingTarget.PlacementReceiveZ:
                _settings.PcbPlacementHandler.ReceiveZ = position.Z;
                break;
            case TeachingTarget.HeatSink1PcbPlacement:
                _recipes.Current.PcbPlacement.HeatSink1PcbPlacementPosition = position;
                break;
            case TeachingTarget.HeatSink2PcbPlacement:
                _recipes.Current.PcbPlacement.HeatSink2PcbPlacementPosition = position;
                break;
            case TeachingTarget.BoltPickup:
                _settings.BoltFastening.PickupPosition = position;
                break;
            case TeachingTarget.BoltPosition:
                _definition.Bolt!.FasteningX = position.X;
                _definition.Bolt.FasteningY = position.Y;
                break;
            case TeachingTarget.ShootingHeadFasteningZ:
                _settings.BoltFastening.ShootingHead.FasteningZ = position.Z;
                break;
            case TeachingTarget.PickupHeadFasteningZ:
                _settings.BoltFastening.PickupHead.FasteningZ = position.Z;
                break;
            case TeachingTarget.ShootingHeadUpperLeftLocatingPin:
                _settings.BoltFastening.ShootingHead.UpperLeftLocatingPin = position;
                break;
            case TeachingTarget.ShootingHeadLowerRightLocatingPin:
                _settings.BoltFastening.ShootingHead.LowerRightLocatingPin = position;
                break;
            case TeachingTarget.PickupHeadUpperLeftLocatingPin:
                _settings.BoltFastening.PickupHead.UpperLeftLocatingPin = position;
                break;
            case TeachingTarget.PickupHeadLowerRightLocatingPin:
                _settings.BoltFastening.PickupHead.LowerRightLocatingPin = position;
                break;
            case TeachingTarget.CarrierUpperLeftLocatingPin:
                _settings.CarrierReference.UpperLeftLocatingPin = position;
                break;
            case TeachingTarget.CarrierLowerRightLocatingPin:
                _settings.CarrierReference.LowerRightLocatingPin = position;
                break;
            case TeachingTarget.InspectionWaiting:
                _settings.NgCarrierTransfer.WaitingPosition = position;
                break;
            case TeachingTarget.NgCarrierPickup:
                _settings.NgCarrierTransfer.CarrierPickupPosition = position;
                break;
            case TeachingTarget.NgShuttlePlace:
                _settings.NgCarrierTransfer.ShuttlePlacePosition = position;
                break;
            default:
                throw new InvalidOperationException("Record image positions with the camera capture command.");
        }
        Refresh();
    }

    public AxisPosition Read()
    {
        var position = Coordinates ?? new();
        return new()
        {
            X = position.X,
            Y = position.Y,
            Z = _definition.Mode == TeachMode.Image ? 0 : position.Z,
        };
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(Coordinates));
        OnPropertyChanged(nameof(PositionLabel));
    }
}

public enum TeachingPointGroup
{
    [Description("Work positions")]
    Work,
    [Description("Carrier transfer positions")]
    CarrierTransfer,
    [Description("Reference positions")]
    MachineReference,
    [Description("Fastening positions")]
    Fastening,
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

    [Description("Record Position with both heads raised and the pickup table down; saves automatically. Move to Position: Safe Z → table down → pickup XY → pickup Z. Vacuum is unchanged.")]
    BoltPickup,

    [Description("Record Position saves this head's Z automatically. Move to Position moves only Z. Automatic fastening reaches this Z before lowering the head.")]
    FasteningZ,

    [Description("Record Position updates XY only. Edit Z offset separately, then Save. Move to Position: Safe Z → table down for pickup / up for shooting → bolt XY → head fastening Z + bolt offset. Both heads must be raised.")]
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
