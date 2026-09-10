using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using IBTM.Core;
using IBTM.Inspection;
using TorchSharp;
using static TorchSharp.torch;
using static TorchSharp.torch.nn.functional;

namespace IBTM.Inspection.Training;

internal sealed class TinyUnetTrainer
{
    public BoltTrainedModel Train(
        BoltSegmentationDataset dataset,
        BoltTrainingSettings settings,
        IProgress<BoltTrainingProgress> progress,
        CancellationToken cancellationToken)
    {
        using var model = new TinyUnet();
        using var optimizer = optim.Adam(model.parameters(), settings.LearningRate);
        using var positiveWeight = tensor(dataset.PositiveWeight);
        var bestLoss = double.PositiveInfinity;
        var bestEpoch = 0;
        var completedEpochs = 0;
        byte[] bestWeights = [];

        for (var epoch = 1; epoch <= settings.MaxEpochs; epoch++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            model.train();
            var trainingLoss = Run(
                model,
                dataset.Training,
                settings.BatchSize,
                positiveWeight,
                cancellationToken,
                optimizer);
            model.eval();
            double validationLoss;
            using (no_grad())
            {
                validationLoss = Run(
                    model,
                    dataset.Validation,
                    settings.BatchSize,
                    positiveWeight,
                    cancellationToken);
            }

            if (validationLoss < bestLoss)
            {
                bestLoss = validationLoss;
                bestEpoch = epoch;
                using var stream = new MemoryStream();
                model.save(stream);
                bestWeights = stream.ToArray();
            }

            completedEpochs = epoch;
            progress.Report(new BoltTrainingProgress(epoch, bestEpoch, trainingLoss, validationLoss));
            if (epoch - bestEpoch >= settings.Patience)
                break;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new BoltTrainedModel(bestWeights, completedEpochs, bestLoss);
    }

    private double Run(
        TinyUnet model,
        IReadOnlyList<BoltTrainingInput> samples,
        int batchSize,
        Tensor positiveWeight,
        CancellationToken cancellationToken,
        optim.Optimizer? optimizer = null)
    {
        var indices = new int[samples.Count];
        for (var index = 0; index < indices.Length; index++)
        {
            indices[index] = index;
        }

        if (optimizer is not null)
        {
            Random.Shared.Shuffle(indices);
        }

        var totalLoss = 0d;
        for (var offset = 0; offset < indices.Length; offset += batchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var scope = NewDisposeScope();
            var count = Math.Min(batchSize, indices.Length - offset);
            var (images, masks) = CreateBatch(samples, indices, offset, count, cancellationToken);
            var input = tensor(images, dtype: ScalarType.Float32)
                .reshape(
                    count,
                    ImageFrame.ColorChannelCount,
                    IBoltRecessSegmenter.InputSize,
                    IBoltRecessSegmenter.InputSize);
            var target = tensor(masks, dtype: ScalarType.Float32)
                .reshape(count, 1, IBoltRecessSegmenter.InputSize, IBoltRecessSegmenter.InputSize);
            var logits = model.call(input);
            var loss = binary_cross_entropy_with_logits(logits, target, pos_weights: positiveWeight);

            if (optimizer is not null)
            {
                optimizer.zero_grad();
                loss.backward();
                optimizer.step();
            }

            totalLoss += loss.item<float>() * count;
        }

        return totalLoss / samples.Count;
    }

    private static (float[] Images, float[] Masks) CreateBatch(
        IReadOnlyList<BoltTrainingInput> samples,
        IReadOnlyList<int> indices,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        var pixels = IBoltRecessSegmenter.InputSize * IBoltRecessSegmenter.InputSize;
        var images = new float[count * ImageFrame.ColorChannelCount * pixels];
        var masks = new float[count * pixels];

        for (var index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sample = samples[indices[offset + index]];
            var image = TorchBoltRecessSegmenter.CreateInput(sample.Image);
            var mask = sample.Mask;
            Array.Copy(image, 0, images, index * ImageFrame.ColorChannelCount * pixels, image.Length);
            for (var pixel = 0; pixel < pixels; pixel++)
                masks[index * pixels + pixel] = mask[pixel] / (float)byte.MaxValue;
        }

        return (images, masks);
    }
}

internal readonly record struct BoltTrainingProgress(
    int Epoch,
    int BestEpoch,
    double TrainingLoss,
    double ValidationLoss);
