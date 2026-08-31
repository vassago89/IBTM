using IBTM.Core;

namespace IBTM.UI;

public enum WorkTargetState
{
    Pending,
    Active,
    Ok,
    Ng,
}

public sealed record WorkTargetView(
    int Number,
    FasteningHead Head,
    double Left,
    double Top,
    WorkTargetState State);
