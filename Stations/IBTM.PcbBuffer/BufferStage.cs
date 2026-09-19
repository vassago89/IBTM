using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.PcbBuffer;

public sealed class BufferStage
{
    private readonly PcbBufferSettings _settings;
    private readonly IPcbHandoffSource _supplyState;
    private readonly IPcbHandoffReceiver _placementState;
    private readonly MotionStatus _supplyMotion;
    private readonly MotionStatus _placementMotion;
    private readonly XyPosition _supplyHandoff;
    private readonly Func<double> _supplyTransportZ;
    private readonly AxisPosition _placementHandoff;
    private readonly UnitSettings _units;

    public BufferStage(
        PcbBufferSettings settings,
        IPcbHandoffSource supplyState,
        IPcbHandoffReceiver placementState,
        MotionStatus supplyMotion,
        MotionStatus placementMotion,
        XyPosition supplyHandoff,
        AxisPosition placementHandoff,
        Func<double> supplyTransportZ,
        UnitSettings units)
    {
        _settings = settings;
        _supplyState = supplyState;
        _placementState = placementState;
        _supplyMotion = supplyMotion;
        _placementMotion = placementMotion;
        _supplyHandoff = supplyHandoff;
        _supplyTransportZ = supplyTransportZ;
        _placementHandoff = placementHandoff;
        _units = units;
        supplyMotion.Feedback.PositionChanged += (_, _, _) => PositionChanged?.Invoke();
        placementMotion.Feedback.PositionChanged += (_, _, _) => PositionChanged?.Invoke();
        supplyMotion.Feedback.MovingChanged += _ => StateChanged?.Invoke();
        placementMotion.Feedback.MovingChanged += _ => StateChanged?.Invoke();
        placementState.Changed += () => StateChanged?.Invoke();
        supplyState.Changed += () => StateChanged?.Invoke();
    }

    public event Action? PositionChanged;
    public event Action? StateChanged;

    private bool IsPositionKnown(bool live = true)
    {
        return _units.PcbSupply
            && _units.PcbPlacement
            && _supplyMotion.IsReady(live)
            && _placementMotion.IsReady(live)
            && _supplyMotion.ReadAxisState(MotionAxis.X, live).Homed
            && _placementMotion.ReadAxisState(MotionAxis.X, live).Homed
            && _placementMotion.ReadAxisState(MotionAxis.Y, live).Homed
            && _placementMotion.ReadAxisState(MotionAxis.Z, live).Homed;
    }

    public bool IsSupplyInside(bool live = true)
    {
        return _units.PcbSupply
            && _supplyMotion.IsReady(live)
            && _supplyMotion.ReadAxisState(MotionAxis.X, live).Homed
            && _settings.ContainsSupplyX(_supplyMotion.ReadPosition(live).X);
    }

    public bool IsPlacementInside(bool live = true)
    {
        return _units.PcbPlacement
            && _placementMotion.IsReady(live)
            && _placementMotion.ReadAxisState(MotionAxis.X, live).Homed
            && _placementMotion.ReadAxisState(MotionAxis.Y, live).Homed
            && IsInsidePlacement(_placementMotion.ReadPosition(live));
    }

    public bool IsSupplyAtHandoff(bool live = true)
    {
        return _units.PcbSupply
            && _supplyMotion.IsReady(live)
            && _supplyMotion.ReadAxisState(MotionAxis.X, live).Homed
            && _supplyMotion.ReadAxisState(MotionAxis.Y, live).Homed
            && _supplyMotion.ReadAxisState(MotionAxis.Z, live).Homed
            && _supplyMotion.IsSettled(live, _supplyMotion.Feedback.Axes.ToArray())
            && IsAtSupplyHandoff(_supplyMotion.ReadPosition(live));
    }

    public bool IsPlacementSecuredAtHandoff(bool live = true)
    {
        return IsPlacementAtHandoff(live) && _placementState.PcbSecured;
    }

    public bool IsPlacementAtHandoff(bool live = true)
    {
        return _units.PcbPlacement
            && _placementMotion.IsReady(live)
            && _placementMotion.IsSettled(live, _placementMotion.Feedback.Axes.ToArray())
            && IsAt(_placementMotion.ReadPosition(live), _placementHandoff);
    }

