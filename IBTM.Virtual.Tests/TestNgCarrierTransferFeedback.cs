using System;
using IBTM.Device;

namespace IBTM.Virtual.Tests;

internal sealed class TestNgCarrierTransferFeedback : INgCarrierTransferFeedback
{
    private readonly IIoService _io;

    public TestNgCarrierTransferFeedback(IIoService io)
    {
        _io = io;
        io.InputChanged += OnInputChanged;
    }

    public event Action? Changed;

    public bool IsRaised
    {
        get
        {
            return _io.GetInput(InputIo.NgCarrierPickupUp)
                && !_io.GetInput(InputIo.NgCarrierPickupDown);
        }
    }

    public bool IsClear
    {
        get
        {
            return IsRaised && !_io.GetInput(InputIo.NgCarrierDetected);
        }
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
