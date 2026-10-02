namespace IBTM.Storage;

public sealed record ProductionCounts(long OkCount, long NgCount)
{
    public long TotalCount => OkCount + NgCount;
}
