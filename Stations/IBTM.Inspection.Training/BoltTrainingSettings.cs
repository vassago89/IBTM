using System;
using System.ComponentModel;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace IBTM.Inspection.Training;

public enum InspectionImageCollection
{
    [Description("Off")]
    Off,
    [Description("All Inspections")]
    All,
    [Description("NG Only")]
    NgOnly,
}

public sealed partial class BoltTrainingSettings : ObservableObject
{
    [ObservableProperty]
    private InspectionImageCollection _imageCollection;
    [ObservableProperty]
    private int _maxEpochs = 50;
    [ObservableProperty]
    private int _batchSize = 8;
    [ObservableProperty]
    private double _learningRate = 0.001;
    [ObservableProperty]
    private int _patience = 10;
    [ObservableProperty]
    private float _maskThreshold = 0.5f;

    partial void OnMaskThresholdChanging(float value)
    {
        if (!(value >= 0 && value <= 1))
            throw new ArgumentOutOfRangeException(nameof(value), "Use 0 to 1.");
    }

    [JsonIgnore]
    public bool IsValid
    {
        get
        {
            return MaxEpochs > 0
                && BatchSize > 0
                && Patience > 0
                && double.IsFinite(LearningRate)
                && LearningRate > 0;
        }
    }
}
