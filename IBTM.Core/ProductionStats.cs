namespace IBTM.Core;

public sealed class ProductionStats
{
    public int TotalCount { get; set; }
    public int GoodCount { get; set; }
    public int NgCount { get; set; }
    public double LastCycleTimeSeconds { get; set; }

    public double NgRate => TotalCount > 0 ? (double)NgCount / TotalCount * 100.0 : 0.0;
}
