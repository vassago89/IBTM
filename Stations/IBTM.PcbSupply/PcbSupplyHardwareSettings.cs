using IBTM.Core;
using IBTM.Device;

namespace IBTM.PcbSupply;

public sealed class PcbSupplyHardwareSettings : MotionHardwareSettings
{
    public override HardwareArea Area
    {
        get
        {
            return HardwareArea.PcbSupply;
        }
    }

    public PcbSupplyHardwareSettings() : base(
        MotionGroup.PcbSupply,
        (
            MotionAxis.X,
            MachineAxis.PcbSupplyX,
            0),
        (
            MotionAxis.Y,
            MachineAxis.PcbSupplyY,
            1),
        (
            MotionAxis.Z,
            MachineAxis.PcbSupplyZ,
            2))
    {
        Inputs = new()
        {
            [InputIo.PcbSupplyAvailableFromFront1] = 16,
            [InputIo.PcbSupplyRotated] = 20,
            [InputIo.PcbSupplyUnrotated] = 21,
            [InputIo.PcbSupplyGripperClosed] = 24,
            [InputIo.PcbSupplyGripperOpen] = 25,
            [InputIo.PcbSupplyIpmFixerForward] = 28,
            // Installed, but its address still needs field confirmation. DI-109 is the gripper.
            [InputIo.PcbSupplyIpmFixerBackward] = -1,
            [InputIo.PcbSupplyPcbDetected] = 22,
        };
        Outputs = new()
        {
            [OutputIo.PcbSupplyReadyToFront1] = Output(16),
            [OutputIo.PcbSupplyGripperClosed] = Output(
                22,
                23,
                InputIo.PcbSupplyGripperClosed,
                InputIo.PcbSupplyGripperOpen),
            [OutputIo.PcbSupplyRotate] = Output(
                20,
                21,
                InputIo.PcbSupplyRotated,
                InputIo.PcbSupplyUnrotated),
            [OutputIo.PcbSupplyIpmFixerForward] = Output(
                24,
                InputIo.PcbSupplyIpmFixerForward,
                InputIo.PcbSupplyIpmFixerBackward),
        };
    }
}
