namespace IBTM.Stations.Inspection;

public static class InspectionStages
{
    public static readonly ProcessStage WaitShuttle = new(301, "Inspection.WaitShuttle");
    public static readonly ProcessStage StopAlignLift = new(302, "Inspection.StopAlignLift");
    public static readonly ProcessStage Inspect = new(303, "Inspection.Inspect");
    public static readonly ProcessStage NgTransfer = new(304, "Inspection.NgTransfer");
    public static readonly ProcessStage SmemaWait = new(305, "Inspection.SmemaWait");
    public static readonly ProcessStage Discharge = new(306, "Inspection.Discharge");
    public static readonly ProcessStage Release = new(307, "Inspection.Release");
}
