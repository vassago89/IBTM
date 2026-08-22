using IBTM.Device;

namespace IBTM.PcbSupply;

public sealed class PcbSupplyHardwareSettings : MotionHardwareSettings
{
    public override HardwareArea Area => HardwareArea.PcbSupply;

    public PcbSupplyHardwareSettings()
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
        Axes = new()
        {
            [MachineAxis.PcbSupplyX] = Axis(0, 200),
            [MachineAxis.PcbSupplyY] = Axis(1, 200),
            [MachineAxis.PcbSupplyZ] = Axis(2, 100),
        };
    }
}
