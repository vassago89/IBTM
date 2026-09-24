using System.ComponentModel;

namespace IBTM.Device;

public sealed class MotionSettings : IDataErrorInfo
{
    public MotionSettings()
    {
        HorizontalHome = new();
        ZHome = new()
        {
            SearchSpeed = 10,
            DetectionSpeed = 2,
            ApproachSpeed = 1,
            FineSpeed = 0.1,
        };
    }

    // Preserve saved values so older settings can be opened and corrected in the UI.
    // Validation belongs to editing/saving and the motion operation, not deserialization.
    public double HorizontalSpeed { get; set; } = 100.0;
    public double ZSpeed { get; set; } = 50.0;
    public double AccelerationSeconds { get; set; } = 0.5;
    public double DecelerationSeconds { get; set; } = 0.5;
    public HomeSettings HorizontalHome { get; set; }
    public HomeSettings ZHome { get; set; }

    public HomeSettings Home(MotionAxis axis)
    {
        return axis == MotionAxis.Z ? ZHome : HorizontalHome;
    }

    string IDataErrorInfo.Error => GetValidationError(hasZ: true) ?? string.Empty;
    string IDataErrorInfo.this[string propertyName] => this[propertyName] ?? string.Empty;

    public string? this[string propertyName]
    {
        get
        {
            return propertyName switch
            {
                nameof(HorizontalSpeed) => PositiveValueError(HorizontalSpeed, propertyName),
                nameof(ZSpeed) => PositiveValueError(ZSpeed, propertyName),
                nameof(AccelerationSeconds) => PositiveValueError(AccelerationSeconds, propertyName),
                nameof(DecelerationSeconds) => PositiveValueError(DecelerationSeconds, propertyName),
                _ => null,
            };
        }
    }

    public string? GetValidationError(bool hasZ)
    {
        return this[nameof(HorizontalSpeed)]
            ?? this[nameof(AccelerationSeconds)]
            ?? this[nameof(DecelerationSeconds)]
            ?? HorizontalHome.ValidationError
            ?? (hasZ ? this[nameof(ZSpeed)] ?? ZHome.ValidationError : null);
    }

    internal static string? PositiveValueError(double value, string name)
    {
        return double.IsFinite(value) && value > 0
            ? null
            : $"{name} must be a positive finite value.";
    }
}
