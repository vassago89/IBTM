using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using IBTM.Core;

namespace IBTM.Inspection.Training;

internal sealed class BoltSegmentationDataset
{
    internal IReadOnlyList<BoltTrainingInput> Training { get; }
    internal IReadOnlyList<BoltTrainingInput> Validation { get; }
    internal float PositiveWeight { get; }

    internal BoltSegmentationDataset(BoltTrainingStore store, CancellationToken cancellationToken)
    {
        var samples = store.GetSamples().Where(sample => sample.Included && sample.Label != BoltLabel.Unlabeled).ToArray();
        var training = samples.Where(sample => sample.Use == BoltSampleUse.Training).ToArray();
        var validation = samples.Where(sample => sample.Use == BoltSampleUse.Validation).ToArray();
        if (training.Length == 0 || validation.Length == 0)
            throw new InvalidDataException("Include at least one labeled training image and one labeled validation image.");

        Training = Prepare(training, store, cancellationToken);
        Validation = Prepare(validation, store, cancellationToken);
        var positive = Training.Sum(sample => sample.Mask.LongCount(value => value > 0));
        if (positive == 0) throw new InvalidDataException("Training polygons contain no bolt-recess pixels.");
        var pixels = (long)Training.Count * IBoltRecessSegmenter.InputSize * IBoltRecessSegmenter.InputSize;
        PositiveWeight = (float)(pixels - positive) / positive;
    }

    internal static IReadOnlyList<BoltTrainingInput> LoadValidation(
        BoltTrainingStore store, CancellationToken token)
    {
        var samples = store.GetSamples().Where(sample => sample.Included
            && sample.Label != BoltLabel.Unlabeled && sample.Use == BoltSampleUse.Validation).ToArray();
        if (samples.Length == 0)
            throw new InvalidDataException("Include at least one labeled validation image to review the saved model.");

        return Prepare(samples, store, token);
    }

    private static BoltTrainingInput[] Prepare(BoltSampleInfo[] samples,
        BoltTrainingStore store, CancellationToken token)
    {
        var inputs = new BoltTrainingInput[samples.Length];
        for (var index = 0; index < samples.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            var sample = samples[index];
            var image = BoltImageInput.Create(store.LoadImage(sample.Id), sample.RegionSize);
            inputs[index] = new(sample, image, BoltPolygon.Mask(sample.Polygon));
        }
        token.ThrowIfCancellationRequested();
        return inputs;
    }
}

// Training-session data only: 128×128 BGR + byte mask, not full originals or persistent tensors.
internal sealed record BoltTrainingInput(BoltSampleInfo Sample, ImageFrame Image, byte[] Mask);
