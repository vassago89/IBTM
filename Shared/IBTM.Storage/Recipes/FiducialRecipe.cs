using System;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using IBTM.Core;

namespace IBTM.Inspection;

public sealed class FiducialRecipe
{
    public FiducialRecipe()
    {
        First = new();
        Second = new();
    }

    public FiducialMark First { get; set; }
    public FiducialMark Second { get; set; }
}

public sealed partial class FiducialMark : ObservableObject
{
    public FiducialMark()
    {
        Contour = [];
    }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AxisPosition? MarkPosition
    {
        get;
        set
        {
            field = value;
            if (value is not null)
                LegacyCameraPosition = null;
        }
    }

    [JsonPropertyName("CameraPosition"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AxisPosition? LegacyCameraPosition { get; set; }
    public Point2D[] Contour { get; set; }
    public int ImageWidth { get; set; }
    public int ImageHeight { get; set; }
    public int? LightLevel { get; set; }

    [JsonIgnore]
    public bool IsRegistered => Contour.Length >= 5 && ImageWidth > 0 && ImageHeight > 0
        && (MarkPosition ?? LegacyCameraPosition) is { } position && double.IsFinite(position.X) && double.IsFinite(position.Y);

    [ObservableProperty]
    public partial bool WhiteMark { get; set; }

    public int Threshold
    {
        get;
        set
        {
            if (value is < 0 or > 255)
                throw new ArgumentOutOfRangeException(nameof(value));
            SetProperty(ref field, value);
        }
    } = 127;

    public int SearchSizePixels
    {
        get;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            SetProperty(ref field, value);
        }
    } = 200;

    public double? ReferenceDiameterMm
    {
        get;
        set
        {
            if (value is { } diameter && (!double.IsFinite(diameter) || diameter <= 0))
                throw new ArgumentOutOfRangeException(nameof(value), UiText.Get("Enter a positive fiducial diameter"));
            field = value;
        }
    }

    [JsonInclude, JsonPropertyName("ReferenceRadiusMm")]
    internal double? LegacyRadiusMm
    {
        set => ReferenceDiameterMm ??= value * 2;
    }

    public double MaximumShapeError
    {
        get;
        set
        {
            if (!double.IsFinite(value) || value <= 0)
                throw new ArgumentOutOfRangeException(nameof(value));
            field = value;
        }
    } = 0.01;

    public int MinimumMatchScore
    {
        get;
        set
        {
            if (value is < 0 or > 100)
                throw new ArgumentOutOfRangeException(nameof(value), UiText.Get("Enter a match score from 0 to 100"));
            field = value;
        }
    } = 50;

    public double SizeTolerancePercent
    {
        get;
        set
        {
            if (!double.IsFinite(value) || value < 0 || value >= 100)
                throw new ArgumentOutOfRangeException(nameof(value));
            field = value;
        }
    } = 20;
}
