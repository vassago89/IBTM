using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.NgConveyor;

public enum NgTransferLiftState
{
    [Description("Up")]
    Up,

    [Description("Between")]
    Between,

    [Description("Down")]
    Down,
}

public enum NgTransferGripperState
{
    [Description("Open")]
    Open,

    [Description("Between")]
    Between,

    [Description("Closed")]
    Closed,
}

public sealed class NgCarrierTransfer : IInspectionGantryClearance
{
    private const double PositionTolerance = 0.05;
    private readonly IIoService _io;
    private readonly IXyMotion _motion;
    private readonly NgConveyorSettings _settings;

    public NgCarrierTransfer(
        IIoService io,
        IXyMotion motion,
        NgConveyorSettings settings)
    {
        _io = io;
        _motion = motion;
        _settings = settings;
        io.InputChanged += OnInputChanged;
    }

    public event Action? Changed;

    public bool CarrierDetected =>
        _io.GetInput(InputIo.NgCarrierDetected);
    public NgTransferLiftState Lift =>
        (_io.GetInput(InputIo.NgCarrierPickupUp),
            _io.GetInput(InputIo.NgCarrierPickupDown)) switch
        {
            (true, false) => NgTransferLiftState.Up,
            (false, true) => NgTransferLiftState.Down,
            _ => NgTransferLiftState.Between,
        };
    public NgTransferGripperState Gripper =>
        (_io.GetInput(InputIo.NgCarrierGripperOpen),
            _io.GetInput(InputIo.NgCarrierGripperClosed)) switch
        {
            (true, false) => NgTransferGripperState.Open,
            (false, true) => NgTransferGripperState.Closed,
            _ => NgTransferGripperState.Between,
        };
    public bool AtCarrier => IsAt(_settings.CarrierPickupPosition);
    public bool AtShuttle => IsAt(_settings.ShuttlePlacePosition);
    public bool Available => Lift == NgTransferLiftState.Up && !CarrierDetected;

    public Task MoveToCarrierAsync(
        CancellationToken cancellationToken = default) =>
        MoveToAsync(_settings.CarrierPickupPosition, cancellationToken);

    public Task MoveToShuttleAsync(
        CancellationToken cancellationToken = default) =>
        MoveToAsync(_settings.ShuttlePlacePosition, cancellationToken);

    public Task SetLiftDownAsync(
        bool down,
        CancellationToken cancellationToken = default) =>
        _io.SetOutputAndWaitAsync(
            OutputIo.NgCarrierPickupDown,
            down,
            cancellationToken);

    public Task SetGripperClosedAsync(
        bool closed,
        CancellationToken cancellationToken = default) =>
        _io.SetOutputAndWaitAsync(
            OutputIo.NgCarrierGripperClose,
            closed,
            cancellationToken);

    public Task WaitForCarrierGripAsync(
        CancellationToken cancellationToken = default) =>
        _io.WaitForInputAsync(
            InputIo.NgCarrierDetected,
            true,
            cancellationToken);

    public Task WaitForShuttleCarrierAsync(
        CancellationToken cancellationToken = default) =>
        _io.WaitForInputAsync(
            InputIo.NgShuttleCarrierDetected,
            true,
            cancellationToken);

    private Task MoveToAsync(
        AxisPos position,
        CancellationToken cancellationToken) =>
        _motion.MoveToXYAsync(
            position.X,
            position.Y,
            _settings.TransferSpeed,
            cancellationToken);

    private bool IsAt(AxisPos position)
    {
        var current = _motion.GetPosition();
        return !_motion.IsMoving
            && _motion.GetAxisState(MotionAxis.X).InPosition
            && _motion.GetAxisState(MotionAxis.Y).InPosition
            && Math.Abs(current.X - position.X) <= PositionTolerance
            && Math.Abs(current.Y - position.Y) <= PositionTolerance;
    }

    private void OnInputChanged(InputIo input, bool _)
    {
        if (input is InputIo.NgCarrierPickupUp
            or InputIo.NgCarrierPickupDown
            or InputIo.NgCarrierDetected)
        {
            Changed?.Invoke();
        }
    }
}
