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

    public bool CarrierDetected
    {
        get
        {
            return _io.GetInput(InputIo.NgShuttleCarrierDetected);
        }
    }

    public NgShuttleLiftState Lift
    {
        get
        {
            return (_io.GetInput(InputIo.NgShuttleUp), _io.GetInput(InputIo.NgShuttleDown)) switch
            {
                (true, false) => NgShuttleLiftState.Up,
                (false, true) => NgShuttleLiftState.Down,
                _ => NgShuttleLiftState.Between,
            };
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
