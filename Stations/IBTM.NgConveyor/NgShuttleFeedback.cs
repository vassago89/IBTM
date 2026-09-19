using System;
using IBTM.Device;

namespace IBTM.NgConveyor;

public sealed class NgShuttleFeedback
{
    private readonly IIoService _io;

    public NgShuttleFeedback(IIoService io)
    {
        _io = io;
        io.InputChanged += OnInputChanged;
    }

    public event Action? Changed;

    public bool CarrierDetected => _io.GetInput(InputIo.NgShuttleCarrierDetected);

    public NgShuttleLiftState Lift
    {
        get
        {
            switch ((_io.GetInput(InputIo.NgShuttleUp), _io.GetInput(InputIo.NgShuttleDown)))
            {
                case (true, false):
                    return NgShuttleLiftState.Up;
                case (false, true):
                    return NgShuttleLiftState.Down;
                default:
                    return NgShuttleLiftState.Between;
            }
        }
    }

    private void OnInputChanged(InputIo input, bool _)
    {
        if (input is InputIo.NgShuttleUp
            or InputIo.NgShuttleDown
            or InputIo.NgShuttleCarrierDetected)
        {
            Changed?.Invoke();
        }
    }
}
