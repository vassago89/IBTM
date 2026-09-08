using System.ComponentModel;

namespace IBTM.BoltFastening;

internal enum BoltFasteningWorkState
{
    [Description("Waiting for Carrier")]
    WaitingForCarrier,

    [Description("Waiting for Carrier Seating")]
    WaitingForSeat,

    [Description("Ready to Fasten")]
    ReadyToFasten,

    [Description("Waiting for Transfer")]
    WaitingForTransfer,
}

public enum BoltFasteningState
{
    [Description("Waiting")]
    Waiting,

    [Description("Moving to PCB Bolt")]
    MovingToPcbBolt,

    [Description("Lowering for PCB Fastening")]
    LoweringForPcb,

    [Description("Shooting Bolt")]
    ShootingBolt,

    [Description("Advancing Shooting Escape")]
    AdvancingShootingEscape,

    [Description("Waiting for Shooting Feeder")]
    WaitingForShootingFeeder,

    [Description("Waiting for Shooting Tube")]
    WaitingForShootingTubeClear,

    [Description("Retracting Shooting Escape")]
    RetractingShootingEscape,

    [Description("Fastening PCB Bolt")]
    FasteningPcb,

    [Description("Clearing Shooting Head")]
    ClearingShootingHead,

    [Description("Moving to Pickup XY")]
    MovingToPickupXY,

    [Description("Lowering Head for Bolt Pickup")]
    LoweringForBoltPickup,

    [Description("Moving to Pickup Z")]
    MovingToPickupZ,

    [Description("Waiting for Pickup Feeder")]
    WaitingForPickupFeeder,

    [Description("Picking Up Bolt")]
    PickingUpBolt,

    [Description("Raising Picked Bolt")]
    RaisingPickedBolt,

    [Description("Raising Pickup Head")]
    RaisingPickupHead,

    [Description("Moving to IPM Seating Bolt")]
    MovingToIpmSeatingBolt,

    [Description("Lowering for IPM Seating")]
    LoweringForIpmSeating,

    [Description("Seating IPM Bolt")]
    SeatingIpm,

    [Description("Moving to IPM Final Bolt")]
    MovingToIpmFinalBolt,

    [Description("Lowering for IPM Final")]
    LoweringForIpmFinal,

    [Description("Finalizing IPM Bolt")]
    FinalizingIpm,

    [Description("Clearing Pickup Head")]
    ClearingPickupHead,

    [Description("Completing Carrier")]
    CompletingCarrier,
}

public enum FasteningPass
{
    [Description("PCB Fastening")]
    Pcb,

    [Description("IPM Seating")]
    IpmSeating,

    [Description("IPM Final")]
    IpmFinal,
}

public enum BoltCylinderState
{
    [Description("Up")]
    Up,

    [Description("Between")]
    Between,

    [Description("Down")]
    Down,
}

internal enum BoltEscapeState
{
    [Description("Forward")]
    Forward,

    [Description("Between")]
    Between,

    [Description("Backward")]
    Backward,
}
