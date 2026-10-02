using System.ComponentModel;

namespace IBTM.Core;

public enum BoltFasteningStage
{
    [Description("1-stage fastening")]
    Single,

    [Description("Pre-tightening")]
    Preliminary,

    [Description("Final tightening")]
    Final,
}
