using IBTM.Core;
using IBTM.Device;

namespace IBTM.PcbSupply;

public sealed class PcbSupplyHardwareSettings : MotionHardwareSettings
{
    public override HardwareArea Area => HardwareArea.PcbSupply;

    public PcbSupplyHardwareSettings() : base(
        MotionGroup.PcbSupply,
        (MotionAxis.X, MachineAxis.PcbSupplyX, 0, 200),
        (MotionAxis.Y, MachineAxis.PcbSupplyY, 1, 200),
        (MotionAxis.Z, MachineAxis.PcbSupplyZ, 2, 100))
    {
        Inputs = new()
        {
            [InputIo.PcbSupplyAvailableFromFront1] = 16,
            [InputIo.PcbSupplyNestForward] = 20,
            [InputIo.PcbSupplyNestBackward] = 21,
            [InputIo.PcbSupplyRotated] = 22,
            [InputIo.PcbSupplyUnrotated] = 23,
            [InputIo.PcbSupplyIpmFixerForward] = 26,
            [InputIo.PcbSupplyIpmFixerBackward] = 27,
            [InputIo.PcbSupplyPcbDetected] = 28,
        };
        Outputs = new()
        {
            [OutputIo.PcbSupplyReadyToFront1] = Output(16),
            [OutputIo.PcbSupplyNestForward] = Output(
                20,
                21,
                InputIo.PcbSupplyNestForward,
                InputIo.PcbSupplyNestBackward),
            [OutputIo.PcbSupplyRotate] = Output(
                22,
                23,
                InputIo.PcbSupplyRotated,
                InputIo.PcbSupplyUnrotated),
            [OutputIo.PcbSupplyIpmFixerForward] = Output(
                26,
                27,
                InputIo.PcbSupplyIpmFixerForward,
                InputIo.PcbSupplyIpmFixerBackward),
        };
    }
}
