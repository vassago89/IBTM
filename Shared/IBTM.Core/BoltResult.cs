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
    public BoltFasteningStage Stage { get; init; }
    public BoltResult? PreliminaryResult { get; init; }

    [JsonIgnore]
    public bool IsComplete => Stage != BoltFasteningStage.Preliminary
        || !Success || Source == BoltResultSource.DryRun;
    // Keep the bolt's applied criterion with the measurement, independent of later recipe edits.
    public double? MinimumTurns { get; init; }
    public double? MaximumTurns { get; init; }

    [JsonIgnore]
    public double? MeasuredTurns => Source == BoltResultSource.Controller
        && Controller is { Angle3: >= 0 } data && double.IsFinite(data.Angle3)
        ? data.Angle3 / 360.0 : null;

    [JsonIgnore]
    public double? TotalTurns => Stage == BoltFasteningStage.Final
        ? MeasuredTurns + PreliminaryResult?.MeasuredTurns : MeasuredTurns;

    [JsonIgnore]
    public AssemblyResult? TurnsResult
    {
        get
        {
            if (MinimumTurns is null && MaximumTurns is null)
                return null;
            if (TotalTurns is not { } turns)
                return AssemblyResult.Pending;
            if ((MinimumTurns is { } minimum && turns < minimum)
                || (MaximumTurns is { } maximum && turns > maximum))
                return AssemblyResult.Ng;
            return AssemblyResult.Ok;
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
    ushort[]? Registers)
{
    // Preset setting captured at operation start, separate from the ADC result registers.
    public ushort? TorqueCompensationPercent { get; init; }
}
