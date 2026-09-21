using System.ComponentModel;

namespace IBTM.BoltFeeder;

public enum BoltFeederState
{
    [Description("Waiting for Bolt")]
    WaitingForBolt,

    [Description("Bolt Ready")]
    BoltReady,

    [Description("Waiting for Escape Backward")]
    WaitingForEscapeBackward,
}
