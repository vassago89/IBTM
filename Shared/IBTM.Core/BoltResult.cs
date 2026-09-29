using System;
using System.ComponentModel;
using System.Text.Json.Serialization;

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

    [Description("IO · Result unavailable")]
    IoResultUnavailable,
}

public sealed record BoltResult(
    bool Success,
    double? Torque,
    BoltResultSource Source = BoltResultSource.Controller,
    string? Error = null)
{
    public DateTimeOffset? RecordedAt { get; init; }
    public BoltControllerData? Controller { get; init; }
    // Keep the bolt's applied criterion with the measurement, independent of later recipe edits.
    public double? MinimumTurns { get; init; }

    [JsonIgnore]
    public double? TotalTurns
    {
        get
        {
            return Source == BoltResultSource.Controller
                && Controller is { Angle3: >= 0 } data && double.IsFinite(data.Angle3)
                ? data.Angle3 / 360.0 : null;
        }
    }

    [JsonIgnore]
    public AssemblyResult? TurnsResult
    {
        get
        {
            if (MinimumTurns is not { } minimum)
                return null;
            if (TotalTurns is not { } turns)
                return AssemblyResult.Pending;
            return turns >= minimum ? AssemblyResult.Ok : AssemblyResult.Ng;
        }
    }
}

// The complete ADC result payload. Codes and original registers are retained as received.
public sealed record BoltControllerData(
    string Port,
    byte SlaveAddress,
    ushort EventCount,
    ushort FasteningTimeMilliseconds,
    ushort Preset,
    double TargetTorque,
    ushort TargetSpeedRpm,
    double Angle1,
    double Angle2,
    double Angle3,
    ushort ScrewCount,
    ushort ErrorCode,
    ushort DirectionCode,
    ushort StatusCode,
    ushort SnugAngle,
    ushort[]? Registers);
