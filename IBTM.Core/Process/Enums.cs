namespace IBTM.Core.Process;

public readonly record struct ProcessStage(int Id, string Name)
{
    public override string ToString() => Name;
}

public static class SystemStages
{
    public static readonly ProcessStage Error = new(-1, nameof(Error));
    public static readonly ProcessStage Idle = new(0, nameof(Idle));
    public static readonly ProcessStage Complete = new(900, nameof(Complete));
}

public enum StageStatus
{
    Idle,
    Running,
    Done,
    Error,
    Warning,
    Skipped,
}

public enum InspectionResult
{
    Unknown,
    Good,
    Ng,
}
