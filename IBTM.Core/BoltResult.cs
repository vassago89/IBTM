using System.ComponentModel;

namespace IBTM.Core;

public enum BoltResultSource
{
    [Description("Controller")]
    Controller,

    [Description("Manual")]
    Manual,
}

public sealed record BoltResult(
    bool Success,
    double Torque,
    BoltResultSource Source = BoltResultSource.Controller);
