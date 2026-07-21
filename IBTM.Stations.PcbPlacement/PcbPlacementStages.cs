namespace IBTM.Stations.PcbPlacement;

public static class PcbPlacementStages
{
    public static readonly ProcessStage WaitShuttle = new(101, "PcbPlacement.WaitShuttle");
    public static readonly ProcessStage StopAlignLift = new(102, "PcbPlacement.StopAlignLift");
    public static readonly ProcessStage PickPlace = new(103, "PcbPlacement.PickPlace");
    public static readonly ProcessStage Release = new(104, "PcbPlacement.Release");
}
