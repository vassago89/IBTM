using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace IBTM.Inspection.Training;

public enum BoltValidationResult
{
    [Description("Empty")]
    Empty,

    [Description("Bolt")]
    Bolt,
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
    private readonly IReadOnlyList<BoltValidationSample> _samples;
    private readonly BoltInspectionSettings _settings;
    private int _index;

    private BoltModelReview(
        IReadOnlyList<BoltValidationSample> samples,
        BoltInspectionSettings settings)
    {
        _samples = samples;
        _settings = settings;
        ImageCount = samples.Count;
        CalibrateMinimumMaskRatio();
        Show();
    }

    [ObservableProperty]
    private BitmapSource _image = null!;

    [ObservableProperty]
    private int _imageNumber;

    [ObservableProperty]
    private int _imageCount;

    [ObservableProperty]
    private double _maskRatioPercent;

    [ObservableProperty]
    private BoltValidationResult _result;

    [ObservableProperty]
    private BoltValidationResult _expected;

    [ObservableProperty]
    private BoltValidationOutcome _outcome;

    [ObservableProperty]
    private int _correctCount;

    internal static BoltModelReview Load(
        string modelFile,
        IReadOnlyList<BoltSample> validationSamples,
        BoltInspectionSettings settings,
        CancellationToken cancellationToken)
    {
        var samples = new List<BoltValidationSample>();
        using var segmenter = new TorchBoltRecessSegmenter(modelFile);
        foreach (var sample in validationSamples)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var image = BoltTrainingFiles.Load(sample.ImagePath);
            samples.Add(new BoltValidationSample(
                image,
                segmenter.Segment(BoltTrainingFiles.ToFrame(image)),
                sample.BoltPresent
                    ? BoltValidationResult.Bolt
                    : BoltValidationResult.Empty));
        }

        return new BoltModelReview(samples, settings);
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

    private bool CanPrevious() => _index > 0;
    private bool CanNext() => _index < _samples.Count - 1;

    private void Show()
    {
        ImageNumber = _index + 1;
        Refresh();
        PreviousCommand.NotifyCanExecuteChanged();
        NextCommand.NotifyCanExecuteChanged();
    }

    private void Refresh()
    {
        var sample = _samples[_index];
        var positive = sample.PredictedMask.Count(
            value => value >= _settings.MaskThreshold);
        MaskRatioPercent = (double)positive
                           / sample.PredictedMask.Length
                           * 100;
        Result = MaskRatioPercent >= _settings.MinimumMaskRatio * 100
            ? BoltValidationResult.Bolt
            : BoltValidationResult.Empty;
        Expected = sample.Expected;
        Outcome = Result == Expected
            ? BoltValidationOutcome.Match
            : BoltValidationOutcome.Mismatch;
        CorrectCount = _samples.Count(IsCorrect);
        Image = BoltTrainingFiles.CreateOverlay(
            sample.Image,
            sample.PredictedMask,
            _settings.MaskThreshold);
    }

    private void CalibrateMinimumMaskRatio()
    {
        var scores = _samples
            .Select(sample => new BoltValidationScore(
                MaskRatio(sample.PredictedMask),
                sample.Expected))
            .OrderBy(score => score.Ratio)
            .ToArray();
        var candidates = new[] { 0d }
            .Concat(scores.Zip(scores.Skip(1),
                (left, right) => (left.Ratio + right.Ratio) / 2))
            .Append(Math.BitIncrement(scores[^1].Ratio));

        _settings.MinimumMaskRatio = candidates
            .Select(threshold => new
            {
                Threshold = threshold,
                Accuracy = BalancedAccuracy(scores, threshold),
                Margin = scores.Min(score =>
                    Math.Abs(score.Ratio - threshold)),
            })
            .OrderByDescending(candidate => candidate.Accuracy)
            .ThenByDescending(candidate => candidate.Margin)
            .First()
            .Threshold;
    }

    private double MaskRatio(IReadOnlyList<float> mask) =>
        (double)mask.Count(value => value >= _settings.MaskThreshold)
        / mask.Count;

    private bool IsCorrect(BoltValidationSample sample) =>
        (MaskRatio(sample.PredictedMask) >= _settings.MinimumMaskRatio)
        == (sample.Expected == BoltValidationResult.Bolt);

    private static double BalancedAccuracy(
        IReadOnlyList<BoltValidationScore> scores,
        double threshold)
    {
        var bolts = scores.Where(score =>
            score.Expected == BoltValidationResult.Bolt).ToArray();
        var empty = scores.Where(score =>
            score.Expected == BoltValidationResult.Empty).ToArray();
        var detectedBolts = bolts.Count(score => score.Ratio >= threshold);
        var detectedEmpty = empty.Count(score => score.Ratio < threshold);
        return ((double)detectedBolts / bolts.Length
                + (double)detectedEmpty / empty.Length)
               / 2;
    }
}

internal sealed record BoltValidationSample(
    BitmapSource Image,
    float[] PredictedMask,
    BoltValidationResult Expected);

internal readonly record struct BoltValidationScore(
    double Ratio,
    BoltValidationResult Expected);
