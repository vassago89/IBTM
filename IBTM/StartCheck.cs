using System.ComponentModel;

namespace IBTM;

public enum StartArea
{
    [Description("Supply handler")]
    Supply,
    [Description("Placement handler")]
    Placement,
    [Description("Head 1 · Pickup")]
    PickupHead,
    [Description("Head 2 · Shooting")]
    ShootingHead,
    [Description("S1 · PCB Placement")]
    Station1,
    [Description("S2 · Bolt Fastening")]
    Station2,
    [Description("S3 · Inspection")]
    Station3,
}

public enum StartCheckState
{
    [Description("Not checked")]
    NotChecked,
    [Description("Unknown")]
    Unknown,
    [Description("Empty")]
    Empty,
    [Description("Remove material")]
    MaterialRemaining,
    [Description("Unfinished carrier")]
    UnfinishedCarrier,
    [Description("Completed")]
    Completed,
    [Description("Ready for Placement handoff")]
    HandoffReady,
}
