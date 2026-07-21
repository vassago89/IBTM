namespace IBTM.Stations.Inspection;

public sealed class InspectionOptions
{
    public ZoneMotionParams Motion { get; set; } = new();
    public int NgStackMaxCount { get; set; } = 3;

    public void CopyFrom(InspectionOptions source)
    {
        Motion.CopyFrom(source.Motion);
        NgStackMaxCount = source.NgStackMaxCount;
    }
}
