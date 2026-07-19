namespace IBTM.Domain.Process;

public sealed class ProductionStats
{
    public int TotalCount { get; set; }
    public int GoodCount { get; set; }
    public int NgCount { get; set; }
    public double LastCycleTimeSeconds { get; set; }
    public double AverageCycleTimeSeconds { get; set; }
    public DateTime StartTime { get; set; } = DateTime.Now;

    public double NgRate => TotalCount > 0 ? (double)NgCount / TotalCount * 100.0 : 0.0;
    public TimeSpan Uptime => DateTime.Now - StartTime;
    public string UptimeFormatted => $"{(int)Uptime.TotalHours:D2}:{Uptime.Minutes:D2}:{Uptime.Seconds:D2}";
}
