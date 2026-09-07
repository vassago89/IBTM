using IBTM.Core;
using IBTM.Device;

namespace IBTM.Inspection;

public sealed class InspectionGantryHardwareSettings : MotionHardwareSettings
{
    public override HardwareArea Area => HardwareArea.InspectionGantry;

    public InspectionGantryHardwareSettings() : base(
        MotionGroup.InspectionGantry,
        (MotionAxis.X, MachineAxis.InspectionGantryX, 9, 200),
        (MotionAxis.Y, MachineAxis.InspectionGantryY, 10, 200))
    {
    }
}
