namespace IBTM.Domain.Process;

public sealed class LogEntry
{
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public string Message { get; init; } = string.Empty;
    public LogLevel Level { get; init; } = LogLevel.Info;
    public string Stage { get; init; } = string.Empty;

    public string FormattedTime => Timestamp.ToString("HH:mm:ss.fff");
}
