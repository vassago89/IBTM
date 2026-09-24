using System.ComponentModel;
using System.Text.Json.Serialization;

namespace IBTM.Device;

public sealed class HomeSettings : IDataErrorInfo
{
    public double SearchSpeed { get; set; } = 15;
    public double DetectionSpeed { get; set; } = 3;
    public double ApproachSpeed { get; set; } = 1.5;
    public double FineSpeed { get; set; } = 0.15;
    public double SearchAccelerationSeconds { get; set; } = 1;
    public double DetectionAccelerationSeconds { get; set; } = 2;

    string IDataErrorInfo.Error => ValidationError ?? string.Empty;
    string IDataErrorInfo.this[string propertyName] => this[propertyName] ?? string.Empty;

    [JsonIgnore]
    public string? ValidationError
    {
        get
        {
            return this[nameof(SearchSpeed)]
                ?? this[nameof(DetectionSpeed)]
                ?? this[nameof(ApproachSpeed)]
                ?? this[nameof(FineSpeed)]
                ?? this[nameof(SearchAccelerationSeconds)]
                ?? this[nameof(DetectionAccelerationSeconds)];
        }
    }

    public string? this[string propertyName]
    {
        get
        {
            return propertyName switch
            {
                nameof(SearchSpeed) => MotionSettings.PositiveValueError(SearchSpeed, propertyName),
                nameof(DetectionSpeed) => MotionSettings.PositiveValueError(DetectionSpeed, propertyName),
                nameof(ApproachSpeed) => MotionSettings.PositiveValueError(ApproachSpeed, propertyName),
                nameof(FineSpeed) => MotionSettings.PositiveValueError(FineSpeed, propertyName),
                nameof(SearchAccelerationSeconds) => MotionSettings.PositiveValueError(SearchAccelerationSeconds, propertyName),
                nameof(DetectionAccelerationSeconds) => MotionSettings.PositiveValueError(DetectionAccelerationSeconds, propertyName),
                _ => null,
            };
        }
    }
}
