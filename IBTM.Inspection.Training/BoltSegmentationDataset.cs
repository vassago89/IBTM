using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IBTM.Inspection.Training;

public sealed class BoltSegmentationDataset
{
    internal IReadOnlyList<BoltSample> Training { get; }
    internal IReadOnlyList<BoltSample> Validation { get; }
    internal float PositiveWeight { get; }

    public int TrainingCount => Training.Count;
    public int ValidationCount => Validation.Count;

    public BoltSegmentationDataset(
        string directory,
        int size,
        CancellationToken cancellationToken = default)
    {
        var imageDirectory = Path.Combine(directory, "Images");
        var maskDirectory = Path.Combine(directory, "Masks");
        var samples = new List<BoltSample>();
        foreach (var path in Directory.GetFiles(imageDirectory)
                     .Order(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            samples.Add(Load(
                path,
                Path.Combine(maskDirectory, Path.GetFileName(path)),
                size));
        }

        if (samples.Count < 5)
        {
            throw new InvalidDataException(
                "At least five image/mask pairs are required.");
        }

        var boltSamples = samples.Where(sample => sample.BoltPresent).ToArray();
        var emptySamples = samples.Where(sample => !sample.BoltPresent).ToArray();
        if (boltSamples.Length < 2 || emptySamples.Length < 2)
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
        string maskPath,
        int size)
    {
        var (imagePixels, imageWidth, imageHeight, imageStride) =
            LoadPixels(imagePath);
        var (maskPixels, maskWidth, maskHeight, maskStride) =
            LoadPixels(maskPath);
        var input = new float[3 * size * size];
        var mask = new float[size * size];
        var plane = size * size;
        var imageLeft = (imageWidth - size) / 2;
        var imageTop = (imageHeight - size) / 2;
        var maskLeft = (maskWidth - size) / 2;
        var maskTop = (maskHeight - size) / 2;

        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var imageSource = ((imageTop + y) * imageStride)
                                  + ((imageLeft + x) * 3);
                var maskSource = ((maskTop + y) * maskStride)
                                 + ((maskLeft + x) * 3);
                var target = (y * size) + x;
                input[target] = imagePixels[imageSource + 2] / 255f;
                input[plane + target] = imagePixels[imageSource + 1] / 255f;
                input[(2 * plane) + target] = imagePixels[imageSource] / 255f;
                mask[target] = maskPixels[maskSource] >= 128 ? 1f : 0f;
            }
        }

        return new BoltSample(imagePath, input, mask);
    }

    private static IEnumerable<BoltSample> ValidationSamples(
        IReadOnlyList<BoltSample> samples) =>
        samples.Where((_, index) => index % 5 == 0);

    private static IEnumerable<BoltSample> TrainingSamples(
        IReadOnlyList<BoltSample> samples) =>
        samples.Where((_, index) => index % 5 != 0);

    private static (byte[] Pixels, int Width, int Height, int Stride)
        LoadPixels(string path)
    {
        using var stream = File.OpenRead(path);
        var frame = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad).Frames[0];
        var image = new FormatConvertedBitmap(
            frame,
            PixelFormats.Bgr24,
            null,
            0);
        var stride = image.PixelWidth * 3;
        var pixels = new byte[stride * image.PixelHeight];
        image.CopyPixels(pixels, stride, 0);
        return (pixels, image.PixelWidth, image.PixelHeight, stride);
    }
}

internal sealed record BoltSample(
    string ImagePath,
    float[] Image,
    float[] Mask)
{
    public bool BoltPresent => Mask.Any(value => value > 0);
}
