namespace IBTM.Core.Process;

public static class SystemStages
{
    public const string Error = nameof(Error);
    public const string Idle = nameof(Idle);
    public const string Complete = nameof(Complete);
}

public enum StageStatus
{
    Idle,
    Running,
    Done,
    Error,
}

public enum InspectionResult
{
    Good,
    Ng,
}
