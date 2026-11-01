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
            [InputIo.PcbPlacementIpmSupportEngaged] = 32,
            [InputIo.PcbPlacementIpmSupportRetracted] = 33,
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
            [OutputIo.PcbPlacementIpmSupport] = CreateOutput(
                31,
                30,
                InputIo.PcbPlacementIpmSupportEngaged,
                InputIo.PcbPlacementIpmSupportRetracted),
            [OutputIo.PcbPlacementIpmDown] = new() { Number = 32, OffNumber = 33 },
            [OutputIo.PcbPlacementVacuumEjector] = CreateOutput(36),
        };
    }

    [JsonIgnore]
    public override HardwareArea Area => HardwareArea.PcbPlacementHandler;

    void IJsonOnDeserialized.OnDeserialized()
    {
        // Discard retired inputs and the removed gripper from saved settings.
        Inputs.Remove(InputIo.Unused7);
        Inputs.Remove(InputIo.Unused8);
        Inputs.Remove(InputIo.PcbPlacementIpmDown);
        Inputs.Remove(InputIo.PcbPlacementIpmUp);
        Outputs.Remove(OutputIo.Unused3);
        // Correct the original support valve mapping; preserve custom channel assignments.
        if (Outputs.TryGetValue(OutputIo.PcbPlacementIpmSupport, out var support)
            && support.Number == 30 && support.OffNumber == 31)
        {
            support.Number = 31;
            support.OffNumber = 30;
        }
    }
}
