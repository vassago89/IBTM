using IBTM.Core;

namespace IBTM.AlphaMotion;

public sealed class AlphaMotionSettings : Setting
{
    // Keep the persisted key; for TMC-AE16DIOe this is the Digital IO utility's Card No.
    public int ControllerNumber { get; set; }
}
