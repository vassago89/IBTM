using System.ComponentModel;

namespace IBTM.Core;

public enum BoltResultSource
{
    [Description("Controller")]
    Controller,

    [Description("Manual")]
    Manual,

    [Description("IO · Assumed OK")]
    IoAssumedOk,

    [Description("Dry run · Not measured")]
    DryRun,
}

public sealed record BoltResult(
    bool Success,
    double? Torque,
    BoltResultSource Source = BoltResultSource.Controller,
    string? Error = null);
