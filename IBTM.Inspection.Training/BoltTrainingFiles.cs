using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using IBTM.Core;

namespace IBTM.Inspection.Training;

internal static class BoltTrainingFiles
{
    internal const string ImageDirectoryName = "Images";
    internal const string MaskDirectoryName = "Masks";

    public static BitmapSource CreateInput(ImageFrame frame)
    {
        var image = BitmapSource.Create(
            frame.Width,
            frame.Height,
            96,
            96,
            PixelFormats.Bgr24,
            null,
            frame.Pixels,
            frame.Stride);
        var size = IBoltRecessSegmenter.InputSize;
        var crop = new CroppedBitmap(
            image,
            new Int32Rect(
                (image.PixelWidth - size) / 2,
                (image.PixelHeight - size) / 2,
                size,
                size));
        crop.Freeze();
        return crop;
    }

    public static void SaveSample(
        string directory,
        BitmapSource image,
        byte[] mask)
    {
        var imageDirectory = Path.Combine(directory, ImageDirectoryName);
        var maskDirectory = Path.Combine(directory, MaskDirectoryName);
        Directory.CreateDirectory(imageDirectory);
        Directory.CreateDirectory(maskDirectory);

        var name = $"{NextNumber(imageDirectory):D6}.png";
        Save(image, Path.Combine(imageDirectory, name));
        var maskImage = BitmapSource.Create(
            IBoltRecessSegmenter.InputSize,
            IBoltRecessSegmenter.InputSize,
            96,
            96,
            PixelFormats.Gray8,
            null,
            mask,
            IBoltRecessSegmenter.InputSize);
        Save(maskImage, Path.Combine(maskDirectory, name));
    }

    public static BitmapSource CreateOverlay(
        BitmapSource source,
        IReadOnlyList<float> mask,
        float threshold)
    {
        var image = BitmapFiles.ToFrame(source);
        var pixels = image.Pixels;
        var size = IBoltRecessSegmenter.InputSize;
        var left = (image.Width - size) / 2;
        var top = (image.Height - size) / 2;
        for (var index = 0; index < mask.Count; index++)
        {
            if (mask[index] < threshold)
            {
                continue;
            }

            var pixel = (top + index / size) * image.Stride
                        + (left + index % size) * ImageFrame.ColorChannelCount;
            pixels[pixel + ImageFrame.BlueChannel] = (byte)(
                (pixels[pixel + ImageFrame.BlueChannel] * 2 + 40) / 3);
            pixels[pixel + ImageFrame.GreenChannel] = (byte)(
                (pixels[pixel + ImageFrame.GreenChannel] * 2 + 90) / 3);
            pixels[pixel + ImageFrame.RedChannel] = (byte)(
                (pixels[pixel + ImageFrame.RedChannel] + 510) / 3);
        }

        var overlay = BitmapSource.Create(
            image.Width,
            image.Height,
            96,
            96,
            PixelFormats.Bgr24,
            null,
            pixels,
            image.Stride);
        overlay.Freeze();
        return overlay;
    }

    private static int NextNumber(string directory) =>
        Directory.GetFiles(directory, "*.png")
            .Select(Path.GetFileNameWithoutExtension)
            .Select(name => int.TryParse(name, out var number) ? number : 0)
            .DefaultIfEmpty()
            .Max() + 1;

    private static void Save(BitmapSource image, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
