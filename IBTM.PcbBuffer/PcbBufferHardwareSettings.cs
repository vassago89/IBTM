using IBTM.Device;

namespace IBTM.PcbBuffer;

public sealed class PcbBufferHardwareSettings : InputHardwareSettings
{
    public override HardwareArea Area => HardwareArea.PcbBuffer;

    public PcbBufferHardwareSettings()
    {
        Inputs = new()
        {
            [InputIo.PcbBufferPcbPresent] = 29,
        };
    }
}
