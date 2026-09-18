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
    }

    public override HardwareArea Area
    {
        get
        {
            return HardwareArea.InspectionGantry;
        }
    }
}
