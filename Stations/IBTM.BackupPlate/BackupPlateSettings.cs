using System;
using System.Text.Json.Serialization;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.BackupPlate;

public sealed class BackupPlateSettings : Setting
{
    public BackupPlateSettings()
    {
        Motion = new();
    }

    public double? TransferHeight { get; set; }
    public double? FasteningHeight { get; set; }
    public double PositionTolerance { get; set; } = 0.05;
    public MotionSettings Motion { get; set; }

    [JsonIgnore]
    public string? ValidationError
    {
        get
        {
            if (TransferHeight is not { } transfer || !double.IsFinite(transfer)
                || FasteningHeight is not { } fastening || !double.IsFinite(fastening))
                return UiText.Get("Set the S2 backup transfer and fastening heights");
            if (!double.IsFinite(PositionTolerance) || PositionTolerance <= 0
                || Math.Abs(transfer - fastening) <= PositionTolerance * 2)
                return UiText.Get("Check the S2 backup heights and position tolerance");
            return Motion.GetValidationError(hasZ: true);
        }
    }
}
