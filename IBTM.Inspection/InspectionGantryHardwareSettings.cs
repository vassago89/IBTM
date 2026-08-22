using IBTM.Device;

namespace IBTM.Inspection;

public sealed class InspectionGantryHardwareSettings : MotionHardwareSettings
{
    public override HardwareArea Area => HardwareArea.InspectionGantry;

    public InspectionGantryHardwareSettings()
    {
        Inputs = new()
        {
            [InputIo.NgCarrierPickupDown] = 75,
            [InputIo.NgCarrierPickupUp] = 76,
            [InputIo.NgCarrierGripperClosed] = 77,
            [InputIo.NgCarrierGripperOpen] = 78,
            [InputIo.NgCarrierJigDetected] = 79,
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
        Axes = new()
        {
            [MachineAxis.InspectionGantryX] = Axis(9),
            [MachineAxis.InspectionGantryY] = Axis(10),
        };
    }
}
