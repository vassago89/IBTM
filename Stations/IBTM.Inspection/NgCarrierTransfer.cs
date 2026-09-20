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

    // Presence near the pickup does not prove that its gripper holds the carrier.
    public bool CarrierDetected => _io.GetInput(InputIo.NgCarrierDetected);

    // Unfinished pickup/release ownership, not proof that material is present.
    public bool IsTransferPending
    {
        get;
        private set
        {
            if (field == value)
                return;
            field = value;
            Changed?.Invoke();
        }
    }

    public NgTransferLiftState Lift
    {
        get
        {
            switch ((_io.GetInput(InputIo.NgCarrierPickupUp), _io.GetInput(InputIo.NgCarrierPickupDown)))
            {
                case (true, false):
                    return NgTransferLiftState.Up;
                case (false, true):
                    return NgTransferLiftState.Down;
                default:
                    return NgTransferLiftState.Between;
            }
        }
    }

    public NgTransferGripperState Gripper
    {
        get
        {
            switch ((
                _io.GetInput(InputIo.NgCarrierGripperOpen),
                _io.GetInput(InputIo.NgCarrierGripperClosed)))
            {
                case (true, false):
                    return NgTransferGripperState.Open;
                case (false, true):
                    return NgTransferGripperState.Closed;
                default:
                    return NgTransferGripperState.Between;
            }
        }
    }

    public bool IsRaised => Lift == NgTransferLiftState.Up;

    public bool IsClear => IsRaised && !IsTransferPending;

    public Task SetLiftUpAsync(bool up, CancellationToken cancellationToken = default)
    {
        if (up && IsTransferPending && Gripper != NgTransferGripperState.Closed)
            throw new MotionInterlockException("Confirm the NG gripper is closed before raising the pending transfer.");
        return _io.SetOutputAndWaitAsync(OutputIo.NgCarrierPickupDown, !up, cancellationToken);
    }

    public Task SetGripperOpenAsync(bool open, CancellationToken cancellationToken = default)
    {
        return _io.SetOutputAndWaitAsync(OutputIo.NgCarrierGripperClose, !open, cancellationToken);
    }

    internal async Task GripForTransferAsync(CancellationToken cancellationToken, bool allowEmpty)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IsTransferPending = true;
        try
        {
            await SetGripperOpenAsync(false, cancellationToken);
            if (!allowEmpty)
                await _io.WaitForInputAsync(InputIo.NgCarrierDetected, true, cancellationToken);
        }
        finally
        {
            if (Gripper == NgTransferGripperState.Open)
                IsTransferPending = false;
        }
    }

    private void OnInputChanged(InputIo input, bool value)
    {
        if (input is InputIo.NgCarrierGripperOpen or InputIo.NgCarrierGripperClosed
            && Gripper == NgTransferGripperState.Open)
            IsTransferPending = false;
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
