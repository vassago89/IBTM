using System.ComponentModel;

namespace IBTM.Inspection;

internal enum InspectionWorkState
{
    [Description("Waiting for Carrier")]
    WaitingForCarrier,

    [Description("Waiting for plate DOWN, stopper UP and conveyor STOP")]
    WaitingForInspectionPosition,

    [Description("Waiting for Inspection Gantry")]
    WaitingForGantry,

    [Description("Ready to Inspect")]
    ReadyToInspect,

    [Description("Waiting for Transfer")]
    WaitingForTransfer,

    [Description("Waiting for other carrier transfers before inspection")]
    WaitingForConveyor,
}
