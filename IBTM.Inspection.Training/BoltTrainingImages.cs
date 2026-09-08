using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using IBTM.Core;

namespace IBTM.Inspection.Training;

public static class BoltTrainingImages
{
    internal static byte[] Encode(ImageFrame image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(Create(image)));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    public static ImageFrame Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var image = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad).Frames[0];
        return ToFrame(image);
    }

    internal static BitmapSource Create(ImageFrame image)
    {
        var bitmap = BitmapSource.Create(image.Width, image.Height, 96, 96,
            PixelFormats.Bgr24, null, image.Pixels, image.Stride);
        bitmap.Freeze();
        return bitmap;
    }

    internal static Rect Region(BitmapSource source, int size) => new(
        (source.PixelWidth - size) / 2, (source.PixelHeight - size) / 2, size, size);

    public static ImageFrame ToFrame(BitmapSource source)
    {
        var image = new FormatConvertedBitmap(source, PixelFormats.Bgr24, null, 0);
        var stride = image.PixelWidth * ImageFrame.ColorChannelCount;
        var pixels = new byte[stride * image.PixelHeight];
        image.CopyPixels(pixels, stride, 0);
        return new ImageFrame(image.PixelWidth, image.PixelHeight, stride, pixels);
    }

    public static BitmapSource CreateOverlay(
        BitmapSource source,
        IReadOnlyList<float> mask,
        float threshold)
    {
        var image = ToFrame(source);
        var pixels = image.Pixels;
        var size = IBoltRecessSegmenter.InputSize;
        for (var index = 0; index < mask.Count; index++)
        {
            if (mask[index] < threshold)
            {
                continue;
            }

            var pixel = index / size * image.Stride
                        + index % size * ImageFrame.ColorChannelCount;
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
}
