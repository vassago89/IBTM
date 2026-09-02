using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;
using IBTM.Inspection;

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
    private readonly IIoService _io;
    private readonly InspectionGantry _gantry;
    private readonly NgConveyorSettings _settings;

    public NgCarrierTransfer(
        IIoService io,
        InspectionGantry gantry,
        NgConveyorSettings settings)
    {
        _io = io;
        _gantry = gantry;
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
    public bool AtCarrier => _gantry.IsAt(_settings.CarrierPickupPosition);
    public bool AtShuttle => _gantry.IsAt(_settings.ShuttlePlacePosition);
    public bool IsClear => Lift == NgTransferLiftState.Up && !CarrierDetected;

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

    private Task MoveToAsync(
        AxisPosition position,
        CancellationToken cancellationToken) =>
        _gantry.MoveToAsync(
            position,
            _settings.TransferSpeed,
            cancellationToken);

    private void OnInputChanged(InputIo input, bool _)
    {
        if (input is InputIo.NgCarrierPickupUp
            or InputIo.NgCarrierPickupDown
            or InputIo.NgCarrierGripperOpen
            or InputIo.NgCarrierGripperClosed
            or InputIo.NgCarrierDetected)
        {
            Changed?.Invoke();
        }
    }
}
