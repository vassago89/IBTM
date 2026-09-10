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
            0,
            200),
        (
            MotionAxis.Y,
            MachineAxis.PcbSupplyY,
            1,
            200),
        (
            MotionAxis.Z,
            MachineAxis.PcbSupplyZ,
            2,
            100))
    {
        Inputs = new()
        {
            [InputIo.PcbSupplyAvailableFromFront1] = 16,
            [InputIo.PcbSupplyRotated] = 20,
            [InputIo.PcbSupplyUnrotated] = 21,
            [InputIo.PcbSupplyGripperClosed] = 22,
            [InputIo.PcbSupplyGripperOpen] = 23,
            [InputIo.PcbSupplyIpmFixerForward] = 24,
            [InputIo.PcbSupplyIpmFixerBackward] = 25,
            [InputIo.PcbSupplyPcbDetected] = 28,
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
                25,
                InputIo.PcbSupplyIpmFixerForward,
                InputIo.PcbSupplyIpmFixerBackward),
        };
    }
}
