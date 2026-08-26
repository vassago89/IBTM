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
    private const string Images = "Images";
    private const string Masks = "Masks";

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
        var imageDirectory = Path.Combine(directory, Images);
        var maskDirectory = Path.Combine(directory, Masks);
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

    public static ImageFrame ToFrame(BitmapSource source)
    {
        var image = new FormatConvertedBitmap(
            source,
            PixelFormats.Bgr24,
            null,
            0);
        var stride = image.PixelWidth * 3;
        var pixels = new byte[stride * image.PixelHeight];
        image.CopyPixels(pixels, stride, 0);
        return new ImageFrame(
            image.PixelWidth,
            image.PixelHeight,
            stride,
            pixels);
    }

    public static BitmapSource CreateOverlay(
        BitmapSource source,
        IReadOnlyList<float> mask,
        float threshold)
    {
        var image = ToFrame(source);
        var pixels = (byte[])image.Pixels.Clone();
        for (var index = 0; index < mask.Count; index++)
        {
            if (mask[index] < threshold)
            {
                continue;
            }

            var pixel = index * 3;
            pixels[pixel] = (byte)((pixels[pixel] * 2 + 40) / 3);
            pixels[pixel + 1] = (byte)((pixels[pixel + 1] * 2 + 90) / 3);
            pixels[pixel + 2] = (byte)((pixels[pixel + 2] + 510) / 3);
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

    public static BitmapSource Load(string path)
    {
        using var stream = File.OpenRead(path);
        var image = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad).Frames[0];
        image.Freeze();
        return image;
    }

    private static void Save(BitmapSource image, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
