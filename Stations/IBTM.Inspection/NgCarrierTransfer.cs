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

    public bool CarrierDetected
    {
        get
        {
            return _io.GetInput(InputIo.NgCarrierDetected);
        }
    }

    public NgTransferLiftState Lift
    {
        get
        {
            return (_io.GetInput(InputIo.NgCarrierPickupUp), _io.GetInput(InputIo.NgCarrierPickupDown)) switch
            {
                (true, false) => NgTransferLiftState.Up,
                (false, true) => NgTransferLiftState.Down,
                _ => NgTransferLiftState.Between,
            };
        }
    }

    public NgTransferGripperState Gripper
    {
        get
        {
            return (
                _io.GetInput(InputIo.NgCarrierGripperOpen),
                _io.GetInput(InputIo.NgCarrierGripperClosed)) switch
            {
                (true, false) => NgTransferGripperState.Open,
                (false, true) => NgTransferGripperState.Closed,
                _ => NgTransferGripperState.Between,
            };
        }
    }

    public bool IsRaised
    {
        get
        {
            return Lift == NgTransferLiftState.Up;
        }
    }

    public bool IsClear
    {
        get
        {
            return IsRaised && !CarrierDetected;
        }
    }

    public Task RaiseAsync(CancellationToken cancellationToken = default)
    {
        return SetLiftUpAsync(true, cancellationToken);
    }

    public Task SetLiftUpAsync(bool up, CancellationToken cancellationToken = default)
    {
        return _io.SetOutputAndWaitAsync(OutputIo.NgCarrierPickupUp, up, cancellationToken);
    }

    public Task SetGripperOpenAsync(bool open, CancellationToken cancellationToken = default)
    {
        return _io.SetOutputAndWaitAsync(OutputIo.NgCarrierGripperOpen, open, cancellationToken);
    }

    internal Task WaitForCarrierGripAsync(CancellationToken cancellationToken = default)
    {
        return _io.WaitForInputAsync(InputIo.NgCarrierDetected, true, cancellationToken);
    }

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
