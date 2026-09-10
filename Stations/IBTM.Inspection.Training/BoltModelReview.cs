using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IBTM.Core;

namespace IBTM.Inspection.Training;

public enum BoltReviewScope
{
    [Description("Validation Images")]
    Validation,
    [Description("Selected Image")]
    SelectedImage,
}

public enum BoltValidationOutcome
{
    [Description("Match")]
    Match,

    [Description("Mismatch")]
    Mismatch,
}

public partial class BoltModelReview : ObservableObject
{
    private readonly IReadOnlyList<BoltReviewSample> _samples;
    private readonly Func<BoltInspectionRecipe> _getRecipe;
    private readonly BoltTrainingSettings _training;
    private int _index;

    private BoltModelReview(
        IReadOnlyList<BoltReviewSample> samples,
        BoltReviewScope scope,
        Func<BoltInspectionRecipe> getRecipe,
        BoltTrainingSettings training)
    {
        _samples = samples;
        Scope = scope;
        _getRecipe = getRecipe;
        _training = training;
        Show();
    }

    [ObservableProperty]
    private BitmapSource _image = null!;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Sample))]
    [NotifyPropertyChangedFor(nameof(RegionSize))]
    private int _imageNumber;

    public BoltReviewScope Scope { get; }

    public BoltSampleInfo Sample
    {
        get
        {
            return _samples[_index].Info;
        }
    }

    public int RegionSize
    {
        get
        {
            return _samples[_index].RegionSize;
        }
    }

    public int ImageCount
    {
        get
        {
            return _samples.Count;
        }
    }

    public float MaskThreshold
    {
        get
        {
            return _training.MaskThreshold;
        }
    }

    public double MinimumMaskPercent
    {
        get
        {
            return _getRecipe().MinimumMaskRatio * 100;
        }
    }

    [ObservableProperty]
    private double _maskRatioPercent;

    [ObservableProperty]
    private AssemblyResult _result;

    [ObservableProperty]
    private AssemblyResult? _expected;

    [ObservableProperty]
    private BoltValidationOutcome? _outcome;

    [ObservableProperty]
    private int _correctCount;

    internal static BoltModelReview Load(
        byte[] weights,
        IReadOnlyList<BoltTrainingInput> validation,
        Func<BoltInspectionRecipe> getRecipe,
        BoltTrainingSettings training,
        CancellationToken cancellationToken)
    {
        var samples = new List<BoltReviewSample>();
        using var segmenter = new TorchBoltRecessSegmenter(weights);
        foreach (var input in validation)
        {
            cancellationToken.ThrowIfCancellationRequested();
            samples.Add(
                Predict(input.Sample, input.Sample.RegionSize, input.Image, segmenter, cancellationToken));
        }

        return new BoltModelReview(samples, BoltReviewScope.Validation, getRecipe, training);
    }

    internal static BoltModelReview LoadImage(
        byte[] weights,
        BoltSampleInfo sample,
        ImageFrame original,
        int regionSize,
        Func<BoltInspectionRecipe> getRecipe,
        BoltTrainingSettings training,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var input = BoltImageInput.Create(original, regionSize);
        using var segmenter = new TorchBoltRecessSegmenter(weights);
        return new BoltModelReview(
            [Predict(sample, regionSize, input, segmenter, cancellationToken)],
            BoltReviewScope.SelectedImage,
            getRecipe,
            training);
    }

    private static BoltReviewSample Predict(
        BoltSampleInfo sample,
        int regionSize,
        ImageFrame input,
        IBoltRecessSegmenter segmenter,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var mask = segmenter.Segment(input);
        token.ThrowIfCancellationRequested();
        return new(sample, regionSize, BoltTrainingImages.Create(input), new BoltPrediction(input, mask));
    }

    [RelayCommand(CanExecute = nameof(CanPrevious))]
    private void Previous()
    {
        _index--;
        Show();
    }

    [RelayCommand(CanExecute = nameof(CanNext))]
    private void Next()
    {
        _index++;
        Show();
    }

    private bool CanPrevious()
    {
        return _index > 0;
    }

    private bool CanNext()
    {
        return _index < _samples.Count - 1;
    }

    private void Show()
    {
        ImageNumber = _index + 1;
        Refresh();
        PreviousCommand.NotifyCanExecuteChanged();
        NextCommand.NotifyCanExecuteChanged();
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(MaskThreshold));
        OnPropertyChanged(nameof(MinimumMaskPercent));
        RefreshJudgments();
        var sample = _samples[_index];
        Image = BoltTrainingImages.CreateOverlay(
            sample.Image,
            sample.Prediction.Probabilities,
            MaskThreshold);
    }

    private void RefreshJudgments()
    {
        var recipe = _getRecipe();
        var ratio = _samples[_index].Prediction.MaskRatio(MaskThreshold);
        MaskRatioPercent = ratio * 100;
        Result = ratio >= recipe.MinimumMaskRatio ? AssemblyResult.Ok : AssemblyResult.Ng;
        Expected = Sample.Label switch
        {
            BoltLabel.Bolt => AssemblyResult.Ok,
            BoltLabel.Empty => AssemblyResult.Ng,
            _ => null,
        };
        Outcome = Expected is null
            ? null
            : Result == Expected ? BoltValidationOutcome.Match : BoltValidationOutcome.Mismatch;
        CorrectCount = _samples.Count(
            value =>
                value.Info.Label != BoltLabel.Unlabeled
                    && (value.Prediction.MaskRatio(MaskThreshold) >= recipe.MinimumMaskRatio) == (value.Info.Label == BoltLabel.Bolt));
    }
}

internal sealed record BoltReviewSample(
    BoltSampleInfo Info,
    int RegionSize,
    BitmapSource Image,
    BoltPrediction Prediction);
