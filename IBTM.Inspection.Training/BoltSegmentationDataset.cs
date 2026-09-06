using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using IBTM.Core;

namespace IBTM.Inspection.Training;

internal sealed class BoltSegmentationDataset
{
    private const int MinimumSampleCount = 5;
    private const int MinimumSamplesPerClass = 2;
    private const int ValidationInterval = 5;
    private const byte MaskThreshold = 128;

    internal IReadOnlyList<BoltSample> Training { get; }
    internal IReadOnlyList<BoltSample> Validation { get; }
    internal float PositiveWeight { get; }

    public BoltSegmentationDataset(
        string directory,
        CancellationToken cancellationToken = default)
    {
        var imageDirectory = Path.Combine(
            directory,
            BoltTrainingFiles.ImageDirectoryName);
        var maskDirectory = Path.Combine(
            directory,
            BoltTrainingFiles.MaskDirectoryName);
        var samples = new List<BoltSample>();
        foreach (var path in Directory.GetFiles(imageDirectory)
                     .Order(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            samples.Add(Load(
                path,
                Path.Combine(maskDirectory, Path.GetFileName(path))));
        }

        if (samples.Count < MinimumSampleCount)
        {
            throw new InvalidDataException(
                "At least five image/mask pairs are required.");
        }

        var boltSamples = samples.Where(sample => sample.BoltPresent).ToArray();
        var emptySamples = samples.Where(sample => !sample.BoltPresent).ToArray();
        if (boltSamples.Length < MinimumSamplesPerClass
            || emptySamples.Length < MinimumSamplesPerClass)
        {
            throw new InvalidDataException(
                "At least two bolt images and two empty-hole images are required.");
        }

        Validation = ValidationSamples(boltSamples)
            .Concat(ValidationSamples(emptySamples))
            .OrderBy(sample => sample.ImagePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Training = TrainingSamples(boltSamples)
            .Concat(TrainingSamples(emptySamples))
            .OrderBy(sample => sample.ImagePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var positive = Training.Sum(sample =>
            sample.Mask.Count(value => value > 0));
        if (positive == 0)
        {
            throw new InvalidDataException(
                "Training masks contain no white bolt-recess pixels.");
        }

        var pixels = Training.Sum(sample => (long)sample.Mask.Length);
        PositiveWeight = (float)(pixels - positive) / positive;
    }

    private static BoltSample Load(
        string imagePath,
        string maskPath)
    {
        var image = BitmapFiles.LoadFrame(imagePath);
        var maskImage = BitmapFiles.LoadFrame(maskPath);
        var input = TorchBoltRecessSegmenter.CreateInput(image);
        var size = IBoltRecessSegmenter.InputSize;
        var mask = new float[size * size];
        var maskLeft = (maskImage.Width - size) / 2;
        var maskTop = (maskImage.Height - size) / 2;

        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var maskSource = ((maskTop + y) * maskImage.Stride)
                                 + ((maskLeft + x) * ImageFrame.ColorChannelCount);
                var target = (y * size) + x;
                mask[target] = maskImage.Pixels[maskSource] >= MaskThreshold
                    ? 1f
                    : 0f;
            }
        }

        return new BoltSample(imagePath, input, mask);
    }

    private static IEnumerable<BoltSample> ValidationSamples(
        IReadOnlyList<BoltSample> samples) =>
        samples.Where((_, index) => index % ValidationInterval == 0);

    private static IEnumerable<BoltSample> TrainingSamples(
        IReadOnlyList<BoltSample> samples) =>
        samples.Where((_, index) => index % ValidationInterval != 0);
}

internal sealed record BoltSample(
    string ImagePath,
    float[] Image,
    float[] Mask)
{
    public bool BoltPresent => Mask.Any(value => value > 0);
}
