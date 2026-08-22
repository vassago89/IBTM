using IBTM.Core;
using IBTM.Device;

namespace IBTM.PcbPlacement;

public sealed class PcbPlacementHandlerSettings : Setting
{
    public MotionSettings Motion { get; set; } = new();
    public AxisPos BufferHandoffPosition { get; set; } = new();
}
