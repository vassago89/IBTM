using IBTM.Core;

namespace IBTM.AlphaMotion;

public sealed class AlphaMotionSettings : Setting
{
    public int ControllerNumber { get; set; }
    public int StationNumber { get; set; } = 1;
    public int CommunicationSpeed { get; set; } = 3;
}
