using System;
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
    private readonly IMotionFeedback _supplyMotion;
    private readonly IMotionFeedback _placementMotion;
    private readonly AxisPosition _supplyHandoff;
    private readonly AxisPosition _placementHandoff;
    private readonly Func<double> _placementEntryZ;

    public BufferStage(
        PcbBufferSettings settings,
        IIoService io,
        IBufferPlacementState placementState,
        IMotionFeedback supplyMotion,
        IMotionFeedback placementMotion,
        AxisPosition supplyHandoff,
        AxisPosition placementHandoff,
        Func<double> placementEntryZ)
    {
        _settings = settings;
        _io = io;
        _placementState = placementState;
        _supplyMotion = supplyMotion;
        _placementMotion = placementMotion;
        _supplyHandoff = supplyHandoff;
        _placementHandoff = placementHandoff;
        _placementEntryZ = placementEntryZ;
        supplyMotion.PositionChanged += (_, _, _) => PositionChanged?.Invoke();
        placementMotion.PositionChanged += (_, _, _) => PositionChanged?.Invoke();
        supplyMotion.MovingChanged += _ => StateChanged?.Invoke();
        placementMotion.MovingChanged += _ => StateChanged?.Invoke();
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

    public bool PcbPresent =>
        _io.GetInput(InputIo.PcbBufferPcbPresent);

    private bool PositionKnown =>
        _supplyMotion.GetAxisState(MotionAxis.X).Homed
        && _placementMotion.GetAxisState(MotionAxis.X).Homed
        && _placementMotion.GetAxisState(MotionAxis.Y).Homed
        && _placementMotion.GetAxisState(MotionAxis.Z).Homed;

    public bool SupplyInside =>
        _supplyMotion.GetAxisState(MotionAxis.X).Homed
        && _settings.ContainsSupplyX(_supplyMotion.GetPosition().X);

    public bool PlacementInside =>
        _placementMotion.GetAxisState(MotionAxis.X).Homed
        && _placementMotion.GetAxisState(MotionAxis.Y).Homed
        && IsInsidePlacement(_placementMotion.GetPosition());

    public bool PlacementBlocksSupply =>
        PlacementInside
        && _placementMotion.GetPosition().Z > _placementEntryZ();

    public bool SupplyAtHandoff =>
        IsSettled(_supplyMotion)
        && IsAt(_supplyMotion.GetPosition(), _supplyHandoff);

    public bool PlacementSecuredAtHandoff =>
        PlacementAtHandoff
        && _placementState.PcbSecured;

    private bool PlacementAtHandoff =>
        IsSettled(_placementMotion)
        && IsAt(_placementMotion.GetPosition(), _placementHandoff);

    public bool CanSupplyEnter =>
        PositionKnown && !PcbPresent && !PlacementBlocksSupply;
    public bool CanPlacementEnter =>
        PositionKnown
        && PcbPresent
        && (!SupplyInside || SupplyAtHandoff);
    public bool Conflict
    {
        get
        {
            if (!_supplyMotion.GetAxisState(MotionAxis.X).Homed)
            {
                return false;
            }

            var supplyPosition = _supplyMotion.GetPosition();
            if (!_settings.ContainsSupplyX(supplyPosition.X)
                || !_placementMotion.GetAxisState(MotionAxis.X).Homed
                || !_placementMotion.GetAxisState(MotionAxis.Y).Homed)
            {
                return false;
            }

            var placementPosition = _placementMotion.GetPosition();
            if (!IsInsidePlacement(placementPosition)
                || placementPosition.Z <= _placementEntryZ())
            {
                return false;
            }

            var supplyAtHandoff = IsSettled(_supplyMotion)
                                  && IsAt(
                                      supplyPosition,
                                      _supplyHandoff);
            var placementAtHandoff = IsSettled(_placementMotion)
                                     && IsAt(
                                         placementPosition,
                                         _placementHandoff);
            return !supplyAtHandoff && !placementAtHandoff;
        }
    }

    public Task WaitForPcbAsync(
        bool present,
        CancellationToken cancellationToken = default) =>
        _io.WaitForInputAsync(
            InputIo.PcbBufferPcbPresent,
            present,
            cancellationToken);

    public async Task WaitForSupplyOutsideAsync(
        CancellationToken cancellationToken = default)
    {
        var changed = new AsyncAutoResetEvent();
        void OnStateChanged() => changed.Set();

        StateChanged += OnStateChanged;
        try
        {
            while (SupplyInside)
            {
                await changed.WaitAsync(cancellationToken);
            }
        }
        finally
        {
            StateChanged -= OnStateChanged;
        }
    }

    private static bool IsAt(
        (double X, double Y, double Z) current,
        AxisPosition target) =>
        Math.Abs(current.X - target.X)
            <= MotionService.PositionToleranceMillimeters
        && Math.Abs(current.Y - target.Y)
            <= MotionService.PositionToleranceMillimeters
        && Math.Abs(current.Z - target.Z)
            <= MotionService.PositionToleranceMillimeters;

    private bool IsInsidePlacement((double X, double Y, double Z) position) =>
        Between(
            position.X,
            _settings.PlacementBoundary1.X,
            _settings.PlacementBoundary2.X)
        && Between(
            position.Y,
            _settings.PlacementBoundary1.Y,
            _settings.PlacementBoundary2.Y);

    private static bool Between(
        double value,
        double boundary1,
        double boundary2) =>
        value >= Math.Min(boundary1, boundary2)
        && value <= Math.Max(boundary1, boundary2);

    private static bool IsSettled(IMotionFeedback motion)
    {
        if (motion.IsMoving)
        {
            return false;
        }

        foreach (var axis in motion.Axes)
        {
            if (!motion.GetAxisState(axis).InPosition)
            {
                return false;
            }
        }

        return true;
    }
}
