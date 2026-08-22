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
    private readonly IAxisMotion _supplyMotion;
    private readonly IAxisMotion _placementMotion;
    private readonly AxisPos _supplyHandoff;
    private readonly AxisPos _placementHandoff;

    public BufferStage(
        PcbBufferSettings settings,
        IIoService io,
        IAxisMotion supplyMotion,
        IAxisMotion placementMotion,
        AxisPos supplyHandoff,
        AxisPos placementHandoff)
    {
        _settings = settings;
        _io = io;
        _supplyMotion = supplyMotion;
        _placementMotion = placementMotion;
        _supplyHandoff = supplyHandoff;
        _placementHandoff = placementHandoff;
        supplyMotion.PositionChanged += (_, _, _) => PositionChanged?.Invoke();
        placementMotion.PositionChanged += (_, _, _) => PositionChanged?.Invoke();
        supplyMotion.MovingChanged += _ => StateChanged?.Invoke();
        placementMotion.MovingChanged += _ => StateChanged?.Invoke();
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

    public bool PositionKnown =>
        _supplyMotion.GetAxisState(MotionAxis.X).Homed
        && _placementMotion.GetAxisState(MotionAxis.X).Homed
        && _placementMotion.GetAxisState(MotionAxis.Y).Homed;

    public bool SupplyInside =>
        _supplyMotion.GetAxisState(MotionAxis.X).Homed
        && _settings.ContainsSupply(_supplyMotion.GetPosition().X);

    public bool PlacementInside =>
        _placementMotion.GetAxisState(MotionAxis.X).Homed
        && _placementMotion.GetAxisState(MotionAxis.Y).Homed
        && _settings.ContainsPlacement(
            _placementMotion.GetPosition().X,
            _placementMotion.GetPosition().Y);

    public bool SupplyAtHandoff =>
        IsSettled(_supplyMotion)
        && IsAt(_supplyMotion.GetPosition(), _supplyHandoff);

    public bool PlacementAtHandoff =>
        IsSettled(_placementMotion)
        && IsAt(_placementMotion.GetPosition(), _placementHandoff);

    public bool Occupied => SupplyInside || PlacementInside;
    public bool CanSupplyEnter =>
        PositionKnown && !PcbPresent && !PlacementInside;
    public bool CanPlacementEnter =>
        PositionKnown
        && PcbPresent
        && (!SupplyInside || SupplyAtHandoff);
    public bool CanPlacementExit => !SupplyInside;
    public bool Conflict =>
        SupplyInside
        && PlacementInside
        && !SupplyAtHandoff
        && !PlacementAtHandoff;

    public Task WaitForPcbAsync(
        bool present,
        CancellationToken cancellationToken = default) =>
        _io.WaitForInputAsync(
            InputIo.PcbBufferPcbPresent,
            present,
            cancellationToken);

    private static bool IsAt(
        (double X, double Y, double Z) current,
        AxisPos target) =>
        Math.Abs(current.X - target.X) <= PositionTolerance
        && Math.Abs(current.Y - target.Y) <= PositionTolerance
        && Math.Abs(current.Z - target.Z) <= PositionTolerance;

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
