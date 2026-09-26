using System.Text.Json.Serialization;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.PcbSupply;

public sealed class PcbSupplyHardwareSettings : MotionHardwareSettings
{
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
            [InputIo.PcbSupplyPcbDetected] = 22,
        };
        Outputs = new()
        {
            [OutputIo.PcbSupplyReadyToFront1] = CreateOutput(16),
            [OutputIo.PcbSupplyGripperClosed] = CreateOutput(
                22,
                23,
                InputIo.PcbSupplyGripperClosed,
                InputIo.PcbSupplyGripperOpen),
            [OutputIo.PcbSupplyRotate] = CreateOutput(
                20,
                21,
                InputIo.PcbSupplyRotated,
                InputIo.PcbSupplyUnrotated),
            [OutputIo.PcbSupplyIpmFixerForward] = CreateOutput(
                24,
                InputIo.PcbSupplyIpmFixerForward),
        };
    }

    [JsonIgnore]
    public override HardwareArea Area => HardwareArea.PcbSupply;
}
