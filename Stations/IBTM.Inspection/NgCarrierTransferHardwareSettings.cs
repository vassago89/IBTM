using System.Text.Json.Serialization;
using IBTM.Device;

namespace IBTM.Inspection;

public sealed class NgCarrierTransferHardwareSettings : IoHardwareSettings
{
    public NgCarrierTransferHardwareSettings()
    {
        Inputs = new()
        {
            [InputIo.NgCarrierPickupDown] = 75,
            [InputIo.NgCarrierPickupUp] = 76,
            [InputIo.NgCarrierGripperClosed] = 77,
            [InputIo.NgCarrierGripperOpen] = 78,
            [InputIo.NgCarrierDetected] = 79,
        };
        Outputs = new()
        {
            [OutputIo.NgCarrierPickupDown] = CreateOutput(
                64,
                65,
                InputIo.NgCarrierPickupDown,
                InputIo.NgCarrierPickupUp),
            [OutputIo.NgCarrierGripperClose] = CreateOutput(
                66,
                67,
                InputIo.NgCarrierGripperClosed,
                InputIo.NgCarrierGripperOpen),
        };
    }

    [JsonIgnore]
    public override HardwareArea Area => HardwareArea.NgCarrierTransfer;
}
