namespace IBTM.Stations.BoltFastening;

public static class BoltFasteningStages
{
    public static readonly ProcessStage WaitShuttle = new(201, "BoltFastening.WaitShuttle");
    public static readonly ProcessStage StopAlignLift = new(202, "BoltFastening.StopAlignLift");
    public static readonly ProcessStage Fiducial = new(203, "BoltFastening.Fiducial");
    public static readonly ProcessStage Tighten = new(204, "BoltFastening.Tighten");
    public static readonly ProcessStage Release = new(205, "BoltFastening.Release");
}
