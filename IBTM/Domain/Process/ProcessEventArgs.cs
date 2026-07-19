namespace IBTM.Domain.Process;

public sealed class StageChangedEventArgs(ProcessStage stage, StageStatus status) : EventArgs
{
    public ProcessStage Stage { get; } = stage;
    public StageStatus Status { get; } = status;
}

public sealed class BoltProgressEventArgs(int current, int total, string boltName) : EventArgs
{
    public int Current { get; } = current;
    public int Total { get; } = total;
    public string BoltName { get; } = boltName;
}

/// <summary>Zone 위치 변경 이벤트 인자</summary>
public sealed class ZonePositionEventArgs(int zone, double x, double y, double z) : EventArgs
{
    public int Zone { get; } = zone;
    public double X { get; } = x;
    public double Y { get; } = y;
    public double Z { get; } = z;
}
