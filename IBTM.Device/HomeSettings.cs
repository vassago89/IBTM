using IBTM.Core;

namespace IBTM.Device;

public sealed class HomeSettings : Setting
{
    public double HorizontalSpeed { get; set; } = 15.0;
    public double ZSpeed { get; set; } = 10.0;
}
