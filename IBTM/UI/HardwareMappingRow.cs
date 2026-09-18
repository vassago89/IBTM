using System;
using IBTM.Device;

namespace IBTM.UI;

public sealed class HardwareMappingRow
{
    public HardwareMappingRow(
        HardwareSettings hardware,
        Enum signal)
    {
        Hardware = hardware;
        Signal = signal;
    }

    public HardwareSettings Hardware { get; }
    public Enum Signal { get; }

    public HardwareArea Area
    {
        get
        {
            return Hardware.Area;
        }
    }

    public IoSection? Section
    {
        get
        {
            return Hardware.GetSection(Signal);
        }
    }

    public int Order
    {
        get
        {
            return Convert.ToInt32(Signal);
        }
    }

    public int Number { get; set; }
    public OutputHardware? Output { get; init; }
    public AxisHardware? Axis { get; init; }
}
