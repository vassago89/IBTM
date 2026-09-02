using System;
using IBTM.Device;

namespace IBTM.Virtual.Tests;

internal sealed class TestInspectionGantryClearance : IInspectionGantryClearance
{
    private readonly IIoService _io;

    public TestInspectionGantryClearance(IIoService io)
    {
        _io = io;
        io.InputChanged += OnInputChanged;
    }

    public event Action? Changed;

    public bool IsClear =>
        _io.GetInput(InputIo.NgCarrierPickupUp)
        && !_io.GetInput(InputIo.NgCarrierPickupDown)
        && !_io.GetInput(InputIo.NgCarrierDetected);

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
