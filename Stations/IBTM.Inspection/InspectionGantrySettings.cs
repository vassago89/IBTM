using IBTM.Core;
using IBTM.Device;

namespace IBTM.Inspection;

public sealed class InspectionGantrySettings : Setting
{
    public InspectionGantrySettings()
    {
        Motion = new();
    }

    public MotionSettings Motion { get; set; }

}