    // Direct handoffs still require both handlers to be enabled and
    // homed; ignoring an unused drive is not permission to enter an unknown zone.
    public bool IsSupplyEntryAllowed(bool live = true)
    {
        return IsPositionKnown(live)
            && (!IsPlacementInside(live) || _placementState.HandlerRaised);
    }

    // Permission to lower the receiving cylinder; approach motion is independent.
    public bool IsPlacementEntryAllowed(bool live = true)
    {
        return IsPositionKnown(live)
            && IsSupplyAtHandoff(live)
            && _supplyState.PcbSecured;
    }

    public bool IsPlacementRaiseAllowed(bool live = true)
    {
        return IsPositionKnown(live)
            && _supplyState.PcbReleased
            && IsPlacementSecuredAtHandoff(live);
    }

    public bool IsSupplyExitAllowed(bool live = true)
    {
        return IsPositionKnown(live)
            && _supplyState.PcbReleased
            && _placementState.HandlerRaised;
    }

    public bool HasConflict(bool live = true)
    {
        if (!_units.PcbSupply
            || !_units.PcbPlacement
            || !_supplyMotion.IsReady(live)
            || !_placementMotion.IsReady(live)
            || !_supplyMotion.ReadAxisState(MotionAxis.X, live).Homed)
        {
            return false;
        }

        var supplyPosition = _supplyMotion.ReadPosition(live);
        if (!_settings.ContainsSupplyX(supplyPosition.X)
            || !_placementMotion.ReadAxisState(MotionAxis.X, live).Homed
            || !_placementMotion.ReadAxisState(MotionAxis.Y, live).Homed)
        {
            return false;
        }

        var placementPosition = _placementMotion.ReadPosition(live);
        if (!IsInsidePlacement(placementPosition)
            || _placementState.HandlerRaised)
        {
            return false;
        }

        var supplyAtHandoff = IsSupplyAtHandoff(live);
        var placementAtHandoff = _placementMotion.IsSettled(live, _placementMotion.Feedback.Axes.ToArray()) && IsAt(
            placementPosition,
            _placementHandoff);
        return _supplyMotion.ReadAxisState(MotionAxis.X, live).InMotion
            || _supplyMotion.ReadAxisState(MotionAxis.Y, live).InMotion
            || !supplyAtHandoff && !placementAtHandoff;
    }

    public bool IsSupplyOutside(bool live = true)
    {
        return _units.PcbSupply
            && _supplyMotion.IsReady(live)
            && _supplyMotion.ReadAxisState(MotionAxis.X, live).Homed
            && !_settings.ContainsSupplyX(_supplyMotion.ReadPosition(live).X);
    }

    public async Task WaitForSupplyOutsideAsync(CancellationToken cancellationToken = default)
    {
        var changed = new AsyncAutoResetEvent();

        PositionChanged += changed.Set;
        StateChanged += changed.Set;
        try
        {
            while (!IsSupplyOutside())
            {
                await changed.WaitAsync(cancellationToken);
            }
        }
        finally
        {
            PositionChanged -= changed.Set;
            StateChanged -= changed.Set;
        }
    }

    private static bool IsAt((double X, double Y, double Z) current, AxisPosition target)
    {
        return Math.Abs(current.X - target.X) <= MotionService.PositionToleranceMillimeters
            && Math.Abs(current.Y - target.Y) <= MotionService.PositionToleranceMillimeters
            && Math.Abs(current.Z - target.Z) <= MotionService.PositionToleranceMillimeters;
    }

    private bool IsAtSupplyHandoff((double X, double Y, double Z) current)
    {
        return Math.Abs(current.X - _supplyHandoff.X) <= MotionService.PositionToleranceMillimeters
            && Math.Abs(current.Y - _supplyHandoff.Y) <= MotionService.PositionToleranceMillimeters
            && Math.Abs(current.Z - _supplyTransportZ()) <= MotionService.PositionToleranceMillimeters;
    }

    private bool IsInsidePlacement((double X, double Y, double Z) position)
    {
        return IsBetween(position.X, _settings.PlacementBoundary1.X, _settings.PlacementBoundary2.X)
            && IsBetween(position.Y, _settings.PlacementBoundary1.Y, _settings.PlacementBoundary2.Y);
    }

    private static bool IsBetween(double value, double boundary1, double boundary2)
    {
        return value >= Math.Min(boundary1, boundary2)
            && value <= Math.Max(boundary1, boundary2);
    }
}
