using System.ComponentModel;

namespace IBTM.Device;

public enum MotionCommand
{
    [Description("Idle")]
    None,
    [Description("Positioning")]
    Positioning,
    [Description("Manual Adjustment")]
    Adjustment,
}
