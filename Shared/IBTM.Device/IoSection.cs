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

    [Description("Station 1")]
    MainConveyorStation1,

    [Description("Station 2")]
    MainConveyorStation2,

    [Description("Station 3")]
    MainConveyorStation3,

    [Description("Pickup Head")]
    BoltFasteningPickupHead,

    [Description("Shooting Head")]
    BoltFasteningShootingHead,

    [Description("Storage")]
    NgConveyorStorage,

    [Description("Operator Eject")]
    NgConveyorOperatorEject,
}
