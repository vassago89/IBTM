namespace IBTM.Stations.PcbPlacement;

public sealed class PcbPlacementOptions
{
    public ZoneMotionParams Motion { get; set; } = new();

    public void CopyFrom(PcbPlacementOptions source) => Motion.CopyFrom(source.Motion);
}
