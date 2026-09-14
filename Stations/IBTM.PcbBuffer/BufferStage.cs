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
    private readonly IPcbHandoffState _supplyState;
    private readonly IPcbHandoffState _placementState;
    private readonly MotionStatus _supplyMotion;
    private readonly MotionStatus _placementMotion;
    private readonly XyPosition _supplyHandoff;
    private readonly Func<double> _supplyTransportZ;
    private readonly AxisPosition _placementHandoff;
    private readonly Func<double> _placementEntryZ;
    private readonly Func<bool> _supplyEnabled;
    private readonly Func<bool> _placementEnabled;

    public BufferStage(
        PcbBufferSettings settings,
        IPcbHandoffState supplyState,
        IPcbHandoffState placementState,
        MotionStatus supplyMotion,
        MotionStatus placementMotion,
        XyPosition supplyHandoff,
        AxisPosition placementHandoff,
        Func<double> supplyTransportZ,
        Func<double> placementEntryZ,
        Func<bool>? supplyEnabled = null,
        Func<bool>? placementEnabled = null)
    {
        _settings = settings;
        _supplyState = supplyState;
        _placementState = placementState;
        _supplyMotion = supplyMotion;
        _placementMotion = placementMotion;
        _supplyHandoff = supplyHandoff;
        _supplyTransportZ = supplyTransportZ;
        _placementHandoff = placementHandoff;
        _placementEntryZ = placementEntryZ;
        _supplyEnabled = supplyEnabled ?? (() => true);
        _placementEnabled = placementEnabled ?? (() => true);
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
        return _supplyEnabled()
            && _placementEnabled()
            && _supplyMotion.IsReady(live)
            && _placementMotion.IsReady(live)
            && _supplyMotion.ReadAxisState(MotionAxis.X, live).Homed
            && _placementMotion.ReadAxisState(MotionAxis.X, live).Homed
            && _placementMotion.ReadAxisState(MotionAxis.Y, live).Homed
            && _placementMotion.ReadAxisState(MotionAxis.Z, live).Homed;
    }

    public bool IsSupplyInside(bool live = true)
    {
        return _supplyEnabled()
            && _supplyMotion.IsReady(live)
            && _supplyMotion.ReadAxisState(MotionAxis.X, live).Homed
            && _settings.ContainsSupplyX(_supplyMotion.ReadPosition(live).X);
    }

    public bool IsPlacementInside(bool live = true)
    {
        return _placementEnabled()
            && _placementMotion.IsReady(live)
            && _placementMotion.ReadAxisState(MotionAxis.X, live).Homed
            && _placementMotion.ReadAxisState(MotionAxis.Y, live).Homed
            && IsInsidePlacement(_placementMotion.ReadPosition(live));
    }

    private bool BlocksSupply(bool live = true)
    {
        if (!_placementMotion.ReadAxisState(MotionAxis.X, live).Homed
            || !_placementMotion.ReadAxisState(MotionAxis.Y, live).Homed)
        {
            return false;
        }

        var position = _placementMotion.ReadPosition(live);
        return IsInsidePlacement(position) && position.Z > _placementEntryZ();
    }

    public bool IsSupplyAtHandoff(bool live = true)
    {
        return _supplyEnabled()
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
        return _placementEnabled()
            && _placementMotion.IsReady(live)
            && _placementMotion.IsSettled(live, _placementMotion.Feedback.Axes.ToArray())
            && IsAt(_placementMotion.ReadPosition(live), _placementHandoff);
    }

    // Direct handoffs still require both handlers to be enabled and
    // homed; ignoring an unused drive is not permission to enter an unknown zone.
    public bool CanEnterSupply(bool live = true)
    {
        return IsPositionKnown(live) && !BlocksSupply(live);
    }

    public bool CanEnterPlacement(bool live = true)
    {
        return IsPositionKnown(live)
            && IsSupplyAtHandoff(live)
            && _supplyState.PcbSecured;
    }

    public bool HasConflict(bool live = true)
    {
        if (!_supplyEnabled()
            || !_placementEnabled()
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
            || placementPosition.Z <= _placementEntryZ())
        {
            return false;
        }

        var supplyAtHandoff = IsSupplyAtHandoff(live);
        var placementAtHandoff = _placementMotion.IsSettled(live, _placementMotion.Feedback.Axes.ToArray()) && IsAt(
            placementPosition,
            _placementHandoff);
        return !supplyAtHandoff && !placementAtHandoff;
    }

    public bool IsSupplyOutside(bool live = true)
    {
        return _supplyEnabled()
            && _supplyMotion.IsReady(live)
            && _supplyMotion.ReadAxisState(MotionAxis.X, live).Homed
            && !_settings.ContainsSupplyX(_supplyMotion.ReadPosition(live).X);
    }

    public async Task WaitForSupplyOutsideAsync(CancellationToken cancellationToken = default)
    {
        var changed = new AsyncAutoResetEvent();
        void OnStateChanged()
        {
            changed.Set();
        }

        PositionChanged += OnStateChanged;
        StateChanged += OnStateChanged;
        try
        {
            while (!IsSupplyOutside())
            {
                await changed.WaitAsync(cancellationToken);
            }
        }
        finally
        {
            PositionChanged -= OnStateChanged;
            StateChanged -= OnStateChanged;
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
        return Between(position.X, _settings.PlacementBoundary1.X, _settings.PlacementBoundary2.X)
            && Between(position.Y, _settings.PlacementBoundary1.Y, _settings.PlacementBoundary2.Y);
    }

    private static bool Between(double value, double boundary1, double boundary2)
    {
        return value >= Math.Min(boundary1, boundary2)
            && value <= Math.Max(boundary1, boundary2);
    }

}
