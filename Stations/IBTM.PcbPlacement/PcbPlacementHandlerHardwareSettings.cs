using System.Text.Json.Serialization;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.PcbPlacement;

public sealed class PcbPlacementHandlerHardwareSettings : MotionHardwareSettings, IJsonOnDeserialized
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
            [OutputIo.PcbPlacementVacuumEjector] = CreateOutput(36),
        };
    }

    [JsonIgnore]
    public override HardwareArea Area => HardwareArea.PcbPlacementHandler;

    void IJsonOnDeserialized.OnDeserialized()
    {
        // Retire the removed gripper without remapping any remaining hardware channels.
        Inputs.Remove(InputIo.Unused7);
        Inputs.Remove(InputIo.Unused8);
        Outputs.Remove(OutputIo.Unused3);
    }
}
