using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace IBTM.Device;

public sealed class IoInputStatus : IoSignal<InputIo>
{
    private readonly IIoService _io;

    public IoInputStatus(
        InputIo signal,
        HardwareArea area,
        IoSection? section,
        IIoService io,
        int? number = null)
        : base(signal, area, section, number)
    {
        _io = io;
    }

    public override bool? IsOn => Number is not null && _io.IsReady ? _io.GetInput(Signal) : null;
}
