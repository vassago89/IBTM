using System.ComponentModel;
using IBTM.Core;

namespace IBTM.UI;

public enum BoltTargetState
{
    [Description("Pending")]
    Pending,

    [Description("Active")]
    Active,

    [Description("OK")]
    Ok,

    [Description("NG")]
    Ng,
}

public sealed record BoltTargetView(
    int Number,
    FasteningHead Head,
    double Left,
    double Top,
    BoltTargetState State);
