using System.Text.Json.Serialization;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.Inspection;

public sealed class InspectionGantryHardwareSettings : MotionHardwareSettings
{
    public InspectionGantryHardwareSettings() : base(
        MotionGroup.InspectionGantry,
        (
            MotionAxis.X,
            MachineAxis.InspectionGantryX,
            9),
        (
            MotionAxis.Y,
            MachineAxis.InspectionGantryY,
            10))
    {
        Inputs = new()
        {
            [InputIo.InspectionProbeUp] = -1,
            [InputIo.InspectionProbeDown] = -1,
        };
        Outputs = new()
        {
            [OutputIo.InspectionProbeDown] = CreateOutput(
                77, InputIo.InspectionProbeDown, InputIo.InspectionProbeUp),
        };
    }

    [JsonIgnore]
    public override HardwareArea Area => HardwareArea.InspectionGantry;
}
