using System.ComponentModel;

namespace IBTM.Device;

public enum IoSection
{
    [Description("Safety")]
    MachineSafety,

    [Description("Mode & Utility")]
    MachineModeUtility,

    [Description("Interface & Drive")]
    MainConveyorInterfaceDrive,

    [Description("Pickup Head")]
    BoltFasteningPickupHead = 6,

    [Description("Shooting Head")]
    BoltFasteningShootingHead,

    [Description("Storage")]
    NgConveyorStorage,

    [Description("Operator Eject")]
    NgConveyorOperatorEject,

    [Description("Pickup Controller I/O")]
    BoltPickupController,

    [Description("Shooting Controller I/O")]
    BoltShootingController,
}
