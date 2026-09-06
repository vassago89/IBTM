using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Device;

namespace IBTM.Inspection;

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

public sealed class NgCarrierTransfer : INgCarrierTransferFeedback
{
    private readonly IIoService _io;

    public NgCarrierTransfer(IIoService io)
    {
        _io = io;
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
    public bool IsRaised => Lift == NgTransferLiftState.Up;
    public bool IsClear => IsRaised && !CarrierDetected;

    internal Task SetLiftDownAsync(
        bool down,
        CancellationToken cancellationToken = default) =>
        _io.SetOutputAndWaitAsync(
            OutputIo.NgCarrierPickupDown,
            down,
            cancellationToken);

    internal Task SetGripperClosedAsync(
        bool closed,
        CancellationToken cancellationToken = default) =>
        _io.SetOutputAndWaitAsync(
            OutputIo.NgCarrierGripperClose,
            closed,
            cancellationToken);

    internal Task WaitForCarrierGripAsync(
        CancellationToken cancellationToken = default) =>
        _io.WaitForInputAsync(
            InputIo.NgCarrierDetected,
            true,
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
