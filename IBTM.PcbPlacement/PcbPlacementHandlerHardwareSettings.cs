using IBTM.Device;

namespace IBTM.PcbPlacement;

public sealed class PcbPlacementHandlerHardwareSettings : MotionHardwareSettings
{
    public override HardwareArea Area => HardwareArea.PcbPlacementHandler;

    public PcbPlacementHandlerHardwareSettings()
    {
        Inputs = new()
        {
            [InputIo.PcbPlacementHandlerDown] = 30,
            [InputIo.PcbPlacementHandlerUp] = 31,
            [InputIo.PcbPlacementHandlerRotated] = 32,
            [InputIo.PcbPlacementHandlerUnrotated] = 33,
            [InputIo.PcbPlacementIpmDown] = 34,
            [InputIo.PcbPlacementIpmUp] = 35,
            [InputIo.PcbPlacementIpmGripperClosed] = 36,
            [InputIo.PcbPlacementIpmGripperOpen] = 37,
            [InputIo.PcbPlacementVacuumDetected] = 38,
            [InputIo.PcbPlacementPcbDetected] = 39,
        };
        Outputs = new()
        {
            [OutputIo.PcbPlacementHandlerDown] = Output(
                28,
                29,
                InputIo.PcbPlacementHandlerDown,
                InputIo.PcbPlacementHandlerUp),
            [OutputIo.PcbPlacementHandlerRotate] = Output(
                30,
                31,
                InputIo.PcbPlacementHandlerRotated,
                InputIo.PcbPlacementHandlerUnrotated),
            [OutputIo.PcbPlacementIpmDown] = Output(
                32,
                33,
                InputIo.PcbPlacementIpmDown,
                InputIo.PcbPlacementIpmUp),
            [OutputIo.PcbPlacementIpmGripperClose] = Output(
                34,
                35,
                InputIo.PcbPlacementIpmGripperClosed,
                InputIo.PcbPlacementIpmGripperOpen),
            [OutputIo.PcbPlacementVacuumEjector] = Output(36),
        };
        Axes = new()
        {
            [MachineAxis.PcbPlacementHandlerX] = Axis(3, 200),
            [MachineAxis.PcbPlacementHandlerY] = Axis(4, 400),
            [MachineAxis.PcbPlacementHandlerZ] = Axis(5, 200),
        };
    }
}
