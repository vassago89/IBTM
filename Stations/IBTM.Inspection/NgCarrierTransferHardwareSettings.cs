using IBTM.Device;

namespace IBTM.Inspection;

public sealed class NgCarrierTransferHardwareSettings : IoHardwareSettings
{
    public override HardwareArea Area
    {
        get
        {
            return HardwareArea.NgCarrierTransfer;
        }
    }

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
            [OutputIo.NgCarrierPickupUp] = Output(
                64,
                65,
                InputIo.NgCarrierPickupUp,
                InputIo.NgCarrierPickupDown),
            [OutputIo.NgCarrierGripperOpen] = Output(
                66,
                67,
                InputIo.NgCarrierGripperOpen,
                InputIo.NgCarrierGripperClosed),
        };
    }
}
