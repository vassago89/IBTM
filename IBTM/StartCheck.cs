using System.ComponentModel;

namespace IBTM;

public enum StartArea
{
    [Description("Supply")]
    Supply,
    [Description("Placement")]
    Placement,
    [Description("Pickup")]
    PickupHead,
    [Description("Shooting")]
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
    [Description("Ready to rework")]
    ReworkReady,
    [Description("Ready for Placement handoff")]
    HandoffReady,
    [Description("Disabled")]
    Disabled,
}

public enum CarrierWorkAction
{
    [Description("Mark complete")]
    Complete,
    [Description("Clear results")]
    Clear,
}

public enum StartPreparationAction
{
    [Description("Release material grip")]
    ReleaseMaterial,
    [Description("Raise tooling")]
    RaiseTooling,
    [Description("Raise/lower carrier support")]
    ToggleCarrierSupport,
    [Description("Raise/lower stopper")]
    ToggleStopper,

    [Description("Move to rotation position")]
    MoveToRotationPosition,
}
