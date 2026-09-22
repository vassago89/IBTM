using System.ComponentModel;

namespace IBTM;

public enum RepeatPhase
{
    [Description("Forward repeat transfer")]
    Automatic,
    [Description("NG end → Shuttle")]
    ReturnToShuttle,
    [Description("Shuttle → Station 3")]
    ReturnToStation3,
    [Description("Returning to entry sensor")]
    ReturnToStart,
    [Description("Station 3 → Waiting position")]
    ClearStation3,
}

