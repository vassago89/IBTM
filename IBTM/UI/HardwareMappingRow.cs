using System;
using IBTM.Device;

namespace IBTM.UI;

public sealed class HardwareMappingRow(
    HardwareSettings hardware,
    Enum signal)
{
    public HardwareSettings Hardware { get; } = hardware;
    public HardwareArea Area { get; } = hardware.Area;
    public IoSection? Section { get; } = hardware.GetSection(signal);
    public Enum Signal { get; } = signal;
    public int Order { get; } = Convert.ToInt32(signal);
    public int Number { get; set; }
    public OutputHardware? Output { get; init; }
    public AxisHardware? Axis { get; init; }
}
