using System;
using System.Text.Json.Serialization;
using IBTM.Core;

namespace IBTM;

public sealed class UnitSettings : Setting
{
    public bool MainConveyor { get; set; } = true;
    public bool PcbSupply { get; set; } = true;
    public bool PcbPlacement { get; set; } = true;
    public bool PickupBoltFeeder { get; set; } = true;
    public bool ShootingBoltFeeder { get; set; } = true;
    public bool BoltFastening { get; set; } = true;
    public bool Inspection { get; set; } = true;
    public bool NgConveyor { get; set; } = true;

    public bool IsBoltFeederEnabled(FasteningHead head)
    {
        switch (head)
        {
            case FasteningHead.Pickup:
                return PickupBoltFeeder;
            case FasteningHead.Shooting:
                return ShootingBoltFeeder;
            default:
                throw new ArgumentOutOfRangeException(nameof(head));
        }
    }

    public bool IsMotionEnabled(MotionGroup group)
    {
        switch (group)
        {
            case MotionGroup.PcbSupply:
                return PcbSupply;
            case MotionGroup.PcbPlacementHandler:
                return PcbPlacement;
            case MotionGroup.BoltFastening:
                return BoltFastening;
            // Inspection and NG transfer use the same physical XY gantry.
            case MotionGroup.InspectionGantry:
                return Inspection;
            default:
                return false;
        }
    }

    [JsonIgnore]
    public bool IsAnyUnitEnabled
    {
        get
        {
            return MainConveyor
                || PcbSupply
                || PcbPlacement
                || PickupBoltFeeder
                || ShootingBoltFeeder
                || BoltFastening
                || Inspection
                || NgConveyor;
        }
    }
}
