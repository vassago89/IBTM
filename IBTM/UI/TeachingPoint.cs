using System;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using IBTM.Core;
using IBTM.Device;
using IBTM.Storage;

namespace IBTM.UI;

public class TeachingPoint : ObservableObject
{
    private readonly MachineSettings _settings;
    private readonly RecipeManager _recipes;
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
        Inspection = definition.Target is TeachingTarget.DataMatrix or TeachingTarget.BoltReference
            ? new(recipes.Current, pcb, definition.Bolt) : null;
    }

    public InspectionPoint? Inspection { get; }

    public TeachingPosition Position => _definition with { HasPosition = Coordinates is not null };

    public TeachingStorage Storage
    {
        get
        {
            if (_definition.Target is TeachingTarget.SupplyHandoff or TeachingTarget.PlacementHandoff)
                return TeachingStorage.Handoff;
            return Setting is null ? TeachingStorage.Recipe : TeachingStorage.Machine;
        }
    }

    public Setting? Setting
    {
        get
        {
            switch (_definition.Target)
            {
                case TeachingTarget.SupplyHandoff:
                    return _settings.PcbSupply;
                case TeachingTarget.PlacementHandoff or TeachingTarget.PlacementReceiveZ:
                    return _settings.PcbPlacementHandler;
                case TeachingTarget.SafeZ:
                    return _definition.MotionGroup == MotionGroup.PcbSupply
                        ? _settings.PcbSupply : _settings.BoltFastening;
                case TeachingTarget.ShootingSafeZ or TeachingTarget.BoltPickup
                    or TeachingTarget.ShootingHeadFasteningZ or TeachingTarget.PickupHeadFasteningZ
                    or TeachingTarget.ShootingHeadUpperLeftLocatingPin or TeachingTarget.ShootingHeadLowerRightLocatingPin
                    or TeachingTarget.PickupHeadUpperLeftLocatingPin or TeachingTarget.PickupHeadLowerRightLocatingPin:
                    return _settings.BoltFastening;
                case TeachingTarget.CarrierUpperLeftLocatingPin or TeachingTarget.CarrierLowerRightLocatingPin:
                    return _settings.CarrierReference;
                case TeachingTarget.InspectionWaiting or TeachingTarget.NgCarrierPickup or TeachingTarget.NgShuttlePlace:
                    return _settings.NgCarrierTransfer;
                default:
                    return null;
            }
        }
    }

    public AxisPosition? Coordinates
    {
        get
        {
            var recipe = _recipes.Current;
            switch (_definition.Target)
            {
                case TeachingTarget.SafeZ:
                    return new() { Z = _definition.MotionGroup == MotionGroup.PcbSupply
                        ? _settings.PcbSupply.TravelZ : _settings.BoltFastening.SafeZ };
                case TeachingTarget.ShootingSafeZ:
                    return new() { Z = _settings.BoltFastening.GetSafeZ(FasteningHead.Shooting) };
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
                case TeachingTarget.BoltFinalPosition:
                    return _definition.Bolt!.FinalFasteningPosition;
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
                case TeachingTarget.DataMatrix or TeachingTarget.BoltReference:
                    return Inspection!.Position;
                default:
                    throw new ArgumentOutOfRangeException(nameof(_definition.Target));
            }
        }
    }

    public string? BoltName
    {
        get => _definition.Bolt?.Name;
        set
        {
            if (_definition.Bolt is not { } bolt || bolt.Name == value)
                return;
            bolt.Name = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(BoltLabel));
            OnPropertyChanged(nameof(Name));
        }
    }

    public string? BoltLabel
    {
        get
        {
            if (_definition.Bolt is not { } bolt)
                return null;
            return _recipes.Current.Pcb.GetBoltName(bolt.Id);
        }
    }

    public string Name
    {
        get
        {
            if (Inspection is { } inspection)
                return inspection.Name;
            if (_definition.Bolt is { } bolt)
            {
                if (_definition.Target == TeachingTarget.BoltFinalPosition)
                    return UiText.Format($"{BoltLabel} Shooting final tightening");
                return _definition.Target == TeachingTarget.BoltPosition
                    ? UiText.Format($"{BoltLabel} Fastening · {UiText.Get(bolt.Head)}")
                    : UiText.Format($"{BoltLabel} Inspection");
            }

            switch ((_definition.Target, _definition.MotionGroup))
            {
                case (TeachingTarget.SafeZ, MotionGroup.PcbSupply):
                    return UiText.Get("PCB Travel Z");
                default:
                    return UiText.Get(_definition.Target);
            }
        }
    }

    public TeachingPointGroup Group
    {
        get
        {
            switch (_definition.Target)
            {
                case TeachingTarget.BoltFinalPosition:
                    return TeachingPointGroup.ShootingFastening;
                case TeachingTarget.BoltPosition or TeachingTarget.BoltReference:
                    return _definition.Bolt!.Head == FasteningHead.Shooting
                        ? TeachingPointGroup.ShootingFastening : TeachingPointGroup.PickupFastening;
                case TeachingTarget.InspectionWaiting or TeachingTarget.NgCarrierPickup or TeachingTarget.NgShuttlePlace:
                    return TeachingPointGroup.CarrierTransfer;
                case TeachingTarget.SafeZ or TeachingTarget.ShootingSafeZ or TeachingTarget.ShootingHeadFasteningZ or TeachingTarget.PickupHeadFasteningZ
                    or TeachingTarget.CarrierUpperLeftLocatingPin or TeachingTarget.CarrierLowerRightLocatingPin
                    or TeachingTarget.ShootingHeadUpperLeftLocatingPin or TeachingTarget.ShootingHeadLowerRightLocatingPin
                    or TeachingTarget.PickupHeadUpperLeftLocatingPin or TeachingTarget.PickupHeadLowerRightLocatingPin:
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
            if (Inspection is { } inspection)
                return inspection.PositionLabel;
            if (Coordinates is not { } position)
                return UiText.Get("Not taught");
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
        var current = Coordinates;
        var position = new AxisPosition
        {
            X = current?.X ?? 0,
            Y = current?.Y ?? 0,
            Z = _definition.Mode == TeachMode.Image ? 0 : current?.Z ?? 0,
        };
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
                _settings.PcbSupply.TravelZ = position.Z;
                break;
            case TeachingTarget.SafeZ:
                _settings.BoltFastening.SafeZ = position.Z;
                break;
            case TeachingTarget.ShootingSafeZ:
                _settings.BoltFastening.ShootingSafeZ = position.Z;
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
            case TeachingTarget.BoltFinalPosition:
                _definition.Bolt!.FinalFasteningPosition = position;
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
                throw new InvalidOperationException(UiText.Get("Record image positions with the camera capture command."));
        }
        Refresh();
    }

    public AxisPosition MovePosition
    {
        get
        {
            var position = Coordinates ?? throw new MotionInterlockException(
                UiText.Get("Record the selected teaching position before moving."));
            return new()
            {
                X = position.X,
                Y = position.Y,
                Z = _definition.Mode == TeachMode.Image ? 0 : position.Z,
            };
        }
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
    [Description("Shooting")]
    ShootingFastening,
    [Description("Pickup")]
    PickupFastening,
}
