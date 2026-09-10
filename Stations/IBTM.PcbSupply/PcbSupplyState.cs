using System.ComponentModel;

namespace IBTM.PcbSupply;

public enum PcbSupplyState
{
    [Description("Waiting for Carrier")]
    WaitingForCarrier,

    [Description("Waiting for Carrier Exit")]
    WaitingForCarrierExit,

    [Description("Picking PCB")]
    PickingPcb,

    [Description("Securing PCB")]
    SecuringPcb,

    [Description("Raising for Pickup")]
    RaisingForPickup,

    [Description("Moving Above Buffer")]
    MovingAboveBuffer,

    [Description("Rotating for Buffer")]
    RotatingForBuffer,

    [Description("Waiting for Buffer")]
    WaitingForBuffer,

    [Description("Moving to Buffer")]
    MovingToBuffer,

    [Description("Waiting for Buffer PCB")]
    WaitingForBufferPcb,

    [Description("Waiting for Placement Handler")]
    WaitingForPlacement,

    [Description("Releasing PCB")]
    ReleasingPcb,

    [Description("Unrotating for Pickup")]
    UnrotatingForPickup,
}

public enum PcbSupplyRotationState
{
    [Description("Unrotated")]
    Unrotated,

    [Description("Between")]
    Between,

    [Description("Rotated")]
    Rotated,
}

public enum PcbSupplyCylinderState
{
    [Description("Backward")]
    Backward,

    [Description("Between")]
    Between,

    [Description("Forward")]
    Forward,
}

public enum PcbSupplyPcbState
{
    [Description("No PCB")]
    None,

    [Description("PCB Detected")]
    Detected,

    [Description("PCB Secured")]
    Secured,
}
