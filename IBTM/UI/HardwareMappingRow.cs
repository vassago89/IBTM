using System;
using IBTM.Device;

namespace IBTM.UI;

public sealed class HardwareMappingRow(
    HardwareSettings hardware,
    Enum signal,
    int number,
    Action<HardwareMappingRow> apply,
    double minimum = 0,
    double maximum = 0)
{
    public HardwareArea Area { get; } = hardware.Area;
    public IoSection? Section { get; } = hardware.GetSection(signal);
    public Enum Signal { get; } = signal;
    public int Order { get; } = Convert.ToInt32(signal);
    public int Number { get; set; } = number;
    public int? OffNumber { get; set; }
    public double Minimum { get; set; } = minimum;
    public double Maximum { get; set; } = maximum;

    public void Apply() => apply(this);
}
