using System;
using System.Linq;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.PcbBuffer;

public sealed class BufferStage
{
    private readonly IPcbHandoffSource _supplyState;
    private readonly IPcbHandoffReceiver _placementState;
    private readonly MotionStatus _supplyMotion;
    private readonly MotionStatus _placementMotion;
    private readonly AxisPosition _supplyHandoff;
    private readonly AxisPosition _placementHandoff;
    private readonly UnitSettings _units;

    public BufferStage(
        IPcbHandoffSource supplyState,
        IPcbHandoffReceiver placementState,
        MotionStatus supplyMotion,
        MotionStatus placementMotion,
        AxisPosition supplyHandoff,
        AxisPosition placementHandoff,
        UnitSettings units)
    {
        _supplyState = supplyState;
        _placementState = placementState;
        _supplyMotion = supplyMotion;
        _placementMotion = placementMotion;
        _supplyHandoff = supplyHandoff;
        _placementHandoff = placementHandoff;
        _units = units;
        supplyMotion.Feedback.PositionChanged += (_, _, _) => StateChanged?.Invoke();
        placementMotion.Feedback.PositionChanged += (_, _, _) => StateChanged?.Invoke();
        supplyMotion.Feedback.MovingChanged += _ => StateChanged?.Invoke();
        placementMotion.Feedback.MovingChanged += _ => StateChanged?.Invoke();
        placementState.Changed += () => StateChanged?.Invoke();
        supplyState.Changed += () => StateChanged?.Invoke();
    }

    public event Action? StateChanged;

    public bool IsSupplyAtHandoff(bool live = true)
    {
        return _units.PcbSupply && IsAtHandoff(_supplyMotion, _supplyHandoff, live);
    }

    public bool IsPlacementAtHandoff(bool live = true)
    {
        return _units.PcbPlacement && IsAtHandoff(_placementMotion, _placementHandoff, live);
    }

    public bool IsPlacementSecuredAtHandoff(bool live = true)
    {
        return IsPlacementAtHandoff(live) && _placementState.PcbSecured;
    }

    public bool IsPlacementEntryAllowed(bool live = true)
    {
        return IsSupplyAtHandoff(live) && _supplyState.PcbSecured;
    }

    public bool IsPlacementRaiseAllowed(bool live = true)
    {
        return _supplyState.PcbReleased && IsPlacementSecuredAtHandoff(live);
    }

    public bool IsSupplyExitAllowed => _supplyState.PcbReleased && _placementState.HandlerRaised;

    private static bool IsAtHandoff(MotionStatus motion, AxisPosition target, bool live)
    {
        if (!motion.IsReady(live)
            || !motion.ReadAxisState(MotionAxis.X, live).Homed
            || !motion.ReadAxisState(MotionAxis.Y, live).Homed
            || !motion.ReadAxisState(MotionAxis.Z, live).Homed
            || !motion.IsSettled(live, motion.Feedback.Axes.ToArray()))
            return false;
        var current = motion.ReadPosition(live);
        return Math.Abs(current.X - target.X) <= MotionService.PositionToleranceMillimeters
            && Math.Abs(current.Y - target.Y) <= MotionService.PositionToleranceMillimeters
            && Math.Abs(current.Z - target.Z) <= MotionService.PositionToleranceMillimeters;
    }
}
