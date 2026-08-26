using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.PcbBuffer;

public sealed class BufferStage
{
    private const double PositionTolerance = 0.05;

    private readonly PcbBufferSettings _settings;
    private readonly IIoService _io;
    private readonly IBufferPlacementState _placementState;
    private readonly IAxisMotion _supplyMotion;
    private readonly IAxisMotion _placementMotion;
    private readonly AxisPos _supplyHandoff;
    private readonly AxisPos _placementHandoff;
    private readonly Func<double> _placementEntryZ;

    public BufferStage(
        PcbBufferSettings settings,
        IIoService io,
        IBufferPlacementState placementState,
        IAxisMotion supplyMotion,
        IAxisMotion placementMotion,
        AxisPos supplyHandoff,
        AxisPos placementHandoff,
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
        && Between(
            _supplyMotion.GetPosition().X,
            _settings.SupplyBoundary1,
            _settings.SupplyBoundary2);

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
    public bool Conflict =>
        SupplyInside
        && PlacementBlocksSupply
        && !SupplyAtHandoff
        && !PlacementAtHandoff;

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
        using var changed = new AsyncAutoResetEvent();
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
        AxisPos target) =>
        Math.Abs(current.X - target.X) <= PositionTolerance
        && Math.Abs(current.Y - target.Y) <= PositionTolerance
        && Math.Abs(current.Z - target.Z) <= PositionTolerance;

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

    private static bool IsSettled(IAxisMotion motion)
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
