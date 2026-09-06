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

internal sealed class TinyUnetTrainer(int batchSize = 8)
{
    private const double LearningRate = 0.001;

    public void Train(
        BoltSegmentationDataset dataset,
        string modelFile,
        int epochs,
        IProgress<BoltTrainingProgress> progress,
        CancellationToken cancellationToken)
    {
        using var model = new TinyUnet();
        using var optimizer = optim.Adam(model.parameters(), LearningRate);
        using var positiveWeight = tensor(dataset.PositiveWeight);
        var bestLoss = double.PositiveInfinity;

        for (var epoch = 1; epoch <= epochs; epoch++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            model.train();
            var trainingLoss = Run(
                model,
                dataset.Training,
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
                    positiveWeight,
                    cancellationToken);
            }

            if (validationLoss < bestLoss)
            {
                bestLoss = validationLoss;
                Directory.CreateDirectory(
                    Path.GetDirectoryName(modelFile)
                    ?? Directory.GetCurrentDirectory());
                model.save(modelFile);
            }

            progress.Report(new BoltTrainingProgress(
                epoch,
                trainingLoss,
                validationLoss));
        }
    }

    private double Run(
        TinyUnet model,
        IReadOnlyList<BoltSample> samples,
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
            var (images, masks) = CreateBatch(
                samples,
                indices,
                offset,
                count);
            var input = tensor(images, dtype: ScalarType.Float32)
                .reshape(
                    count,
                    ImageFrame.ColorChannelCount,
                    IBoltRecessSegmenter.InputSize,
                    IBoltRecessSegmenter.InputSize);
            var target = tensor(masks, dtype: ScalarType.Float32)
                .reshape(
                    count,
                    1,
                    IBoltRecessSegmenter.InputSize,
                    IBoltRecessSegmenter.InputSize);
            var logits = model.call(input);
            var loss = binary_cross_entropy_with_logits(
                logits,
                target,
                pos_weights: positiveWeight);

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
        IReadOnlyList<BoltSample> samples,
        IReadOnlyList<int> indices,
        int offset,
        int count)
    {
        var pixels =
            IBoltRecessSegmenter.InputSize * IBoltRecessSegmenter.InputSize;
        var images = new float[
            count * ImageFrame.ColorChannelCount * pixels];
        var masks = new float[count * pixels];

        for (var index = 0; index < count; index++)
        {
            var sample = samples[indices[offset + index]];
            Array.Copy(
                sample.Image,
                0,
                images,
                index * ImageFrame.ColorChannelCount * pixels,
                sample.Image.Length);
            Array.Copy(
                sample.Mask,
                0,
                masks,
                index * pixels,
                sample.Mask.Length);
        }

        return (images, masks);
    }
}

internal readonly record struct BoltTrainingProgress(
    int Epoch,
    double TrainingLoss,
    double ValidationLoss);
