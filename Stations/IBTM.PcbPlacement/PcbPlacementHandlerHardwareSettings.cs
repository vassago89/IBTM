using IBTM.Core;
using IBTM.Device;

namespace IBTM.PcbPlacement;

public sealed class PcbPlacementHandlerHardwareSettings : MotionHardwareSettings
{
    public PcbPlacementHandlerHardwareSettings() : base(
        MotionGroup.PcbPlacementHandler,
        (
            MotionAxis.X,
            MachineAxis.PcbPlacementHandlerX,
            3),
        (
            MotionAxis.Y,
            MachineAxis.PcbPlacementHandlerY,
            4),
        (
            MotionAxis.Z,
            MachineAxis.PcbPlacementHandlerZ,
            5))
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
            [OutputIo.PcbPlacementHandlerDown] = CreateOutput(
                28,
                29,
                InputIo.PcbPlacementHandlerDown,
                InputIo.PcbPlacementHandlerUp),
            [OutputIo.PcbPlacementHandlerRotate] = CreateOutput(
                30,
                31,
                InputIo.PcbPlacementHandlerRotated,
                InputIo.PcbPlacementHandlerUnrotated),
            [OutputIo.PcbPlacementIpmDown] = CreateOutput(
                32,
                33,
                InputIo.PcbPlacementIpmDown,
                InputIo.PcbPlacementIpmUp),
            [OutputIo.PcbPlacementIpmGripperClose] = CreateOutput(
                34,
                35,
                InputIo.PcbPlacementIpmGripperClosed,
                InputIo.PcbPlacementIpmGripperOpen),
            [OutputIo.PcbPlacementVacuumEjector] = CreateOutput(36),
        };
    }

    public override HardwareArea Area => HardwareArea.PcbPlacementHandler;
}
