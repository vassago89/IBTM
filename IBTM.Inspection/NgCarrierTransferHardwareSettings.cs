using IBTM.Device;

namespace IBTM.Inspection;

public sealed class NgCarrierTransferHardwareSettings : IoHardwareSettings
{
    public override HardwareArea Area => HardwareArea.NgCarrierTransfer;

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
            [OutputIo.NgCarrierPickupDown] = Output(
                63,
                64,
                InputIo.NgCarrierPickupDown,
                InputIo.NgCarrierPickupUp),
            [OutputIo.NgCarrierGripperClose] = Output(
                65,
                66,
                InputIo.NgCarrierGripperClosed,
                InputIo.NgCarrierGripperOpen),
        };
    }
}
