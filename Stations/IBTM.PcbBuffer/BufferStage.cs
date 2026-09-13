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
    private readonly IIoService _io;
    private readonly IBufferPlacementState _placementState;
    private readonly MotionStatus _supplyMotion;
    private readonly MotionStatus _placementMotion;
    private readonly AxisPosition _supplyHandoff;
    private readonly AxisPosition _placementHandoff;
    private readonly Func<double> _placementEntryZ;
    private readonly Func<bool> _supplyEnabled;
    private readonly Func<bool> _placementEnabled;

    public BufferStage(
        PcbBufferSettings settings,
        IIoService io,
        IBufferPlacementState placementState,
        MotionStatus supplyMotion,
        MotionStatus placementMotion,
        AxisPosition supplyHandoff,
        AxisPosition placementHandoff,
        Func<double> placementEntryZ,
        Func<bool>? supplyEnabled = null,
        Func<bool>? placementEnabled = null)
    {
        _settings = settings;
        _io = io;
        _placementState = placementState;
        _supplyMotion = supplyMotion;
        _placementMotion = placementMotion;
        _supplyHandoff = supplyHandoff;
        _placementHandoff = placementHandoff;
        _placementEntryZ = placementEntryZ;
        _supplyEnabled = supplyEnabled ?? (() => true);
        _placementEnabled = placementEnabled ?? (() => true);
        supplyMotion.Feedback.PositionChanged += (_, _, _) => PositionChanged?.Invoke();
        placementMotion.Feedback.PositionChanged += (_, _, _) => PositionChanged?.Invoke();
        supplyMotion.Feedback.MovingChanged += _ => StateChanged?.Invoke();
        placementMotion.Feedback.MovingChanged += _ => StateChanged?.Invoke();
        placementState.Changed += () => StateChanged?.Invoke();
        io.InputChanged += (input, _) =>
        {
            if (input == InputIo.PcbBufferPcbPresent)
            {
                StateChanged?.Invoke();
            }
        };
    }

    public event Action? PositionChanged;
    public event Action? StateChanged;

    public bool PcbPresent
    {
        get
        {
            return _io.GetInput(InputIo.PcbBufferPcbPresent);
        }
    }

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
            && _supplyMotion.IsSettled(live, _supplyMotion.Feedback.Axes.ToArray())
            && IsAt(_supplyMotion.ReadPosition(live), _supplyHandoff);
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

    // Shared-buffer transfers still require both handlers to be enabled and
    // homed; ignoring an unused drive is not permission to enter an unknown zone.
    public bool CanLowerSupply(bool live = true)
    {
        return IsPositionKnown(live) && !BlocksSupply(live);
    }

    public bool CanEnterSupply(bool live = true)
    {
        return CanLowerSupply(live) && !PcbPresent;
    }

    public bool CanEnterPlacement(bool live = true)
    {
        return IsPositionKnown(live) && PcbPresent && (!IsSupplyInside(live) || IsSupplyAtHandoff(live));
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

        var supplyAtHandoff = _supplyMotion.IsSettled(live, _supplyMotion.Feedback.Axes.ToArray()) && IsAt(supplyPosition, _supplyHandoff);
        var placementAtHandoff = _placementMotion.IsSettled(live, _placementMotion.Feedback.Axes.ToArray()) && IsAt(
            placementPosition,
            _placementHandoff);
        return !supplyAtHandoff && !placementAtHandoff;
    }

    public Task WaitForPcbAsync(CancellationToken cancellationToken = default)
    {
        return _io.WaitForInputAsync(InputIo.PcbBufferPcbPresent, true, cancellationToken);
    }

    public async Task WaitForSupplyOutsideAsync(CancellationToken cancellationToken = default)
    {
        var changed = new AsyncAutoResetEvent();
        void OnStateChanged()
        {
            changed.Set();
        }

        StateChanged += OnStateChanged;
        try
        {
            while (IsSupplyInside())
            {
                await changed.WaitAsync(cancellationToken);
            }
        }
        finally
        {
            StateChanged -= OnStateChanged;
        }
    }

    private static bool IsAt((double X, double Y, double Z) current, AxisPosition target)
    {
        return Math.Abs(current.X - target.X) <= MotionService.PositionToleranceMillimeters
            && Math.Abs(current.Y - target.Y) <= MotionService.PositionToleranceMillimeters
            && Math.Abs(current.Z - target.Z) <= MotionService.PositionToleranceMillimeters;
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
