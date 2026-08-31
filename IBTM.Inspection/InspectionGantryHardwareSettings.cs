using IBTM.Device;

namespace IBTM.Inspection;

public sealed class InspectionGantryHardwareSettings : MotionHardwareSettings
{
    public override HardwareArea Area => HardwareArea.InspectionGantry;

    public InspectionGantryHardwareSettings()
    {
        Axes = new()
        {
            [MachineAxis.InspectionGantryX] = Axis(9),
            [MachineAxis.InspectionGantryY] = Axis(10),
        };
    }
}
